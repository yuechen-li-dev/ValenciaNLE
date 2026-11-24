using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Media;
using Valencia.Persistence;
using Xunit;

namespace Valencia.Tests;

public sealed class AnimatedTitleTests(TimelineFixture fixture) : IClassFixture<TimelineFixture>
{
    private static readonly Guid SequenceId = TimelineFixture.Id(4);

    [Theory]
    [InlineData("static")]
    [InlineData("fade")]
    [InlineData("slideUp")]
    [InlineData("pop")]
    [InlineData("impact")]
    public void MotionUsesExactHalfOpenTimesAndSettlesToRest(string mode)
    {
        long start = 9_007_199_254_740_993;
        var cue = new TitleCue(Guid.NewGuid(), "Impact", new(start), new(start + 1000), mode, 100, 200);
        Assert.Null(TitleMotion.At(cue, new(start - 1)));
        Assert.NotNull(TitleMotion.At(cue, new(start)));
        Assert.Equal(new TitlePose(1, 1, 0, 0), TitleMotion.At(cue, new(start + 100)));
        Assert.NotNull(TitleMotion.At(cue, new(start + 999)));
        Assert.Null(TitleMotion.At(cue, cue.End));
        if (mode != "static")
        {
            Assert.NotEqual(TitleMotion.At(cue, cue.Start), TitleMotion.At(cue, new(start + 25)));
            Assert.Equal(.5, TitleMotion.At(cue, new(start + 900))!.Value.Opacity);
        }
    }

    [Fact]
    public async Task PlanDiffUndoAndDeliverancePreserveTitlesAndRejectInvalidRanges()
    {
        var serializer = new ProjectJsonSerializer();
        byte[] legacyPayload = serializer.Serialize(fixture.Snapshot);
        Assert.DoesNotContain("\"Titles\"", System.Text.Encoding.UTF8.GetString(legacyPayload));
        Assert.True(DocumentSemantics.Equal(fixture.Snapshot.Document, serializer.Deserialize<ProjectSnapshot>(legacyPayload).Document));
        await using var engine = new EditingEngine(new EditingSession(fixture.Snapshot));
        var operation = new TitlePlan(Guid.NewGuid(), "NICE SHOT!", TimelineFixture.F(30).Ticks, TimelineFixture.F(43).Ticks,
            "pop", TimelineFixture.F(4).Ticks, TimelineFixture.F(2).Ticks, X: .4, Color: 0xFEEE19FF);
        var plan = new EditPlan(1, fixture.Snapshot.Document.Id, "0", SequenceId, [operation]);
        var dry = engine.Plan(plan, false);
        Assert.True(dry.Valid, string.Join(", ", dry.Diagnostics));
        Assert.Same(fixture.Snapshot, engine.Session.Snapshot);
        var committed = engine.Plan(plan, true);
        Assert.True(committed.Committed);
        Assert.Equal<TitleChange>(dry.Titles!.Value, committed.Titles!.Value);
        Assert.Contains(operation.TitleId, committed.AffectedIds);
        var edited = engine.Session.Snapshot;
        string path = Path.Combine(fixture.Root, "titles.valencia");
        await new ProjectFile().SaveAsync(path, engine.Session);
        var loaded = await new ProjectFile().LoadAsync(path);
        Assert.True(DocumentSemantics.Equal(edited.Document, loaded.Snapshot.Document));
        Assert.True(engine.Undo(edited.Revision).Success);
        Assert.Empty(engine.Query().Sequence(SequenceId).Titles);
        Assert.True(engine.Redo(engine.Session.Snapshot.Revision).Success);
        Assert.True(DocumentSemantics.Equal(edited.Document, engine.Session.Snapshot.Document));
        var beforeFailure = engine.Session.Snapshot;
        plan = plan with { ProjectRevision = EngineJson.Exact(beforeFailure.Revision) };
        var remove = new RemoveTitlePlan(operation.TitleId);
        Assert.False(engine.Plan(plan with
        {
            Operations = [remove, operation with { End = operation.Start }]
        }, true).Valid);
        Assert.Same(beforeFailure, engine.Session.Snapshot);
        Assert.True(engine.Plan(plan with { Operations = [remove] }, true).Committed);
        Assert.Empty(engine.Query().Sequence(SequenceId).Titles);
    }

