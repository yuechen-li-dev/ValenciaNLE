using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Media;
using Xunit;

namespace Valencia.Tests;

public sealed class SequencePreviewTests(TimelineFixture fixture) : IClassFixture<TimelineFixture>
{
    [Fact]
    public void PrerollRetainsNativeFramesAndConvertsOnlyTheSelectedPicture()
    {
        using var evaluator = new SequenceEvaluator(fixture.Snapshot, TimelineFixture.Id(4));
        var time = TimelineFixture.F(20);
        var frame = evaluator.Video(time, default);
        Assert.True(evaluator.ActiveVideoDecodedFrames > 5);
        Assert.Equal(1, evaluator.ActiveVideoConvertedFrames);
        Assert.Equal(frame.Rgba, evaluator.Video(new(time.Ticks + TimelineFixture.F(1).Ticks / 3), default).Rgba);
        Assert.Equal(1, evaluator.ActiveVideoConvertedFrames);
        evaluator.Video(TimelineFixture.F(25), default);
        Assert.Equal(2, evaluator.ActiveVideoConvertedFrames);
    }
    [Fact]
    public async Task PausedSeeksMatchEvaluationAcrossCutsGapsLayersAndBackwardJumps()
    {
        using var preview = new SequencePreview();
        foreach (long frame in new long[] { 60, 0, 30, 29, 45, 44, 74, 10, 60, 75 })
        {
            var time = TimelineFixture.F(frame);
            await using var paused = SequencePlayback.OpenPreview(preview, fixture.Snapshot, TimelineFixture.Id(4), time);
            using var reference = new SequenceEvaluator(fixture.Snapshot, TimelineFixture.Id(4), useProxies: true);
            var expected = reference.Video(time, default);
            Assert.Equal(expected.Rgba, paused.Current!.Rgba);
            Assert.Equal(expected.SourceTimestamps!.Value.ToArray(), paused.Current.SourceTimestamps!.Value.ToArray());
            Assert.Equal(expected.VideoClips.ToArray(), paused.Current.VideoClips.ToArray());
            Assert.Equal(time, paused.Position);
            Assert.False(paused.CanPlay); Assert.False(paused.IsPlaying);
            Assert.Equal(0, paused.UnderrunFrames);
            Assert.Throws<InvalidOperationException>(paused.Play);
        }
    }

    [Fact]
    public void ScrubbingDoesNotDecodeAudioOrRequireAnAudioDevice()
    {
        var badAudio = fixture.Snapshot.Document.Assets[0] with { Id = Guid.NewGuid(), SourcePath = Path.Combine(fixture.Root, "missing-audio.mkv") };
        var sequence = fixture.Snapshot.Document.Sequences[0];
        sequence = sequence with { Tracks = [.. sequence.Tracks.Select(t => t.Kind == TrackKind.Audio
            ? t with { Clips = [.. t.Clips.Where(c => c.AssetId == fixture.Snapshot.Document.Assets[0].Id).Select(c => c with { AssetId = badAudio.Id })] } : t)] };
        var snapshot = fixture.Snapshot with { Document = fixture.Snapshot.Document with
            { Assets = fixture.Snapshot.Document.Assets.Add(badAudio), Sequences = [sequence] } };
        using var preview = new SequencePreview();
        var frame = preview.Video(snapshot, sequence.Id, TimelineFixture.F(10));
        Assert.NotEmpty(frame.VideoClips);
    }

    [Fact]
    public void CancellationAndSnapshotChangesNeverPublishOldContent()
    {
        using var preview = new SequencePreview();
        var first = preview.Video(fixture.Snapshot, TimelineFixture.Id(4), default);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => preview.Video(fixture.Snapshot, TimelineFixture.Id(4), TimelineFixture.F(50), cancellation.Token));
        var sequence = fixture.Snapshot.Document.Sequences[0];
        var snapshot = fixture.Snapshot with { Document = fixture.Snapshot.Document with { Sequences =
            [sequence with { Tracks = [.. sequence.Tracks.Select(t => t with { State = t.State with { Visible = false } })] }] } };
        var changed = preview.Video(snapshot, sequence.Id, default);
        Assert.NotEmpty(first.VideoClips); Assert.Empty(changed.VideoClips);
        Assert.All(changed.Rgba.Where((_, i) => i % 4 != 3), b => Assert.Equal(0, b));
        Assert.Equal(first.Rgba, preview.Video(fixture.Snapshot, sequence.Id, default).Rgba);
    }
}
