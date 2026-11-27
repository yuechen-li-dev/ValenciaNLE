using System.Collections.Immutable;
using System.Text.Json;
using Valencia.Analysis;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Persistence;
using Xunit;
using static Valencia.Testing.TestProject;

namespace Valencia.Programming.Tests;

public sealed class AgentInterfaceTests
{
    [Theory]
    [InlineData(30000, "00:01:00;02", 1800)]
    [InlineData(30000, "00:10:00;00", 17982)]
    [InlineData(60000, "00:01:00;04", 3600)]
    [InlineData(60000, "00:10:00;00", 35964)]
    [InlineData(30000, "25:00:05;08", 2697458)]
    public void DropFrameRoundTripsWithoutWrappingOrFloatingPoint(int numerator, string code, long frame)
    {
        var rate = new FrameRate(numerator, 1001);
        var resolved = AgentTime.Position(code, rate);
        Assert.Equal(frame, TimelineTime.FrameIndex(resolved, rate, TimeRounding.Exact));
        Assert.Equal(code, AgentTime.Describe(resolved, "sequence", rate).Timecode);
        Assert.Equal(resolved, AgentTime.Position($"f{frame}", rate));
    }

    [Fact]
    public void TimeRejectsAmbiguityAndKeepsExactIntegersAndDecimalRules()
    {
        var rate = new FrameRate(30000, 1001);
        Assert.Equal(9_007_199_254_740_993L, AgentTime.Position("9007199254740993").Ticks);
        Assert.Equal(62_500L * (SequenceTime.TicksPerSecond / 1000), AgentTime.Position("00:01:02.500").Ticks);
        Assert.Equal(AgentTime.Position("62.5s"), AgentTime.Position("00:01:02.500"));
        Assert.Equal(-1, AgentTime.Duration("-0.000000000003s", signed: true).Ticks);
        Assert.Equal(long.MinValue, AgentTime.Duration("-9223372036854775808", signed: true).Ticks);
        Assert.Equal("INVALID_TIMECODE", Assert.Throws<AgentRequestException>(() => AgentTime.Position("00:01:00;00", rate)).Issue.Code);
        Assert.Equal("TIME_CONTEXT", Assert.Throws<AgentRequestException>(() => AgentTime.Position("f12")).Issue.Code);
        Assert.Equal("TIME_CONTEXT", Assert.Throws<AgentRequestException>(() => AgentTime.Duration("00:01:00:01", rate)).Issue.Code);
        Assert.Equal("TIME_CONTEXT", Assert.Throws<AgentRequestException>(() => AgentTime.Position("marker:\"Cut\"", source: true)).Issue.Code);
        Assert.Equal("TIME_GRID", Assert.Throws<AgentRequestException>(() => AgentTime.Position("f1", new(29, 1))).Issue.Code);
    }