    [Fact]
    public async Task FractionalFrameExportMatchesSharedTitleEvaluationOverBlackGap()
    {
        await using var engine = new EditingEngine(new EditingSession(fixture.Snapshot));
        var title = new TitlePlan(Guid.NewGuid(), "POP", TimelineFixture.F(31).Ticks, TimelineFixture.F(44).Ticks, "pop",
            TimelineFixture.F(8).Ticks, TimelineFixture.F(2).Ticks, Size: .27);
        Assert.True(engine.Plan(new(1, fixture.Snapshot.Document.Id, "0", SequenceId, [title]), true).Committed);
        string path = Path.Combine(fixture.Root, "titles.mp4");
        await SequenceExport.RunAsync(engine.Session.Snapshot, SequenceId, path);
        var luma = await TimelineFixture.Run("-threads", "1", "-i", path, "-map", "0:v:0", "-pix_fmt", "gray", "-f", "rawvideo", "-");
        using var preview = new SequenceEvaluator(engine.Session.Snapshot, SequenceId);
        double previous = 0;
        foreach (int frame in new[] { 30, 31, 32, 39, 43, 44 })
        {
            var rendered = preview.Video(TimelineFixture.F(frame), default);
            double expected = Enumerable.Range(0, 160 * 90).Average(index => (double)rendered.Rgba[4 * index]);
            double actual = luma.Skip(frame * 160 * 90).Take(160 * 90).Average(value => (double)value);
            Assert.InRange(Math.Abs(actual - expected), 0, 2);
            if (frame is 30 or 31 or 44) Assert.InRange(actual, 0, .1);
            if (frame is 32 or 39) Assert.True(actual > previous + 1, $"Frame {frame} luma: {actual}, previous: {previous}");
            previous = actual;
        }
    }

    [Theory]
    [InlineData("punchIn")]
    [InlineData("shake")]
    public async Task ClipMotionFollowsTheClipAndLeavesItsUnderlyingGradeAndAudioIntact(string preset)
    {
        await using var engine = new EditingEngine(new EditingSession(fixture.Snapshot));
        var clip = engine.Query().Sequence(SequenceId).Tracks[0].Clips[0];
        var color = new ColorPlan(clip.Id, new(Contrast: 1.2));
        var plan = new EditPlan(1, fixture.Snapshot.Document.Id, "0", SequenceId, [color]);
        Assert.True(engine.Plan(plan, true).Committed);
        var motion = new ClipMotionPlan(clip.Id, new(preset, TimelineFixture.F(12).Ticks, .75));
        plan = plan with { ProjectRevision = "1", Operations = [motion] };
        using var original = new SequenceEvaluator(engine.Session.Snapshot, SequenceId);
        Assert.True(engine.Plan(plan, true).Committed);
        var rendered = engine.Session.Snapshot;
        var video = engine.Query().Sequence(SequenceId).Tracks[0].Clips[0].Properties.Video!;
        Assert.Equal(1.2, video.Contrast);
        Assert.Equal(motion.Motion, video.Motion);
        using var modified = new SequenceEvaluator(rendered, SequenceId);
        Assert.False(original.Video(TimelineFixture.F(2), default).Rgba.SequenceEqual(modified.Video(TimelineFixture.F(2), default).Rgba));
        Assert.Equal(original.Video(TimelineFixture.F(12), default).Rgba, modified.Video(TimelineFixture.F(12), default).Rgba);
        Assert.Equal(original.Audio(default, 0, 2000, default), modified.Audio(default, 0, 2000, default));
        var invalid = motion with { ClipId = fixture.Snapshot.Document.Sequences[0].Tracks[2].Clips[0].Id };
        Assert.False(engine.Plan(plan with { ProjectRevision = "2", Operations = [invalid] }, true).Valid);
        Assert.Same(rendered, engine.Session.Snapshot);
    }
}
