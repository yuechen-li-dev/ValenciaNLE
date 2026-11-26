using Valencia.App.Editing;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Media;
using Valencia.Persistence;
using Xunit;

namespace Valencia.Tests;

public sealed class EditorEffectsTests(TimelineFixture fixture) : IClassFixture<TimelineFixture>
{
    private static SequenceTime F(long frames) => TimelineFixture.F(frames);

    [Fact]
    public async Task SpeedAndFreezeUseTheSameSourceFramesAsOriginalAndKeepAudioUntouched()
    {
        var snapshot = fixture.Snapshot;
        var sequence = snapshot.Document.Sequences[0];
        var track = sequence.Tracks[0];
        var first = track.Clips[0];
        var second = track.Clips[1];
        await using var engine = new EditingEngine(new EditingSession(snapshot));
        var plan = new EditPlan(1, snapshot.Document.Id, "0", sequence.Id,
            [new SpeedPlan(first.Id, 2, 1), new FreezePlan(second.Id, F(60).Ticks, F(18).Ticks)]);
        Assert.True(engine.Plan(plan, false).Valid);
        Assert.Same(snapshot, engine.Session.Snapshot);
        Assert.True(engine.Plan(plan, true).Committed);
        var result = engine.Query().Sequence(sequence.Id);
        var sped = result.Tracks[0].Clips[0];
        var frozen = result.Tracks[0].Clips[1];
        Assert.Equal(F(15), sped.End);
        Assert.Equal(F(63), frozen.End);
        Assert.Equal(first.MapToSource(F(12)), sped.MapToSource(F(6)));
        Assert.Equal(second.MapToSource(F(60)), frozen.MapToSource(F(45)));
        Assert.Equal(frozen.MapToSource(F(45)), frozen.MapToSource(F(62)));
        Assert.Throws<ArgumentOutOfRangeException>(() => frozen.MapToSource(F(63)));
        Assert.Throws<InvalidOperationException>(() => frozen.MapToTimeline(frozen.MapToSource(F(62))));
        Assert.Equal(sequence.Tracks[2], result.Tracks[2]);

        using var original = new SequenceEvaluator(snapshot, sequence.Id);
        using var edited = new SequenceEvaluator(engine.Session.Snapshot, sequence.Id);
        Assert.Equal(original.Video(F(12), default).Rgba, edited.Video(F(6), default).Rgba);
        Assert.Equal(original.Video(F(60), default).SourceTimestamps!.Value[0], edited.Video(F(45), default).SourceTimestamps!.Value[0]);
        Assert.Equal(edited.Video(F(45), default).Rgba, edited.Video(F(59), default).Rgba);
        Assert.All(edited.Video(F(15), default).Rgba.Chunk(4), pixel => Assert.Equal(new byte[] { 0, 0, 0, 255 }, pixel));
        Assert.Equal(original.Audio(default, 0, 4096, default), edited.Audio(default, 0, 4096, default));

        string project = Path.Combine(fixture.Root, "speed.valencia");
        await new ProjectFile().SaveAsync(project, engine.Session);
        var reopened = (await new ProjectFile().LoadAsync(project)).Snapshot;
        Assert.True(DocumentSemantics.Equal(engine.Session.Snapshot.Document, reopened.Document));
        string clip = Path.Combine(fixture.Root, "freeze.mp4");
        var export = await SequenceExport.RunAsync(reopened, sequence.Id, clip, range: (F(45), F(59)));
        Assert.Equal(14, export.Frames);
        byte[] frames = await TimelineFixture.Run("-i", clip, "-an", "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1");
        int width = sequence.Video.Width, height = sequence.Video.Height, size = width * height;
        Assert.Equal(14 * size, frames.Length);
        for (int output = 1; output < 14; output++)
        {
            double difference = Enumerable.Range(0, size).Average(i => (double)Math.Abs(frames[i] - frames[output * size + i]));
            Assert.InRange(difference, 0, 2);
        }
    }

