using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Media;
using Valencia.Persistence;
using Xunit;

namespace Valencia.Tests;

public sealed class SoundtrackKitTests(TimelineFixture fixture) : IClassFixture<TimelineFixture>
{
    private static readonly Guid Sequence = TimelineFixture.Id(4);
    private static readonly Guid VideoTrack = TimelineFixture.Id(5);
    private static SequenceTime F(long frame) => TimelineFixture.F(frame);
    private static SequenceTime Seconds(long seconds) => new(seconds * SequenceTime.TicksPerSecond);

    [Fact]
    public async Task DissolveUsesRealSourceHandlesAndMatchesTheTwoComposites()
    {
        var source = fixture.Snapshot.Document.Sequences[0];
        var track = source.Tracks[0];
        var a = track.Clips[0];
        var b = track.Clips[1];
        b = b with { Start = a.End, SourceIn = b.SourceIn.Shift(F(12)), SourceOut = b.SourceOut.Shift(F(12)) };
        var snapshot = fixture.Snapshot with { Document = fixture.Snapshot.Document with
            { Sequences = [source with { Tracks = [track with { Clips = [a, b] }] }] } };
        await using var engine = new EditingEngine(new EditingSession(snapshot));
        var operation = new TransitionPlan(VideoTrack, Guid.NewGuid(), new(a.Id, b.Id, F(6).Ticks, F(6).Ticks));
        var plan = new EditPlan(1, snapshot.Document.Id, "0", Sequence, [operation]);
        var dry = engine.Plan(plan, false);
        Assert.True(dry.Valid, string.Join("\n", dry.Diagnostics));
        Assert.Same(snapshot, engine.Session.Snapshot);
        var applied = engine.Plan(plan, true);
        Assert.True(applied.Committed);
        Assert.Equal<TransitionChange>(dry.Transitions!.Value, applied.Transitions!.Value);
        Assert.Equal(source.Tracks[0].Clips[0].End, engine.Query().Sequence(Sequence).Tracks[0].Clips[1].Start);

        var at = a.End;
        var pairing = engine.Query().Evaluate(Sequence, at);
        Assert.Equal(new[] { a.Id, b.Id }, pairing.Select(clip => clip.Clip.Id));
        Assert.Equal(a.SourceOut, pairing[0].SourcePosition);
        Assert.Equal(b.SourceIn, pairing[1].SourcePosition);
        using var evaluator = new SequenceEvaluator(engine.Session.Snapshot, Sequence);
        using var before = new SequenceEvaluator(snapshot, Sequence);
        Assert.Equal(before.Video(F(24), default).Rgba, evaluator.Video(F(24), default).Rgba);
        Assert.Equal(before.Video(F(36), default).Rgba, evaluator.Video(F(36), default).Rgba);

        byte[] past = RenderSide(a with { SourceOut = a.SourceOut.Shift(F(6)) });
        byte[] future = RenderSide(b);
        Assert.False(past.SequenceEqual(future));
        byte[] blended = evaluator.Video(at, default).Rgba;
        for (int i = 0; i < blended.Length; i++)
            Assert.Equal((byte)Math.Round((past[i] + future[i]) / 2.0), blended[i]);

        var file = new ProjectFile();
        string path = Path.Combine(fixture.Root, "transition-roundtrip.valencia");
        await file.SaveAsync(path, engine.Session);
        Assert.True(DocumentSemantics.Equal(engine.Session.Snapshot.Document, (await file.LoadAsync(path)).Snapshot.Document));
        var move = plan with { ProjectRevision = "1", Operations = [new MovePlan(b.Id, track.Id, EngineJson.Exact(F(40).Ticks))] };
        var moved = engine.Plan(move, true);
        Assert.True(moved.Committed);
        Assert.Null(Assert.Single(moved.Transitions!.Value).After);
        Assert.True(engine.Undo(2).Success);
        Assert.Single(engine.Query().Sequence(Sequence).Tracks[0].Transitions);

        byte[] RenderSide(TimelineClip clip)
        {
            var document = snapshot.Document with { Sequences = [source with { Tracks = [track with { Clips = [clip] }] }] };
            using var side = new SequenceEvaluator(new(document, 0), Sequence);
            return side.Video(at, default).Rgba;
        }
    }

