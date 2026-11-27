using System.Collections.Immutable;
using System.Text.Json;
using Valencia.Analysis;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Persistence;
using Valencia.Scripting;
using Xunit;
using static Valencia.Testing.TestProject;

namespace Valencia.Programming.Tests;

public sealed class EditorialWorkflowTests
{
    [Fact]
    public async Task CopelandTemplatesComposeOneDeterministicCandidateAndOneUndoStep()
    {
        using var directory = new TemporaryDirectory();
        var session = Create();
        var basis = session.Snapshot;
        await using var engine = new EditingEngine(session, analysisDirectory: directory.Path);
        EditorialStep[] steps =
        [
            new("creator-title", new(Start: "1s", End: "2s", Text: "Opening", Subtitle: "Part one")),
            new("make-short", new(Start: "1s", End: "6s", Width: 180, Height: 320, FocalX: .64))
        ];
        var first = await EditorialWorkflows.BuildAsync(engine, basis, steps);
        var second = await EditorialWorkflows.BuildAsync(engine, basis, steps);
        Assert.Same(basis, session.Snapshot);
        Assert.Equal(EngineJson.Write(first.Plan), EngineJson.Write(second.Plan));
        Assert.Equal("card", Assert.IsType<TitlePlan>(first.Plan!.Operations[0]).Style);
        var profile = Assert.Single(Assert.IsType<CreatorPlan>(first.Plan.Operations[1]).Settings.Profiles);
        Assert.Equal(180, profile.Width);
        Assert.Equal(.64, profile.FocalX);
        Assert.Equal(T(1), profile.Start);
        var request = JsonSerializer.SerializeToElement(new { action = "compose", projectId = ProjectId, revision = basis.Revision, steps },
            EngineJson.Options);
        var reply = await EditorialWorkflows.InvokeAsync(engine, request);
        Assert.True(reply.Ok, reply.Issue?.Message);
        var candidate = JsonSerializer.SerializeToElement(reply.Result, EngineJson.Options).GetProperty("preparation")
            .GetProperty("candidate").GetString();
        var commit = JsonSerializer.SerializeToElement(new { action = "commit", projectId = ProjectId, revision = basis.Revision, candidate },
            EngineJson.Options);
        Assert.True((await engine.AgentAsync("agent_plan", commit)).Ok);
        var changed = session.Snapshot;
        Assert.Equal(basis.Revision + 1, changed.Revision);
        Assert.Single(engine.Query().Sequence(SequenceId).Titles);
        Assert.True(engine.Undo(changed.Revision).Success);
        Assert.True(DocumentSemantics.Equal(basis.Document, session.Snapshot.Document));
        var stale = await EditorialWorkflows.InvokeAsync(engine, request);
        Assert.Equal("STALE_REVISION", stale.Issue?.Code);
        Assert.True(engine.Redo(session.Snapshot.Revision).Success);
        string path = Path.Combine(directory.Path, "editorial.valencia");
        await engine.SaveAsync(path);
        Assert.True(DocumentSemantics.Equal(changed.Document, (await new ProjectFile().LoadAsync(path)).Snapshot.Document));
    }

    [Fact]
    public async Task BrollAndSplitEditsPreservePictureBoundariesAndRejectLocks()
    {
        using var directory = new TemporaryDirectory();
        string media = Path.Combine(directory.Path, "source.mkv");
        await File.WriteAllTextAsync(media, "Source identity fixture");
        var session = Create(clip: false, path: media);
        for (int index = 0; index < 2; index++)
        {
            int at = index * 4;
            Assert.True(Apply(session, new InsertClip(SequenceId, Video1, Clip(Id(20 + index), at, at + 1, at + 5))).Success);
            var audio = new TimelineClip(Id(30 + index), AssetId, 1, T(at), AudioTime(at + 1), AudioTime(at + 5), new("Speech"));
            var inserted = Apply(session, new InsertClip(SequenceId, Audio, audio));
            Assert.True(inserted.Success, inserted.Message);
        }
        var basis = session.Snapshot;
        await using var engine = new EditingEngine(session, analysisDirectory: directory.Path);
        foreach (var variant in new[] { (Name: "j-cut", Direction: -1), (Name: "l-cut", Direction: 1) })
        {
            var proposed = await EditorialWorkflows.BuildAsync(engine, basis, variant.Name, new(Start: "4s", Offset: "f5", Track: Audio.ToString()));
            var operations = proposed.Plan!.Operations.Cast<TrimPlan>().ToArray();
            var shifted = operations.Single(x => x.ClipId == Id(31));
            long delta = TimelineTime.FromFrames(5, engine.Query().Sequence(SequenceId).Video.FrameRate).Ticks;
            Assert.Equal(T(4).Ticks + variant.Direction * delta, EngineJson.Int64(shifted.Start));
            Assert.True(engine.Plan(proposed.Plan, false).Valid);
            Assert.Same(basis, session.Snapshot);
        }
        var broll = await EditorialWorkflows.BuildAsync(engine, basis, "insert-broll", new(
            Start: "2s", End: "3s", Track: Video2.ToString(), Asset: AssetId.ToString(), SourceStart: "6s"));
        var insert = Assert.IsType<InsertPlan>(Assert.Single(broll.Plan!.Operations));
        Assert.Equal(Video2, insert.TrackId);
        Assert.True(engine.Plan(broll.Plan, true).Committed);
        Assert.Equal(new[] { T(0), T(4) }, engine.Query().Sequence(SequenceId).Tracks.Single(t => t.Id == Audio).Clips.Select(c => c.Start));
        Assert.True(Apply(session, new SetTrackState(SequenceId, Audio, new(Locked: true))).Success);
        await Assert.ThrowsAsync<ArgumentException>(() => EditorialWorkflows.BuildAsync(engine, session.Snapshot, "j-cut",
            new(Start: "4s", Offset: "f5", Track: Audio.ToString())));
    }