    [Fact]
    public async Task SpeedAndFreezeRejectOverlapsAndKeepTheirMappingThroughSplits()
    {
        var start = fixture.Snapshot;
        var sequence = start.Document.Sequences[0];
        var first = sequence.Tracks[0].Clips[0];
        var second = sequence.Tracks[0].Clips[1];
        var audio = sequence.Tracks[2].Clips[0];
        await using var engine = new EditingEngine(new EditingSession(start));
        EditPlan Plan(params PlanOperation[] operations) =>
            new(1, start.Document.Id, EngineJson.Exact(engine.Session.Snapshot.Revision), sequence.Id, [.. operations]);
        Assert.False(engine.Plan(Plan(new SpeedPlan(first.Id, 1, 2)), true).Valid);
        Assert.False(engine.Plan(Plan(new FreezePlan(audio.Id, F(15).Ticks, F(10).Ticks)), true).Valid);
        Assert.False(engine.Plan(Plan(new SpeedPlan(first.Id, 17, 3)), true).Valid);
        Assert.Same(start, engine.Session.Snapshot);
        Guid right = Guid.NewGuid(), rightHold = Guid.NewGuid();
        Assert.True(engine.Plan(Plan(new SpeedPlan(first.Id, 2, 1), new FreezePlan(second.Id, F(47).Ticks, F(18).Ticks),
            new SplitPlan(first.Id, EngineJson.Exact(F(7).Ticks), right),
            new SplitPlan(second.Id, EngineJson.Exact(F(51).Ticks), rightHold)), true).Committed);
        var clips = engine.Query().Sequence(sequence.Id).Tracks[0].Clips;
        Assert.Equal(first.MapToSource(F(16)), clips.Single(c => c.Id == right).MapToSource(F(8)));
        Assert.Equal(second.MapToSource(F(47)), clips.Single(c => c.Id == rightHold).MapToSource(F(51)));
        Assert.Equal(F(63), clips.Single(c => c.Id == rightHold).End);
        Assert.True(engine.Undo(1).Success);
        Assert.True(DocumentSemantics.Equal(start.Document, engine.Session.Snapshot.Document));
        Assert.True(engine.Redo(2).Success);
        Assert.Equal(clips, engine.Query().Sequence(sequence.Id).Tracks[0].Clips);
    }

    [Fact]
    public void EffectGesturePreviewsWithoutCommitAndRejectsStaleRevision()
    {
        var snapshot = fixture.Snapshot;
        var sequence = snapshot.Document.Sequences[0];
        var first = sequence.Tracks[0].Clips[0];
        var session = new EditingSession(snapshot);
        var target = new TransformControl(first, null, F(11));
        var drag = new EffectDrag(0, (x, _) => target.Write(sequence.Id, target.Pose with { X = x }));
        drag.Move(session, .65, 0);
        Assert.True(drag.Preview!.Success);
        Assert.Same(snapshot, session.Snapshot);
        Assert.True(drag.Commit(session)!.Success);
        Assert.Equal(EditError.StaleRevision, drag.Commit(session)!.Error);
        var clip = session.Snapshot.Document.Sequences[0].Tracks[0].Clips[0];
        var trimmed = clip with { Start = F(10), SourceIn = clip.SourceIn.Shift(F(10)) };
        var edit = new TransformControl(trimmed, null, F(13));
        var update = Assert.IsType<SetClipTransform>(edit.Write(sequence.Id, edit.Pose with { Y = .3 }));
        Assert.True(update.PreserveOrigin);
        Assert.Equal(new[] { F(11).Ticks, F(13).Ticks }, update.Curve!.Keys.Select(k => k.At));
    }

    [Theory]
    [InlineData(390, 844)]
    [InlineData(768, 1024)]
    [InlineData(1280, 720)]
    public void TransformAndSpeedControlsFitAllStandardLayouts(int width, int height)
    {
        var editor = new EditorDocument();
        editor.Open(fixture.Snapshot, "fixture.valencia");
        editor.ControlsMode = true;
        editor.SelectedClip = editor.Sequence.Tracks[0].Clips[0].Id;
        editor.Playhead = F(10);
        var view = new EditorView();
        var frame = view.Prepare(editor, new(editor), false, false, width, height);
        foreach (string action in new[] { "fx:half", "fx:freeze", "fx:x+", "fx:rotation-", "fx:opacity+", "fx:prev", "fx:key+", "fx:next" })
        {
            Assert.True(frame.TryGetActionCenter(Machina.Core.Actions.UiAction.Named(action), out var center), action);
            Assert.Equal(action, frame.HitTest.HitTest(center)?.Action.Name);
        }
    }
}
