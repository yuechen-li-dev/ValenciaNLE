using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Xunit;
using static Valencia.Testing.TestProject;

namespace Valencia.Core.Tests;

public sealed class TimingAndQueryTests
{
    [Fact]
    public void StandardFrameAndSampleGridsAndRoundingAreExplicit()
    {
        foreach (var rate in new FrameRate[] { new(24000, 1001), new(30000, 1001), new(60000, 1001), new(24, 1), new(25, 1), new(60, 1) })
        {
            var one = TimelineTime.FromFrames(1, rate);
            Assert.Equal(new SequenceTime(one.Ticks * 1000), TimelineTime.FromFrames(1000, rate));
            Assert.Equal(one, TimelineTime.SnapToFrame(new(one.Ticks + 1), rate, TimeRounding.Floor));
            Assert.Equal(new(one.Ticks * 2), TimelineTime.SnapToFrame(new(one.Ticks + 1), rate, TimeRounding.Ceiling));
        }
        foreach (int rate in new[] { 44100, 48000, 96000, 192000 })
            Assert.Equal(T(1), TimelineTime.FromSamples(rate, rate));
        Assert.Equal(new(5_760_000), TimelineTime.FromSamples(1, 44100));
        Assert.Throws<ArgumentException>(() => TimelineTime.Convert(new(1, 1, 90001)));
        long floor = TimelineTime.Convert(new(1, 1, 90001), TimeRounding.Floor).Ticks;
        Assert.Equal(new(floor + 1), TimelineTime.Convert(new(1, 1, 90001), TimeRounding.Ceiling));
        Assert.Equal(new(-floor - 1), TimelineTime.Convert(new(-1, 1, 90001), TimeRounding.Floor));
        Assert.Equal(new(-floor), TimelineTime.Convert(new(-1, 1, 90001), TimeRounding.TowardZero));
        Assert.Equal(T(6 * 3600), TimelineTime.FromSamples(6L * 3600 * 48000, 48000));
        Assert.Throws<OverflowException>(() => TimelineTime.Convert(new(long.MaxValue, int.MaxValue, 1)));
        Assert.Throws<ArgumentException>(() => TimelineTime.FromFrames(1, new(0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimelineTime.FromSamples(1, 0));
        var sample = TimelineTime.FromSamples(1, 48000);
        Assert.Equal(sample, TimelineTime.SnapToSample(new(sample.Ticks / 2), 48000, TimeRounding.NearestAwayFromZero));
        Assert.Equal(new(-sample.Ticks), TimelineTime.SnapToSample(new(-sample.Ticks / 2), 48000, TimeRounding.NearestAwayFromZero));
    }

    [Fact]
    public void SplitAtSingleSequenceTickPreservesSourceContinuityAndDuration()
    {
        var session = Create();
        var original = Query(session).Clips(SequenceId, Video1)[0];
        Assert.True(Apply(session, new SplitClip(SequenceId, ClipId, new(1), Id(8))).Success);
        var clips = Query(session).Clips(SequenceId, Video1);
        Assert.Equal(1, clips[0].Duration.Ticks);
        Assert.Equal(original.Duration.Ticks, checked(clips[0].Duration.Ticks + clips[1].Duration.Ticks));
        Assert.True(clips[0].SourceOut.EquivalentTo(clips[1].SourceIn));
        Assert.Equal(90000, clips[1].SourceIn.Timestamp.Denominator);
        Assert.Equal(Id(8), Assert.Single(Query(session).Evaluate(SequenceId, new(1))).Clip.Id);
        Assert.Equal(ClipId, Assert.Single(Query(session).Evaluate(SequenceId, new(0))).Clip.Id);
        Assert.Empty(Query(session).Evaluate(SequenceId, original.End));
        Assert.Throws<ArgumentOutOfRangeException>(() => clips[0].MapToSource(clips[0].End));
        Assert.Throws<ArgumentOutOfRangeException>(() => clips[1].MapToTimeline(clips[1].SourceOut));
        Assert.Throws<ArgumentException>(() => original.MapToTimeline(new(new(1, 1, 90001))));
    }

    [Fact]
    public void HugeOriginalEpochAndUnusualTimebaseDoNotLoseRationalOrigin()
    {
        var session = Create(clip: false);
        long epoch = long.MaxValue / 2;
        var stream = new AssetStream(0, TrackKind.Video, "fixture", 1, 90001, new(epoch, 1, 90001), null);
        var asset = Asset() with { Id = Id(8), Streams = [stream] };
        Assert.True(Apply(session, new AddAsset(asset)).Success);
        var origin = new SourcePosition(new(epoch, 1, 90001));
        var clip = Clip() with { AssetId = Id(8), SourceIn = origin, SourceOut = origin.Shift(T(10)) };
        Assert.True(Apply(session, new InsertClip(SequenceId, Video1, clip)).Success);
        var mapped = Assert.Single(Query(session).Evaluate(SequenceId, T(6))).SourcePosition;
        Assert.Equal(epoch, mapped.Timestamp.Value);
        Assert.Equal(90001, mapped.Timestamp.Denominator);
        Assert.Equal(T(6), mapped.Offset);
        Assert.Equal(T(6), clip.MapToTimeline(mapped));
        Assert.Throws<OverflowException>(() => origin.Shift(new(long.MaxValue)).Shift(new(1)));
    }

    [Fact]
    public void EmptySequenceDurationAndReadOnlySnapshotQueriesStayCoherent()
    {
        var session = Create(clip: false);
        var query = Query(session);
        Assert.Equal(T(0), query.Duration(SequenceId));
        Assert.Empty(query.Evaluate(SequenceId, T(0)));
        Assert.True(Apply(session, new InsertClip(SequenceId, Video1, Clip())).Success);
        Assert.Empty(query.Clips(SequenceId, Video1));
        Assert.Single(Query(session).Clips(SequenceId, Video1));
        Assert.Throws<ArgumentException>(() => query.Intersecting(SequenceId, T(1), T(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => query.Evaluate(SequenceId, T(-1)));
    }

    [Fact]
    public void EvaluationHonorsMuteVisibilityEnabledAndTrackOrder()
    {
        var session = Create();
        var audio = Clip(Id(8)) with { StreamIndex = 1, SourceIn = new(new(0, 1, 48000)), SourceOut = new(new(480000, 1, 48000)) };
        Assert.True(Apply(session, new InsertClip(SequenceId, Audio, audio)).Success);
        Assert.True(Apply(session, new InsertClip(SequenceId, Video2, Clip(Id(9)) with { Properties = new(Enabled: false) })).Success);
        Assert.Equal([TrackKind.Video, TrackKind.Audio], Query(session).Evaluate(SequenceId, T(1)).Select(c => c.Kind));
        Assert.True(Apply(session, new SetTrackState(SequenceId, Video1, new(Locked: true, Visible: false))).Success);
        Assert.Equal(TrackKind.Audio, Assert.Single(Query(session).Evaluate(SequenceId, T(1))).Kind);
        Assert.True(Apply(session, new SetTrackState(SequenceId, Audio, new(Muted: true))).Success);
        Assert.Empty(Query(session).Evaluate(SequenceId, T(1)));
        Assert.Equal(T(10), Query(session).Duration(SequenceId));
    }

    [Theory]
    [InlineData("project-id")][InlineData("schema")][InlineData("asset-id")][InlineData("stream-id")]
    [InlineData("track-order")][InlineData("frame-rate")][InlineData("sample-rate")][InlineData("relative-path")]
    [InlineData("missing-collections")]
    public void MalformedDocumentsAreRejected(string kind)
    {
        var document = Create().Snapshot.Document;
        document = kind switch
        {
            "project-id" => document with { Id = Guid.Empty }, "schema" => document with { SchemaVersion = 2 },
            "asset-id" => document with { Assets = document.Assets.Add(Asset()) },
            "stream-id" => document with { Assets = [Asset() with { Streams = [Asset().Streams[0], Asset().Streams[0]] }] },
            "relative-path" => document with { Assets = [Asset() with { SourcePath = "relative.mkv" }] },
            "missing-collections" => document with { Assets = default },
            _ => document with { Sequences = [document.Sequences[0] with
            {
                Tracks = kind == "track-order" ? [document.Sequences[0].Tracks[0] with { Order = 2 }] : document.Sequences[0].Tracks,
                Video = kind == "frame-rate" ? new(1920, 1080, new(0, 1)) : document.Sequences[0].Video,
                AudioSampleRate = kind == "sample-rate" ? 90001 : 48000
            }] }
        };
        Assert.ThrowsAny<ArgumentException>(() => new EditingSession(document));
    }
}
