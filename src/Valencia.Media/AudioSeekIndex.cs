using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Media;

/// <summary>Bounded progressive anchors measured from decoded sample counts, never inferred from rounded packet PTS.</summary>
internal sealed class AudioSeekIndex
{
    internal sealed record Anchor(MediaTimestamp Pts, SourcePosition Position, SourcePosition EpochOrigin, long Samples);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, AudioSeekIndex> Recent = [];
    private readonly MediaAsset asset;
    private readonly AssetStream stream;
    private readonly List<Anchor> anchors = [];
    private readonly int rate;
    private SourcePosition origin;
    private long samples;
    private SourcePosition last;
    private bool started, ended;

    private AudioSeekIndex(MediaAsset asset, AssetStream stream)
    {
        this.asset = asset; this.stream = stream;
        rate = stream.SampleRate ?? throw new NotSupportedException("An audio sample rate is required for precise seeking.");
    }

    public static Anchor Resolve(MediaAsset asset, AssetStream stream, SourcePosition target, CancellationToken cancellation)
    {
        var file = new FileInfo(asset.SourcePath);
        string key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{stream.Index}";
        // Index building is worker-only. Serializing this small owner also prevents eviction during an active query.
        lock (Gate)
        {
            if (!Recent.TryGetValue(key, out var index))
            {
                while (Recent.Count >= 8)
                {
                    var oldest = Recent.First();
                    Recent.Remove(oldest.Key);
                }
                Recent.Add(key, index = new(asset, stream));
            }
            return index.Find(target, cancellation);
        }
    }
    public static SourceIndexData Build(MediaAsset asset, AssetStream stream, SourcePosition end, CancellationToken cancellation)
    {
        lock (Gate)
        {
            Resolve(asset, stream, end, cancellation);
            var info = new FileInfo(asset.SourcePath);
            var index = Recent[$"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{stream.Index}"];
            return new(stream.Index, "Progressive original decoded sample-count anchors at two-second intervals; process cache; forward epochs preserved", index.last, index.anchors.Count);
        }
    }

    private Anchor Find(SourcePosition target, CancellationToken cancellation)
    {
        using var reader = !ended && (!started || last.CompareTo(target) < 0)
            ? new FfmpegDecoder(asset, stream, started ? DecoderStart(asset, stream, anchors[^1].Pts)
                : new(stream.Start ?? new(0, stream.TimeBaseNumerator, stream.TimeBaseDenominator)), rate)
            : null;
        Anchor? resume = started && reader is not null ? anchors[^1] : null;
        if (resume is not null)
        {
            origin = resume.EpochOrigin;
            samples = resume.Samples;
            last = resume.Position;
        }
        while (!ended && (!started || last.CompareTo(target) < 0))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!reader!.Read(cancellation)) { ended = true; break; }
            var pts = reader.Timestamp;
            if (resume is not null)
            {
                int compared = new SourcePosition(pts).CompareTo(new(resume.Pts));
                if (compared < 0) continue;
                if (compared > 0) throw SkippedAnchor(asset, resume.Pts, pts);
                resume = null;
            }
            if (!started) { origin = new(pts); samples = 0; }
            var position = origin.Shift(TimelineTime.FromSamples(samples, rate));
            long clockError = checked(pts.Value - TimelineTime.Rebase(position, pts.Numerator, pts.Denominator, TimeRounding.NearestAwayFromZero).Value);
            if (started && Math.Abs(clockError) > 2)
            {
                if (new SourcePosition(pts).CompareTo(last) < 0)
                    throw new NotSupportedException("Backward audio timestamp discontinuity is ambiguous; split the source into explicit epochs.");
                origin = position.Shift(TimelineTime.Convert(new(clockError, pts.Numerator, pts.Denominator)));
                samples = 0; position = origin;
            }
            if (!started || position.Since(anchors[^1].Position).Ticks >= 2 * SequenceTime.TicksPerSecond)
            {
                if (anchors.Count >= 12000) throw new NotSupportedException("Audio seek index exceeds the supported six-hour anchor budget.");
                anchors.Add(new(pts, position, origin, samples));
            }
            samples = checked(samples + reader.SourceSampleCount);
            last = position; started = true;
        }
        if (anchors.Count == 0) throw new InvalidDataException("Audio stream contains no decoded frames.");
        // Leave resampler/codec preroll before the requested samples, including requests exactly on an anchor.
        var before = target.Shift(new(-SequenceTime.TicksPerSecond));
        return anchors.LastOrDefault(a => a.Position.CompareTo(before) <= 0) ?? anchors[0];
    }

    internal static SourcePosition DecoderStart(MediaAsset asset, AssetStream stream, MediaTimestamp anchor)
    {
        var position = new SourcePosition(anchor);
        // PCM can start at an exact sample. Seeking to an arbitrary earlier sample in WAV repacketizes the
        // stream around that earlier position, so no decoded packet may start at the saved anchor.
        // The chosen anchor is already before the target, which leaves resampler warm-up.
        return asset.Container == "wav" && stream.Codec.StartsWith("pcm_", StringComparison.Ordinal)
            ? position : position.Shift(new(-SequenceTime.TicksPerSecond));
    }

    internal static InvalidDataException SkippedAnchor(MediaAsset asset, MediaTimestamp expected, MediaTimestamp actual) =>
        new($"Audio decoder skipped the indexed anchor in {asset.Name} ({asset.Container}): expected {expected.Value}, decoded {actual.Value}.");
}
