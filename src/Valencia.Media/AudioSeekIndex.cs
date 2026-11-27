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
    private readonly object work = new();
    private readonly int rate;
    private string? cacheKey;
    private int savedAnchors;
    private bool cacheChecked, reused, savedEnd;
    private SourcePosition origin;
    private long samples;
    private SourcePosition last;
    private bool started, ended;

    private AudioSeekIndex(MediaAsset asset, AssetStream stream)
    {
        this.asset = asset; this.stream = stream;
        rate = stream.SampleRate ?? throw new NotSupportedException("An audio sample rate is required for precise seeking.");
    }

    private static AudioSeekIndex Get(MediaAsset asset, AssetStream stream)
    {
        var file = new FileInfo(asset.SourcePath);
        string key = $"{ValenciaData.Root}|{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{stream.Index}";
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
            return index;
        }
    }

    public static Anchor Resolve(MediaAsset asset, AssetStream stream, SourcePosition target, CancellationToken cancellation)
    {
        var index = Get(asset, stream);
        lock (index.work)
        {
            index.TryLoad(cancellation);
            try { return index.Find(target, cancellation); }
            finally { index.TrySave(force: false); }
        }
    }

    public static SourceIndexData Build(MediaAsset asset, AssetStream stream, SourcePosition end, CancellationToken cancellation)
    {
        var index = Get(asset, stream);
        // Background construction yields between bounded spans so a seek inside the already
        // indexed region can use its anchors while the worker is still counting later samples.
        while (true)
        {
            lock (index.work)
            {
                index.TryLoad(cancellation);
                var boundary = index.started ? index.last : new SourcePosition(stream.Start ?? new(0, stream.TimeBaseNumerator, stream.TimeBaseDenominator));
                boundary = boundary.Shift(new(120 * SequenceTime.TicksPerSecond));
                if (end.CompareTo(boundary) < 0) boundary = end;
                try { index.Find(boundary, cancellation); }
                finally
                {
                    bool reachedEnd = index.ended || (index.started && index.last.CompareTo(end) >= 0);
                    index.TrySave(force: !cancellation.IsCancellationRequested && reachedEnd);
                }
                if (index.ended || index.last.CompareTo(end) >= 0)
                    return new(stream.Index, "Progressive original decoded sample-count anchors at two-second intervals; forward epochs preserved",
                        index.last, index.anchors.Count, index.reused, index.savedAnchors);
            }
            Thread.Yield();
        }
    }

    private void TryLoad(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (cacheChecked) return;
        cacheChecked = true;
        cacheKey = AudioSeekStorage.Key(asset, stream);
        if (cacheKey is null || AudioSeekStorage.Read(cacheKey) is not { } saved) return;
        anchors.AddRange(saved.Anchors);
        started = reused = true;
        savedAnchors = anchors.Count;
        last = saved.Through;
        ended = savedEnd = saved.Ended;
    }

    private void TrySave(bool force)
    {
        if (cacheKey is null || (anchors.Count == savedAnchors && ended == savedEnd) || anchors.Count == 0
            || (!force && !ended && anchors.Count - savedAnchors < 256)) return;
        // Only a verified anchor is a durable resume point until EOF has been proven.
        var through = ended ? last : anchors[^1].Position;
        if (cacheKey != AudioSeekStorage.Key(asset, stream)) return;
        if (AudioSeekStorage.Write(cacheKey, new(anchors, through, ended)))
        {
            savedAnchors = anchors.Count;
            savedEnd = ended;
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
                if (anchors.Count >= AudioSeekStorage.MaximumAnchors)
                    throw new NotSupportedException("Audio seek index exceeds its 12,000-anchor budget.");
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
