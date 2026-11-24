using System.Collections.Immutable;
using SkiaSharp;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Media;
using Valencia.Persistence;
using Xunit;

namespace Valencia.Tests;

public sealed class TimelineReviewTests(TimelineFixture fixture) : IClassFixture<TimelineFixture>
{
    private static readonly Guid Sequence = TimelineFixture.Id(4);
    private static long Frame(int index) => TimelineFixture.F(index).Ticks;
    private EditPlan MakePlan(params PlanOperation[] operations) =>
        new(1, fixture.Snapshot.Document.Id, "0", Sequence, [.. operations]);

    [Fact]
    public async Task CompositedPlanReviewIsPixelIdenticalAfterCommitAndDoesNotWriteHistory()
    {
        var initial = fixture.Snapshot;
        await using var engine = new EditingEngine(new EditingSession(initial));
        var title = Guid.NewGuid();
        var plan = MakePlan(new TitlePlan(title, "REVIEW", Frame(32), Frame(42), Size: .26),
            new TransformPlan("title", title, new([new(0, X: .36, Rotation: 15)])));
        var portrait = Guid.NewGuid();
        plan = plan with { Operations = plan.Operations.Add(new OutputFramingPlan(portrait, "Portrait", 90, 160, "fill")) };
        string proposed = Path.Combine(fixture.Root, "proposed.png"), committed = Path.Combine(fixture.Root, "committed.png");
        var selection = new ReviewSelection("contactSheet", [Frame(31), Frame(35), Frame(42)], ProfileId: portrait, MaxEdge: 160);
        var reviewed = await engine.ReviewAsync(initial.Document.Id, Sequence, 0, selection, proposed, plan);
        Assert.Same(initial, engine.Session.Snapshot);
        Assert.False(engine.Session.CanUndo);
        Assert.Equal("plan", reviewed.Candidate);
        Assert.True(reviewed.BaseStillCurrent);
        Assert.Equal(portrait, reviewed.ProfileId);
        Assert.Equal(new[] { Frame(31), Frame(35), Frame(42) }, reviewed.Samples!.Value.Select(sample => sample.Time.Ticks));
        using var sheet = SKBitmap.Decode(proposed);
        Assert.Equal((270, 184), (sheet.Width, sheet.Height));
        Assert.All(reviewed.Samples!.Value, sample => Assert.Empty(sample.VideoClips));
        Assert.True(sheet.Pixels.Skip(90).Take(90).All(pixel => pixel.Red == 0));
        Assert.Contains(Enumerable.Range(90, 90).SelectMany(x => Enumerable.Range(0, 160).Select(y => sheet.GetPixel(x, y))), pixel => pixel.Red > 210);

        var applied = engine.Plan(plan, true);
        Assert.True(applied.Committed);
        Assert.Equal(title, Assert.Single(applied.Titles!.Value).TitleId);
        await engine.ReviewAsync(initial.Document.Id, Sequence, 1, selection, committed);
        Assert.Equal(await File.ReadAllBytesAsync(proposed), await File.ReadAllBytesAsync(committed));

        string stale = Path.Combine(fixture.Root, "stale.png");
        await Assert.ThrowsAsync<ArgumentException>(() => engine.ReviewAsync(initial.Document.Id, Sequence, 0, selection, stale, plan));
        Assert.False(File.Exists(stale));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.ReviewAsync(initial.Document.Id, Sequence, 1, selection, stale, cancellation: canceled.Token));
        Assert.False(File.Exists(stale));
        byte[] previous = await File.ReadAllBytesAsync(proposed);
        await Assert.ThrowsAsync<IOException>(() => engine.ReviewAsync(initial.Document.Id, Sequence, 1, selection, proposed));
        Assert.Equal(previous, await File.ReadAllBytesAsync(proposed));
    }

    [Fact]
    public async Task ReviewMovieHonorsExactRangeAndContainsTheKeysAndSilentGap()
    {
        await using var engine = new EditingEngine(new EditingSession(fixture.Snapshot));
        var title = Guid.NewGuid();
        var plan = MakePlan(new TitlePlan(title, "BEAT", Frame(31), Frame(43), Size: .34),
            new TransformPlan("title", title, new([new(0, Opacity: 0, Interpolation: "hold"),
                new(Frame(3), Opacity: 1, Rotation: 18)])));
        string movie = Path.Combine(fixture.Root, "review.mp4");
        var result = await engine.ReviewAsync(plan.ProjectId, Sequence, 0,
            new("video", StartTicks: Frame(30), EndTicks: Frame(45), MaxEdge: 96), movie, plan);
        Assert.Equal(15, result.Video!.Frames);
        Assert.Equal((96, 54), (result.Width, result.Height));
        var luma = await TimelineFixture.Run("-i", movie, "-map", "0:v:0", "-pix_fmt", "gray", "-f", "rawvideo", "-");
        const int frameSize = 96 * 54;
        Assert.Equal(frameSize * 15, luma.Length);
        Assert.True(luma.Take(4 * frameSize).Max() <= 2);
        Assert.True(luma.Skip(4 * frameSize).Take(frameSize).Max() > 210);
        Assert.True(luma.Skip(13 * frameSize).Max() <= 2);
        var pcm = await TimelineFixture.Run("-i", movie, "-map", "0:a:0", "-c:a", "pcm_s16le", "-f", "s16le", "-");
        Assert.All(pcm, sample => Assert.Equal(0, sample));
        Assert.Same(fixture.Snapshot, engine.Session.Snapshot);
    }

    [Fact]
    public async Task ClipKeysRetainTheirSourcePhaseThroughASplitAndRoundTrip()
    {
        await using var engine = new EditingEngine(new EditingSession(fixture.Snapshot));
        var clip = TimelineFixture.Id(10);
        var right = Guid.NewGuid();
        var curve = new TransformCurve([new(0, X: .3, Interpolation: "ease"), new(Frame(20), X: .7, Scale: .8, Rotation: 23)]);
        var transform = new TransformPlan("clip", clip, curve);
        var graded = new ColorPlan(clip, new(Contrast: 1.2));
        var plan = MakePlan(graded, transform);
        Assert.True(engine.Plan(plan, true).Committed);
        var original = engine.Session.Snapshot;
        var split = plan with { ProjectRevision = "1", Operations = [new SplitPlan(clip, EngineJson.Exact(Frame(11)), right)] };
        Assert.True(engine.Plan(split, true).Committed);
        var edited = engine.Session.Snapshot;
        var rightClip = engine.Query().Sequence(Sequence).Tracks[0].Clips[1];
        Assert.Equal(right, rightClip.Id);
        Assert.Equal(original.Document.Sequences[0].Tracks[0].Clips[0].SourceIn, rightClip.Properties.Video!.Transform!.Origin);
        Assert.Equal(Frame(11), rightClip.SourceIn.Since(rightClip.Properties.Video.Transform.Origin).Ticks);
        using var evaluator = new SequenceEvaluator(original, Sequence);
        using var afterSplit = new SequenceEvaluator(edited, Sequence);
        using var baseEvaluator = new SequenceEvaluator(fixture.Snapshot, Sequence);
        Assert.Equal(evaluator.Video(new(Frame(12)), default).Rgba, afterSplit.Video(new(Frame(12)), default).Rgba);
        Assert.False(baseEvaluator.Video(new(Frame(20)), default).Rgba.SequenceEqual(afterSplit.Video(new(Frame(20)), default).Rgba));
        var file = new ProjectFile();
        string path = Path.Combine(fixture.Root, "keys.valencia");
        await file.SaveAsync(path, engine.Session);
        Assert.True(DocumentSemantics.Equal(edited.Document, (await file.LoadAsync(path)).Snapshot.Document));
        Assert.True(engine.Undo(edited.Revision).Success);
        Assert.True(DocumentSemantics.Equal(original.Document, engine.Session.Snapshot.Document));
    }

    [Fact]
    public void HoldLinearAndEaseUseLongRelativeTicksAndExactKeys()
    {
        const long start = 9_007_199_254_740_993;
        var keys = new TransformCurve(ImmutableArray.Create(new TransformKey(start, X: 0, Interpolation: "hold"),
            new(start + 100, X: .25), new(start + 200, X: .75, Interpolation: "ease"), new(start + 300, X: 1.75)));
        Assert.Equal(0, keys.At(start + 99).X);
        Assert.Equal(.25, keys.At(start + 100).X);
        Assert.Equal(.5, keys.At(start + 150).X);
        Assert.Equal(.90625, keys.At(start + 225).X);
        Assert.Equal(1.75, keys.At(start + 999).X);
    }
}