    [Fact]
    public async Task FillersRequireReviewedIdsAndTopicCutsRetainContext()
    {
        using var directory = new TemporaryDirectory();
        string media = Path.Combine(directory.Path, "source.mkv");
        await File.WriteAllTextAsync(media, "Source identity fixture");
        var session = Create(path: media);
        var inserted = Apply(session, new InsertClip(SequenceId, Audio,
            new(Id(30), AssetId, 1, T(0), AudioTime(0), AudioTime(10), new("Speech"))));
        Assert.True(inserted.Success, inserted.Message);
        var basis = session.Snapshot;
        await using var engine = new EditingEngine(session, analysisDirectory: directory.Path);
        var transcript = BuildTranscript(
            ("um", 0, true), ("maybe", 1, true), ("backup", 2, true), ("plan", 3, true),
            ("works", 4, true), ("well", 5, false), ("today", 6, true));
        string fingerprint = AnalysisCache.Fingerprint(media);
        string key = AnalysisCache.Key(AssetId, fingerprint, 1, "fixture", "v1");
        await engine.Analysis.Cache.PublishAsync(new(1, key, AssetId, fingerprint, 1, "transcript", "fixture", "v1",
            "test", new(0, 1, 90000), "complete", 48000, 1, 7, [], [], [], [], [], transcript), default);

        var fillers = await EditorialWorkflows.BuildAsync(engine, basis, "remove-fillers", new(Transcript: key));
        Assert.Null(fillers.Plan);
        var matches = JsonSerializer.SerializeToElement(fillers.Evidence, EngineJson.Options);
        Assert.Equal("um", Assert.Single(matches.EnumerateArray()).GetProperty("vocabulary").GetString());
        var cut = await EditorialWorkflows.BuildAsync(engine, basis, "remove-fillers",
            new(Transcript: key, WordIds: [transcript.Words[0].Id]));
        Assert.NotNull(cut.Plan);
        Assert.True(engine.Plan(cut.Plan, false).Valid);
        await Assert.ThrowsAsync<ArgumentException>(() => EditorialWorkflows.BuildAsync(engine, basis, "remove-fillers",
            new(Transcript: key, WordIds: [transcript.Words[5].Id])));

        var topic = await EditorialWorkflows.BuildAsync(engine, basis, "topic-cut",
            new(Transcript: key, Topics: ["backup plan"], ContextWords: 1, PaddingMs: 0));
        var preview = engine.Plan(topic.Plan!, false);
        Assert.True(preview.Valid);
        Assert.InRange(preview.ResultingDuration!.Value.Ticks, T(3).Ticks, T(4).Ticks + T(1).Ticks / 20);
        Assert.Same(basis, session.Snapshot);
    }

    [Fact]
    public void TopicAndFramingFixturesMeasureBoundariesAndMotion()
    {
        var words = BuildTranscript(("backup", 0, true), ("archives", 1, true),
            ("camera", 8, true), ("exposure", 9, true)).Words;
        var transcript = BuildTranscript() with
        {
            Words = words,
            Segments =
            [
                new(Id(100), "backup archives", new(S(0), S(2), 0), 0, 2),
                new(Id(101), "camera exposure", new(S(8), S(10), 0), 2, 2)
            ]
        };
        var topics = TranscriptTopics.Build(transcript);
        Assert.Equal(new[] { S(0), S(8) }, topics.Select(topic => topic.Range.Start));
        Assert.Contains("exposure", topics[1].Keywords!.Value);
        Assert.Contains("gap", topics[1].Evidence);

        var keys = SubjectFraming.Build(T(0), T(10), [new(T(1), .99, .5), new(T(2), .95, .5)], .5, .5);
        Assert.InRange(keys[1].X, .71, .73);
        Assert.InRange(keys[2].X, .93, .95);
        Assert.Equal(keys[2].X, keys[3].X);
        Assert.Equal(.5, keys[^1].X, 6);
    }

    private static TranscriptData BuildTranscript(params (string Word, int At, bool Usable)[] items)
    {
        ImmutableArray<TranscriptWord> words = [.. items.Select((word, index) =>
            new TranscriptWord(Id(200 + index), word.Word, new(S(word.At), S(word.At + 1), 0), T(word.At).Ticks,
                T(word.At + 1).Ticks, .9, word.Usable))];
        return new(new("fixture", "1", new string('A', 64), 0, "test", ""), "en", "labelled fixture", "manual", "none", [], words, 0, 0);
    }

    private static SourcePosition AudioTime(long seconds) => new(new(seconds * 48000, 1, 48000));

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("Valencia-EditorialTest-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