    [Fact]
    public async Task RolesHandlesCandidateCheckpointAndHistorySurviveTheCorrectBoundaries()
    {
        var session = Create();
        var one = new SequenceMarker(Id(80), T(1), "Opening");
        var two = new SequenceMarker(Id(81), T(3), "opening");
        Assert.True(Apply(session, new SetSequenceMarker(SequenceId, one)).Success);
        Assert.True(Apply(session, new SetSequenceMarker(SequenceId, two)).Success);
        Assert.Equal("AMBIGUOUS_REFERENCE", Assert.Throws<AgentRequestException>(() =>
            AgentTime.Position("marker:\"Opening\"", sequence: Query(session).Sequence(SequenceId))).Issue.Code);
        Assert.Equal(T(2), AgentTime.Position("marker:" + AgentHandles.For("marker", one.Id) + "+1s", sequence: Query(session).Sequence(SequenceId)));
        await using var engine = new EditingEngine(session);
        var baseline = session.Snapshot;
        string sequence = AgentHandles.For("sequence", SequenceId);
        string clip = AgentHandles.For("clip", ClipId);
        string target = AgentHandles.For("track", Video2);
        Assert.Equal(24, clip.Length);

        var checkpoint = await Call("agent_checkpoint", new { action = "create", name = "Original" });
        Assert.True(checkpoint.GetProperty("ok").GetBoolean());
        var failed = await Call("agent_plan", new { action = "prepare", plan = new { sequence, operations =
            new[] { new { operation = "split", clipId = clip, at = "0s", rightClipId = Id(90) } } } });
        Assert.Equal("SPLIT_OUTSIDE_CLIP", failed.GetProperty("issue").GetProperty("code").GetString());
        Assert.Equal(clip, failed.GetProperty("issue").GetProperty("subject").GetString());

        var proposal = await Call("agent_plan", new { action = "prepare", plan = new { sequence, operations = new object[]
        {
            new { operation = "move", clipId = clip, trackId = target, start = "marker:" + AgentHandles.For("marker", one.Id) + "+1s" },
            new { operation = "trackRole", trackId = target, role = "broll" },
            new { operation = "trackRole", trackId = AgentHandles.For("track", Video1), role = "broll" },
            new { operation = "assetRole", assetId = AgentHandles.For("asset", AssetId), role = "game" }
        } } });
        Assert.True(proposal.GetProperty("ok").GetBoolean(), proposal.GetRawText());
        string candidate = proposal.GetProperty("result").GetProperty("candidate").GetString()!;
        Assert.Equal(1, proposal.GetProperty("result").GetProperty("summary").GetProperty("clips").GetProperty("moved").GetInt32());
        Assert.Same(baseline, session.Snapshot);
        var alternate = await Call("agent_plan", new { action = "prepare", plan = new { sequence, operations = new[]
            { new { operation = "trim", clipId = clip, start = "0s", sourceIn = "1s", sourceOut = "9s" } } } });
        Assert.True(alternate.GetProperty("ok").GetBoolean(), alternate.GetRawText());
        var compared = await Call("agent_plan", new { action = "compare", candidate,
            other = alternate.GetProperty("result").GetProperty("candidate").GetString() });
        Assert.True(compared.GetProperty("ok").GetBoolean(), compared.GetRawText());
        Assert.True((await Call("agent_plan", new { action = "commit", candidate })).GetProperty("ok").GetBoolean());
        Assert.Equal(T(2), Query(session).Sequence(SequenceId).Tracks[1].Clips[0].Start);
        Assert.Equal("STALE_REVISION", (await Call("agent_plan", new { action = "commit", candidate })).GetProperty("issue").GetProperty("code").GetString());

        var roles = await Call("edit_objects", new { kind = "track", role = "broll", sequence });
        Assert.Equal(2, roles.GetProperty("result").GetProperty("total").GetInt32());
        using var folder = new ScratchFolder();
        string save = Path.Combine(folder.Path, "test.valencia");
        Assert.True((await Call("agent_history", new { action = "save", output = save })).GetProperty("ok").GetBoolean());
        var loaded = await new ProjectFile().LoadAsync(save);
        Assert.Equal(clip, AgentHandles.For("clip", loaded.Snapshot.Document.Sequences[0].Tracks[1].Clips[0].Id));
        Assert.Equal("game", loaded.Snapshot.Document.Assets[0].Role);
        Assert.Equal("broll", loaded.Snapshot.Document.Sequences[0].Tracks[1].Role);
        Assert.True((await Call("agent_history", new { action = "undo" })).GetProperty("ok").GetBoolean());
        Assert.True(DocumentSemantics.Equal(baseline.Document, session.Snapshot.Document));
        Assert.True((await Call("agent_history", new { action = "redo" })).GetProperty("ok").GetBoolean());
        Assert.True((await Call("agent_checkpoint", new { action = "restore", name = "original" })).GetProperty("ok").GetBoolean());
        Assert.True(DocumentSemantics.Equal(baseline.Document, session.Snapshot.Document));
        Assert.True((await Call("agent_history", new { action = "undo" })).GetProperty("ok").GetBoolean());
        Assert.Equal("game", session.Snapshot.Document.Assets[0].Role);

        async Task<JsonElement> Call(string method, object body)
        {
            var fields = JsonSerializer.SerializeToNode(body, EngineJson.Options)!.AsObject();
            fields["projectId"] = ProjectId.ToString();
            fields["revision"] = EngineJson.Exact(session.Snapshot.Revision);
            return JsonSerializer.SerializeToElement(await engine.AgentAsync(method, JsonSerializer.SerializeToElement(fields)), EngineJson.Options);
        }
    }