    [Fact]
    public async Task MissingSourceHandlesRejectTheWholePlan()
    {
        var source = fixture.Snapshot.Document.Sequences[0];
        var track = source.Tracks[0];
        var a = track.Clips[0];
        var b = track.Clips[1] with { Start = a.End };
        var snapshot = fixture.Snapshot with { Document = fixture.Snapshot.Document with
            { Sequences = [source with { Tracks = [track with { Clips = [a, b] }] }] } };
        await using var engine = new EditingEngine(new EditingSession(snapshot));
        var plan = new EditPlan(1, snapshot.Document.Id, "0", Sequence,
            [new ColorPlan(a.Id, new(Contrast: 1.4)),
                new TransitionPlan(track.Id, Guid.NewGuid(), new(a.Id, b.Id, F(6).Ticks, F(6).Ticks))]);
        var failed = engine.Plan(plan, true);
        Assert.False(failed.Valid);
        Assert.Contains("incoming clip lacks", Assert.Single(failed.Diagnostics));
        Assert.Same(snapshot, engine.Session.Snapshot);
    }

    [Fact]
    public async Task EqualPowerCrossfadeAndTrackEnvelopeRunOnTheSameSampleClock()
    {
        var left = await Pcm("left.wav", "0.25|0");
        var right = await Pcm("right.wav", "0|0.25");
        var audioTrack = Guid.NewGuid();
        var a = Clip(left, default, Seconds(1), Seconds(1));
        var b = Clip(right, Seconds(1), Seconds(1), Seconds(1));
        var source = fixture.Snapshot.Document.Sequences[0];
        var snapshot = new ProjectSnapshot(fixture.Snapshot.Document with { Assets = [left, right],
            Sequences = [source with { Tracks = [new(audioTrack, TrackKind.Audio, 0, "Music", new(), [a, b])] }] }, 0);
        await using var engine = new EditingEngine(new EditingSession(snapshot));
        long half = SequenceTime.TicksPerSecond / 2;
        var plan = new EditPlan(1, snapshot.Document.Id, "0", Sequence,
            [new TransitionPlan(audioTrack, Guid.NewGuid(), new(a.Id, b.Id, half, half))]);
        var paired = engine.Plan(plan, true);
        Assert.True(paired.Committed, string.Join("\n", paired.Diagnostics));
        using var evaluator = new SequenceEvaluator(engine.Session.Snapshot, Sequence);
        float[] stereo = evaluator.Audio(default, 23_999, 48_002, default);
        Assert.Equal((.25f, 0f), At(24_000));
        Assert.Equal((0f, .25f), At(72_000));
        var middle = At(48_000);
        Assert.InRange(middle.Left, .17677f, .17679f);
        Assert.InRange(middle.Right, .17677f, .17679f);
        Assert.InRange(middle.Left * middle.Left + middle.Right * middle.Right, .06249f, .06251f);
        var gain = plan with { ProjectRevision = "1", Operations =
            [new TrackGainPlan(audioTrack, -6, [new(default, 0), new(Seconds(2), -12)])] };
        var result = engine.Plan(gain, true);
        Assert.True(result.Committed);
        Assert.Single(result.TrackGain!.Value);
        Assert.Equal(0, engine.Query().Sequence(Sequence).Creator!.PeakCeilingDb);
        using var reduced = new SequenceEvaluator(engine.Session.Snapshot, Sequence);
        float[] actual = reduced.Audio(default, 48_000, 1, default);
        Assert.InRange(actual[0], .0443f, .0445f);
        Assert.Equal(actual[0], actual[1]);

        (float Left, float Right) At(int sample)
        {
            int index = (sample - 23_999) * 2;
            return (stereo[index], stereo[index + 1]);
        }
    }

