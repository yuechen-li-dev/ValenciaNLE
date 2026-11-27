using System.Text.Json;
using SkiaSharp;
using Valencia.App.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Xunit;

namespace Valencia.Tests;

public sealed class AgentObservationTests(TimelineFixture fixture) : IClassFixture<TimelineFixture>
{
    [Fact]
    public async Task SourceLabelsActualPtsAndPairedCandidateCanCompareTheSameSourceContent()
    {
        var session = new Valencia.Core.Editing.EditingSession(fixture.Snapshot);
        await using var engine = new EditingEngine(session);
        string sequence = AgentHandles.For("sequence", TimelineFixture.Id(4));
        string clip = AgentHandles.For("clip", TimelineFixture.Id(10));
        string asset = AgentHandles.For("asset", TimelineFixture.Id(2));
        string track = AgentHandles.For("track", TimelineFixture.Id(6));
        var source = await Call(engine, "observe", new { mode = "source", asset, stream = 0, at = new[] { "1ms" },
            output = Path.Combine(fixture.Root, "agent-source.png"), maxEdge = 160 });
        Assert.True(source.GetProperty("ok").GetBoolean(), source.GetRawText());
        var sample = source.GetProperty("result").GetProperty("details").GetProperty("samples")[0].GetProperty("metadata");
        Assert.Equal("254016000", sample.GetProperty("requested").GetProperty("ticks").GetString());
        Assert.Equal("0", sample.GetProperty("decoded").GetProperty("ticks").GetString());
        Assert.Equal("0", sample.GetProperty("decodedPts").GetProperty("value").GetString());

        var prepared = await Call(engine, "agent_plan", new { action = "prepare", plan = new { sequence, operations =
            new[] { new { operation = "move", clipId = clip, trackId = track, start = "f30" } } } });
        Assert.True(prepared.GetProperty("ok").GetBoolean(), prepared.GetRawText());
        string candidate = prepared.GetProperty("result").GetProperty("candidate").GetString()!;
        string left = Path.Combine(fixture.Root, "agent-pair-before.png"), right = Path.Combine(fixture.Root, "agent-pair-after.png");
        var pair = await Call(engine, "observe", new { mode = "paired", candidate, correspondence = "content", anchorClip = clip,
            at = new[] { "f5" }, beforeOutput = left, output = right, maxEdge = 160 });
        Assert.True(pair.GetProperty("ok").GetBoolean(), pair.GetRawText());
        JsonElement details = pair.GetProperty("result").GetProperty("details");
        var oldSample = details.GetProperty("before").GetProperty("samples")[0].GetProperty("metadata");
        var newSample = details.GetProperty("after").GetProperty("samples")[0].GetProperty("metadata");
        Assert.Equal("5", oldSample.GetProperty("actual").GetProperty("frame").GetString());
        Assert.Equal("35", newSample.GetProperty("actual").GetProperty("frame").GetString());
        var layer = newSample.GetProperty("layers")[0];
        Assert.Equal(clip, layer.GetProperty("clip").GetString());
        Assert.Equal(track, layer.GetProperty("track").GetString());
        Assert.Equal(oldSample.GetProperty("layers")[0].GetProperty("pts").GetRawText(), layer.GetProperty("pts").GetRawText());
        Assert.Same(fixture.Snapshot, session.Snapshot);
        Assert.False(session.CanUndo);
        using var rendered = SKBitmap.Decode(right);
        Assert.Equal((270, 282), (rendered.Width, rendered.Height));
        Assert.Contains(Enumerable.Range(195, 70).SelectMany(y => Enumerable.Range(7, 220).Select(x => rendered.GetPixel(x, y))),
            pixel => pixel.Red > 190 && pixel.Green > 190 && pixel.Blue > 190);
    }

    [Fact]
    public async Task MixObservationUsesActualAudioAndReportsCutLocations()
    {
        await using var engine = new EditingEngine(new Valencia.Core.Editing.EditingSession(fixture.Snapshot),
            analysisDirectory: Path.Combine(fixture.Root, "cache"));
        string target = Path.Combine(fixture.Root, "agent-mix.png");
        var mix = await Call(engine, "observe", new { mode = "mixAudio", start = "f0", end = "f60", output = target });
        Assert.True(mix.GetProperty("ok").GetBoolean(), mix.GetRawText());
        var details = mix.GetProperty("result").GetProperty("details");
        Assert.InRange(details.GetProperty("waveformBins").GetInt32(), 800, 864);
        Assert.Contains(details.GetProperty("marks").EnumerateArray(), m => m.GetProperty("kind").GetString() == "cut"
            && m.GetProperty("start").GetProperty("ticks").GetString() == EngineJson.Exact(TimelineFixture.F(30).Ticks));
        using var audio = SKBitmap.Decode(target);
        Assert.Equal((864, 233), (audio.Width, audio.Height));
        Assert.Contains(audio.Pixels, pixel => pixel.Blue == 248 && pixel.Green == 195);
    }

    [Fact]
    public async Task NativeDocumentSeesSharedSessionRevisionRolesAndUndoAfterAgentCommit()
    {
        var editor = new EditorDocument();
        editor.Open(fixture.Snapshot, Path.Combine(fixture.Root, "native-agent.valencia"));
        await using var engine = new EditingEngine(editor.Session);
        var proposal = await Call(engine, "agent_plan", new { action = "prepare", plan = new
        {
            sequence = AgentHandles.For("sequence", editor.SequenceId),
            operations = new[] { new { operation = "trackRole", trackId = AgentHandles.For("track", editor.Sequence.Tracks[0].Id), role = "game" } }
        } });
        string candidate = proposal.GetProperty("result").GetProperty("candidate").GetString()!;
        Assert.Null(editor.Sequence.Tracks[0].Role);
        var applied = await Call(engine, "agent_plan", new { action = "commit", candidate });
        Assert.True(applied.GetProperty("ok").GetBoolean(), applied.GetRawText());
        Assert.True(editor.Dirty);
        Assert.Equal("game", editor.Sequence.Tracks[0].Role);
        Assert.Equal(1, editor.Session.Snapshot.Revision);
        Assert.True(editor.History(redo: false).Success);
        Assert.Null(editor.Sequence.Tracks[0].Role);
        Assert.False(editor.Dirty);
    }

    private static async Task<JsonElement> Call(EditingEngine engine, string method, object arguments)
    {
        var obj = JsonSerializer.SerializeToNode(arguments, EngineJson.Options)!.AsObject();
        obj["projectId"] = engine.Session.Snapshot.Document.Id.ToString();
        obj["revision"] = EngineJson.Exact(engine.Session.Snapshot.Revision);
        return JsonSerializer.SerializeToElement(await engine.AgentAsync(method, JsonSerializer.SerializeToElement(obj)), EngineJson.Options);
    }
}