    [Fact]
    public async Task LogicalFootagePagesUseActualCacheAndReportVersionDrift()
    {
        using var folder = new ScratchFolder();
        string media = Path.Combine(folder.Path, "audio.fixture");
        await File.WriteAllTextAsync(media, "fixture");
        var session = Create(path: media);
        Assert.True(Apply(session, new InsertClip(SequenceId, Audio,
            new(Id(112), AssetId, 1, T(1), new(new(0, 1, 48000)), new(new(240000, 1, 48000)), new()))).Success);
        Assert.True(Apply(session, new SetTrackRole(SequenceId, Audio, "mic")).Success);
        await using var engine = new EditingEngine(session, analysisDirectory: Path.Combine(folder.Path, "analysis"));
        string fingerprint = AnalysisCache.Fingerprint(media);
        var origin = new MediaTimestamp(0, 1, 48000);
        SourceRange Range(int t) => new(new SourcePosition(origin).Shift(T(t)), new SourcePosition(origin).Shift(T(t + 1)), 0);
        string key = new('A', 64);
        string scenekey = new('B', 64);
        var audio = new AnalysisArtifact(1, key, AssetId, fingerprint, 1, "audio", "fixture", "{}", "fixture", origin, "complete", 48000, 1,
            0, [], [Range(1), Range(3)], [new(Range(0), "active", .3), new(Range(2), "active", .8), new(Range(4), "active", .2)], [], []);
        await engine.Analysis.Cache.PublishAsync(audio, default);
        await engine.Analysis.Cache.PublishAsync(audio with { Key = scenekey, Kind = "scenes", StreamIndex = 0,
            Activity = [], Silence = [], Scenes = [new(new(1, 1, 1), .9, 0)] }, default);
        var first = await QueryPage(new { kind = "activity", limit = 1, role = "mic", space = "sequence", minimumRms = .2 });
        Assert.True(first.GetProperty("ok").GetBoolean(), first.GetRawText());
        var page = first.GetProperty("result");
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal("254016000000", page.GetProperty("items")[0].GetProperty("sequence").GetProperty("start").GetProperty("ticks").GetString());
        string set = page.GetProperty("artifactSet").GetString()!;
        var second = await QueryPage(new { kind = "activity", limit = 1, role = "mic", space = "sequence", minimumRms = .2, offset = 1, artifactSet = set });
        Assert.Equal(.8, second.GetProperty("result").GetProperty("items")[0].GetProperty("value").GetDouble());
        var near = await QueryPage(new { kind = "activity", stream = 1, nearScene = "100ms", artifacts = new[] { key, scenekey } });
        Assert.True(near.GetProperty("ok").GetBoolean());
        Assert.Equal(1, near.GetProperty("result").GetProperty("total").GetInt32());
        await engine.Analysis.Cache.PublishAsync(audio with { Diagnostics = ImmutableArray.Create("new payload") }, default);
        var outdated = await QueryPage(new { kind = "activity", limit = 1, role = "mic", space = "sequence", minimumRms = .2, offset = 1, artifactSet = set });
        Assert.Equal("ARTIFACT_SET_CHANGED", outdated.GetProperty("issue").GetProperty("code").GetString());

        async Task<JsonElement> QueryPage(object filter)
        {
            var fields = JsonSerializer.SerializeToNode(filter)!.AsObject();
            fields["projectId"] = ProjectId.ToString();
            fields["revision"] = EngineJson.Exact(session.Snapshot.Revision);
            return JsonSerializer.SerializeToElement(await engine.AgentAsync("footage_query", JsonSerializer.SerializeToElement(fields)), EngineJson.Options);
        }
    }

    private sealed class ScratchFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("ValenciaAgent-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