    [Fact]
    public async Task FrameAlignedSoundHitPreservesTheFirstImpulseAcrossSampleBlocks()
    {
        var hit = await Pcm("impulse.wav", "if(eq(n\\,0)\\,0.5\\,0)|if(eq(n\\,0)\\,0.5\\,0)");
        var source = fixture.Snapshot.Document.Sequences[0];
        Guid audioTrack = Guid.NewGuid(), clip = Guid.NewGuid();
        var snapshot = new ProjectSnapshot(fixture.Snapshot.Document with { Assets = [hit],
            Sequences = [source with { Tracks = [new(audioTrack, TrackKind.Audio, 0, "Hits", new(), [])] }] }, 0);
        await using var engine = new EditingEngine(new EditingSession(snapshot));
        var operation = new SoundHitPlan(audioTrack, clip, hit.Id, hit.Streams[0].Index, F(2).Ticks, 0, F(2).Ticks);
        var plan = new EditPlan(1, snapshot.Document.Id, "0", Sequence, [operation with { At = operation.At + 1 }]);
        Assert.False(engine.Plan(plan, true).Valid);
        Assert.Same(snapshot, engine.Session.Snapshot);
        Assert.True(engine.Plan(plan with { Operations = [operation] }, true).Committed);
        Assert.Equal(F(2), engine.Query().Clips(Sequence, audioTrack)[0].Start);
        long sample = TimelineTime.SampleIndex(F(2), 48000, TimeRounding.Ceiling);
        Assert.Equal(3204, sample);
        using var evaluator = new SequenceEvaluator(engine.Session.Snapshot, Sequence);
        Assert.Equal(new[] { 0f, 0f, .5f, .5f }, evaluator.Audio(default, sample - 1, 2, default));
        Assert.Equal(new[] { 0f, 0f }, evaluator.Audio(default, sample + 1, 1, default));
        Assert.Equal(new[] { .5f, .5f, 0f, 0f }, evaluator.Audio(default, sample, 2, default));
    }

    private async Task<MediaAsset> Pcm(string name, string channels)
    {
        string path = Path.Combine(fixture.Root, name);
        await TimelineFixture.Run("-f", "lavfi", "-i", $"aevalsrc={channels}:s=48000:d=4", "-c:a", "pcm_f32le", path);
        return MediaAssetProbe.Read(Guid.NewGuid(), path);
    }

    [Fact]
    public async Task WarmWaveformSeeksMatchContinuousPcmAcrossIndexExtensions()
    {
        var asset = await Pcm("warm.wav", "0.25*sin(2*PI*439*t)|0.3*sin(2*PI*701*t)");
        var source = fixture.Snapshot.Document.Sequences[0];
        var clip = Clip(asset, default, default, Seconds(4));
        var track = new TimelineTrack(Guid.NewGuid(), TrackKind.Audio, 0, "Music", new(), [clip]);
        var snapshot = new ProjectSnapshot(fixture.Snapshot.Document with { Assets = [asset], Sequences = [source with { Tracks = [track] }] }, 0);
        using var continuous = new SequenceEvaluator(snapshot, Sequence);
        float[] expected = continuous.Audio(default, 0, 192_000, default);
        using var warmed = new SequenceEvaluator(snapshot, Sequence);
        foreach (int sample in new[] { 153_739, 180_001, 149_213 })
            Assert.Equal(expected.AsSpan(sample * 2, 1024).ToArray(), warmed.Audio(default, sample, 512, default));
    }

    private static TimelineClip Clip(MediaAsset asset, SequenceTime start, SequenceTime offset, SequenceTime duration)
    {
        var stream = asset.Streams[0];
        var source = new SourcePosition(stream.Start ?? new(0, stream.TimeBaseNumerator, stream.TimeBaseDenominator)).Shift(offset);
        return new(Guid.NewGuid(), asset.Id, stream.Index, start, source, source.Shift(duration), new());
    }
}
