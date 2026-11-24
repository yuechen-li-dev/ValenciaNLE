using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Media;
using Valencia.Persistence;
using Xunit;

namespace Valencia.Tests;

public sealed class EffectPackTests(TimelineFixture fixture) : IClassFixture<TimelineFixture>
{
    private static readonly Guid Sequence = TimelineFixture.Id(4), Clip = TimelineFixture.Id(10);
    private static readonly RegionEffect Rectangle = new("cover", .25, .25, .5, .75, 4);
    private static TimelineClip Video(ProjectSnapshot snapshot) => new TimelineQueries(snapshot).Sequence(Sequence).Tracks[0].Clips[0];

    [Fact]
    public async Task TypedPlanPatchesOnlyItsEffectsAndRoundTripsWithUndo()
    {
        var session = new EditingSession(fixture.Snapshot);
        var properties = new ClipProperties("Keep me", Opacity: .7m, Video: new(Contrast: 1.1, Scale: 1.15, CropLeft: .1));
        Assert.True(session.Execute(new EditCommand(Guid.NewGuid(), 1, new SetClipProperties(Sequence, Clip, properties), 0)).Success);
        var before = session.Snapshot;
        await using var engine = new EditingEngine(session);
        var typed = new EditPlan(1, before.Document.Id, "1", Sequence,
            [new ColorPlan(Clip, new(Temperature: .35)), new RegionPlan(Clip, Rectangle)]);
        var parsed = EditPlanSchema.Parse(EngineJson.Write(typed));
        var preview = engine.Plan(parsed, false);
        Assert.True(preview.Valid);
        Assert.Same(before, session.Snapshot);
        var result = engine.Plan(parsed, true);
        Assert.True(result.Committed);
        Assert.Equal<ClipChange>(preview.Changes, result.Changes);
        var edited = Video(session.Snapshot);
        Assert.Equal(properties with { Video = properties.Video! with { Temperature = .35, Region = Rectangle } }, edited.Properties);
        var project = new ProjectFile();
        string path = Path.Combine(fixture.Root, "effect-roundtrip.valencia");
        await project.SaveAsync(path, session);
        Assert.True(DocumentSemantics.Equal(session.Snapshot.Document, (await project.LoadAsync(path)).Snapshot.Document));
        Assert.True(engine.Undo(session.Snapshot.Revision).Success);
        Assert.Equal(properties, Video(session.Snapshot).Properties);
        Assert.True(engine.Redo(session.Snapshot.Revision).Success);
        Assert.Equal(edited, Video(session.Snapshot));

        var invalid = typed with { ProjectRevision = EngineJson.Exact(session.Snapshot.Revision),
            Operations = [new ColorPlan(Clip, new(Exposure: .1)), new RegionPlan(Clip, Rectangle with { Right = .1 })] };
        var beforeFailure = session.Snapshot;
        Assert.False(engine.Plan(invalid, true).Valid);
        Assert.Same(beforeFailure, session.Snapshot);
        var clear = invalid with { Operations = [new RegionPlan(Clip)] };
        Assert.True(engine.Plan(EditPlanSchema.Parse(EngineJson.Write(clear)), true).Committed);
        Assert.Null(Video(session.Snapshot).Properties.Video!.Region);
        Assert.Equal(.35, Video(session.Snapshot).Properties.Video!.Temperature);
    }

    [Theory]
    [InlineData("cover")]
    [InlineData("pixelate")]
    [InlineData("blur")]
    public void RegionSamplesRespectHalfOpenBoundsAndLeaveOtherPixelsAlone(string mode)
    {
        using var reference = new SequenceEvaluator(fixture.Snapshot, Sequence, useVulkanEffects: false);
        var modified = Apply(new SetClipRegion(Sequence, Clip, Rectangle with { Mode = mode }));
        using var evaluator = new SequenceEvaluator(modified, Sequence, useVulkanEffects: false);
        var original = reference.Video(TimelineFixture.F(6), default);
        var actual = evaluator.Video(TimelineFixture.F(6), default);
        int top = (int)Math.Floor(original.Height * .25), bottom = (int)Math.Ceiling(original.Height * .75);
        int left = original.Width / 4, right = original.Width / 2;
        int changed = 0;
        for (int y = 0; y < original.Height; y++)
        for (int x = 0; x < original.Width; x++)
        for (int channel = 0; channel < 3; channel++)
        {
            byte Channel(int sx, int sy) => original.Rgba[(sy * original.Width + sx) * 4 + channel];
            byte expected = Channel(x, y);
            if (x >= left && x < right && y >= top && y < bottom)
            {
                expected = mode switch
                {
                    "cover" => 0,
                    "pixelate" => Channel(Math.Min(right - 1, left + (x - left) / 4 * 4 + 2), Math.Min(bottom - 1, top + (y - top) / 4 * 4 + 2)),
                    _ => (byte)((Channel(Math.Clamp(x - 4, left, right - 1), Math.Clamp(y - 4, top, bottom - 1))
                        + 2 * Channel(x, Math.Clamp(y - 4, top, bottom - 1)) + Channel(Math.Clamp(x + 4, left, right - 1), Math.Clamp(y - 4, top, bottom - 1))
                        + 2 * Channel(Math.Clamp(x - 4, left, right - 1), y) + 4 * Channel(x, y) + 2 * Channel(Math.Clamp(x + 4, left, right - 1), y)
                        + Channel(Math.Clamp(x - 4, left, right - 1), Math.Clamp(y + 4, top, bottom - 1))
                        + 2 * Channel(x, Math.Clamp(y + 4, top, bottom - 1)) + Channel(Math.Clamp(x + 4, left, right - 1), Math.Clamp(y + 4, top, bottom - 1))) / 16)
                };
            }
            Assert.Equal(expected, actual.Rgba[(y * original.Width + x) * 4 + channel]);
            if (expected != Channel(x, y)) changed++;
        }
        Assert.True(changed > 100);
    }

    [Fact]
    public async Task IndependentMp4DecodeShowsCoverAtTheSamePositionAsPausedPreview()
    {
        var snapshot = Apply(new PatchClipColor(Sequence, Clip, new(Temperature: .6)), new SetClipRegion(Sequence, Clip, Rectangle));
        using var preview = new SequencePreview();
        var frame = preview.Video(snapshot, Sequence, TimelineFixture.F(6));
        string path = Path.Combine(fixture.Root, "effect-covered.mp4");
        await SequenceExport.RunAsync(snapshot, Sequence, path);
        var exported = await TimelineFixture.Run("-i", path, "-vf", "select=eq(n\\,6)", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1");
        Assert.Equal(frame.Rgba.Length, exported.Length);
        int rgb = (45 * frame.Width + 56) * 4;
        Assert.All(frame.Rgba[rgb..(rgb + 3)], channel => Assert.Equal(0, channel));
        Assert.All(exported[rgb..(rgb + 3)], channel => Assert.InRange(channel, 0, 12));
        Assert.True(exported.Zip(frame.Rgba, (a, b) => Math.Abs(a - b)).Average() < 8);
    }

    private ProjectSnapshot Apply(params EditOperation[] operations)
    {
        var session = new EditingSession(fixture.Snapshot);
        var result = session.Execute(new EditTransaction(Guid.NewGuid(), 1, 0, [.. operations.Select(o => new EditCommand(Guid.NewGuid(), 1, o))]));
        Assert.True(result.Success, result.Message);
        return result.Snapshot;
    }
}
