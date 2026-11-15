using System.Collections.Immutable;
using Valencia.Core;
using Valencia.Core.Timeline;
using SkiaSharp;

namespace Valencia.Media;

public sealed record SequenceFrame(SequenceTime Time, int Width, int Height, byte[] Rgba, ImmutableArray<Guid> VideoClips,
    ImmutableArray<MediaTimestamp>? SourceTimestamps = null)
{
    public long ReadyHostTicks { get; init; }
    public double EvaluationMilliseconds { get; init; }
}

/// <summary>Worker-confined source resolution shared verbatim by preview and offline export. Owns at most 8 active cursors of each kind.</summary>
public sealed class SequenceEvaluator : IDisposable
{
    private readonly TimelineQueries queries;
    private readonly Guid sequenceId;
    private readonly long[] boundaries;
    private readonly Dictionary<Guid, VideoCursor> videos = [];
    private readonly Dictionary<Guid, AudioCursor> audios = [];
    private readonly OutputProfile? profile;
    private readonly SKTypeface? captionFont;
    private readonly CaptionIndex captionIndex;
    private readonly bool useProxies;
    private SequenceTime? audioThrough;
    /// <summary>Diagnostic work count for currently retained cursors, including frames skipped during seek.</summary>
    public long ActiveVideoDecodedFrames => videos.Values.Sum(v => v.DecodedFrames);
    /// <summary>Cursor decode work since the last discontinuous seek; excludes progressive index construction.</summary>
    public long ActiveAudioDecodedFrames => audios.Values.Sum(a => a.DecodedFrames);
    public SequenceDocument Sequence { get; }
    public SequenceEvaluator(ProjectSnapshot snapshot, Guid sequenceId, OutputProfile? profile = null, bool useProxies = false)
    {
        DocumentValidator.Validate(snapshot.Document);
        FfmpegRuntime.Initialize();
        queries = new(snapshot); this.sequenceId = sequenceId; Sequence = queries.Sequence(sequenceId);
        this.profile = profile;
        this.useProxies = useProxies;
        captionIndex = new(Sequence);
        if (Sequence.Creator is { Words.IsEmpty: false } creator) captionFont = CreatorRenderer.OpenFont(creator.CaptionStyle.Font);
        if (Sequence.AudioSampleRate is < 8000 or > 192000) throw new NotSupportedException("M1b audio supports sample rates from 8 kHz through 192 kHz.");
        if ((long)Sequence.Video.Width * Sequence.Video.Height > 8_294_400 || Sequence.Video.Width % 2 != 0 || Sequence.Video.Height % 2 != 0)
            throw new NotSupportedException("M1b supports even sequence dimensions up to 3840x2160 pixels.");
        boundaries = [.. Sequence.Tracks.SelectMany(t => t.Clips).SelectMany(c => new[] { c.Start.Ticks, c.End.Ticks }).Distinct().Order()];
    }

    public SequenceFrame Video(SequenceTime time, CancellationToken cancellation)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var evaluated = queries.Evaluate(sequenceId, time);
        var active = evaluated.Where(c => c.Kind == TrackKind.Video).ToArray();
        Limit(active.Length);
        Prune(videos, active.Select(a => a.Clip.Id));
        int width = profile?.Width ?? Sequence.Video.Width, height = profile?.Height ?? Sequence.Video.Height;
        var pixels = new byte[checked(width * height * 4)];
        var timestamps = ImmutableArray.CreateBuilder<MediaTimestamp>();
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        foreach (var a in active) // higher track order is on top
        {
            cancellation.ThrowIfCancellationRequested();
            if (!videos.TryGetValue(a.Clip.Id, out var cursor)) videos.Add(a.Clip.Id, cursor = new(a, Sequence.AudioSampleRate, useProxies));
            var source = cursor.At(a.SourcePosition, cancellation);
            if (source is not null)
            {
                timestamps.Add(source.SourceTimestamp);
                var effects = a.Clip.Properties.Video;
                float opacity = (float)a.Clip.Properties.Opacity;
                if (effects is not null) opacity *= (float)CaptionEvaluation.Fade(time.Ticks - a.Clip.Start.Ticks, a.Clip.Duration.Ticks, effects.FadeInTicks, effects.FadeOutTicks);
                if (effects is null && profile is null) Composite(pixels, width, height, source, opacity);
                else CreatorRenderer.Composite(pixels, width, height, source, opacity, effects ?? new(), profile, time);
            }
        }
        if (Sequence.Creator is { } creator && captionIndex.At(evaluated) is { } line)
            CreatorRenderer.Captions(pixels, width, height, line, creator.CaptionStyle, profile?.CaptionY, captionFont!);
        return new(time, width, height, pixels, [.. active.Select(a => a.Clip.Id)], timestamps.ToImmutable())
        {
            ReadyHostTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            EvaluationMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds
        };
    }

    public float[] Audio(SequenceTime origin, long firstSample, int count, CancellationToken cancellation)
    {
        if (count is < 0 or > 262144) throw new ArgumentOutOfRangeException(nameof(count), "Request audio in bounded blocks of at most 262144 stereo frames.");
        int rate = Sequence.AudioSampleRate;
        var requested = new SequenceTime(checked(origin.Ticks + TimelineTime.FromSamples(firstSample, rate).Ticks));
        // Discontinuous queries must seek, rather than decoding all intervening media.
        // The one-second locality threshold is an exact duration, not nominal source-FPS arithmetic.
        if (audioThrough is { } previous && (requested.Ticks < previous.Ticks || requested.Ticks - previous.Ticks > SequenceTime.TicksPerSecond))
        { foreach (var cursor in audios.Values) cursor.Dispose(); audios.Clear(); }
        audioThrough = new(checked(requested.Ticks + TimelineTime.FromSamples(count, rate).Ticks));
        var output = new float[checked(count * 2)];
        int written = 0;
        while (written < count)
        {
            cancellation.ThrowIfCancellationRequested();
            long sample = checked(firstSample + written);
            var time = new SequenceTime(checked(origin.Ticks + TimelineTime.FromSamples(sample, rate).Ticks));
            int boundaryIndex = Array.BinarySearch(boundaries, time.Ticks);
            boundaryIndex = boundaryIndex >= 0 ? boundaryIndex + 1 : ~boundaryIndex;
            int run = count - written;
            if (boundaryIndex < boundaries.Length)
            {
                long next = TimelineTime.SampleIndex(new(checked(boundaries[boundaryIndex] - origin.Ticks)), rate, TimeRounding.Ceiling);
                run = (int)Math.Min(run, Math.Max(1, next - sample));
            }
            var active = queries.Evaluate(sequenceId, time).Where(c => c.Kind == TrackKind.Audio).ToArray();
            Limit(active.Length); Prune(audios, active.Select(a => a.Clip.Id));
            foreach (var a in active)
            {
                if (!audios.TryGetValue(a.Clip.Id, out var cursor)) audios.Add(a.Clip.Id, cursor = new(a, rate, cancellation));
                float[] clipSamples = new float[run * 2];
                cursor.Mix(a.SourcePosition.SampleIndex(rate, TimeRounding.NearestAwayFromZero), clipSamples, 1, cancellation);
                var processing = Sequence.Creator?.Audio.FirstOrDefault(t => t.TrackId == a.TrackId);
                long step = TimelineTime.FromSamples(1, rate).Ticks;
                double baseGain = (double)a.Clip.Properties.Gain * Math.Pow(10, (processing?.GainDb ?? 0) / 20);
                for (int frame = 0; frame < run; frame++)
                {
                    var at = new SequenceTime(checked(time.Ticks + frame * step));
                    double gain = baseGain * (processing is null || processing.Envelope.IsEmpty ? 1 : Math.Pow(10, CaptionEvaluation.GainAt(processing.Envelope, at) / 20));
                    if (a.Clip.Properties.Audio is { } effects) gain *= CaptionEvaluation.Fade(at.Ticks - a.Clip.Start.Ticks, a.Clip.Duration.Ticks, effects.FadeInTicks, effects.FadeOutTicks);
                    for (int channel = 0; channel < 2; channel++) output[(written + frame) * 2 + channel] += clipSamples[frame * 2 + channel] * (float)gain;
                }
            }
            written += run;
        }
        float master = (float)Math.Pow(10, (Sequence.Creator?.MasterGainDb ?? 0) / 20);
        float ceiling = (float)Math.Pow(10, (Sequence.Creator?.PeakCeilingDb ?? 0) / 20);
        for (int i = 0; i < output.Length; i++) output[i] = Math.Clamp(output[i] * master, -ceiling, ceiling);
        return output;
    }

    private static void Composite(byte[] output, int width, int height, VideoFrame source, float opacity)
    {
        if (opacity <= 0) return;
        if (source.Width == width && source.Height == height && opacity == 1) { source.Rgba.CopyTo(output, 0); return; }
        double scale = Math.Min((double)width / source.Width, (double)height / source.Height);
        int w = Math.Max(1, (int)(source.Width * scale)), h = Math.Max(1, (int)(source.Height * scale));
        int left = (width - w) / 2, top = (height - h) / 2;
        // Each layer has an opaque black letterbox, matching full-sequence layer bounds.
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int d = (y * width + x) * 4;
            bool inside = x >= left && x < left + w && y >= top && y < top + h;
            int s = inside ? (((y - top) * source.Height / h) * source.Width + (x - left) * source.Width / w) * 4 : 0;
            for (int c = 0; c < 3; c++) output[d + c] = (byte)Math.Clamp((inside ? source.Rgba[s + c] : 0) * opacity + output[d + c] * (1 - opacity), 0, 255);
        }
    }
    private static void Limit(int count) { if (count > 8) throw new NotSupportedException("M1b supports up to eight simultaneous clips per media kind."); }
    private static void Prune<T>(Dictionary<Guid, T> cursors, IEnumerable<Guid> active) where T : IDisposable
    {
        var keep = active.ToHashSet();
        foreach (var id in cursors.Keys.Where(k => !keep.Contains(k)).ToArray()) { cursors[id].Dispose(); cursors.Remove(id); }
    }
    public void Dispose() { foreach (var v in videos.Values) v.Dispose(); foreach (var a in audios.Values) a.Dispose(); videos.Clear(); audios.Clear(); captionFont?.Dispose(); }

    private sealed class VideoCursor : IDisposable
    {
        private FfmpegDecoder decoder;
        private readonly ActiveClip clip;
        private readonly int rate;
        private ProxySource? proxy;
        public VideoCursor(ActiveClip clip, int rate, bool useProxy)
        {
            this.clip = clip; this.rate = rate;
            proxy = useProxy ? MediaProxy.Resolve(clip.Asset, clip.Stream) : null;
            try { decoder = new(proxy?.Asset ?? clip.Asset, proxy?.Stream ?? clip.Stream, clip.SourcePosition, rate); }
            catch (Exception error) when (proxy is not null && error is IOException or InvalidOperationException or NotSupportedException)
            { proxy = null; decoder = new(clip.Asset, clip.Stream, clip.SourcePosition, rate); }
        }
        private VideoFrame? current, next;
        private bool ended;
        private SourcePosition? requested;
        public long DecodedFrames { get; private set; }
        public VideoFrame? At(SourcePosition target, CancellationToken cancellation)
        {
            try { return ReadAt(target, cancellation); }
            catch (Exception error) when (proxy is not null && error is IOException or InvalidOperationException or NotSupportedException)
            {
                decoder.Dispose(); proxy = null;
                decoder = new(clip.Asset, clip.Stream, target, rate);
                current = null; next = null; ended = false; requested = null;
                return ReadAt(target, cancellation);
            }
        }
        private VideoFrame? ReadAt(SourcePosition target, CancellationToken cancellation)
        {
            if (requested is { } previous && (target.CompareTo(previous) < 0 || target.Since(previous).Ticks > SequenceTime.TicksPerSecond))
            {
                decoder.Dispose(); decoder = new(proxy?.Asset ?? clip.Asset, proxy?.Stream ?? clip.Stream, target, rate);
                current = null; next = null; ended = false;
            }
            requested = target;
            while (true)
            {
                if (next is null && !ended)
                {
                    if (decoder.Read(cancellation)) { DecodedFrames++; next = decoder.CopyVideo(0); } else ended = true;
                }
                if (next is null || new SourcePosition(next.SourceTimestamp).CompareTo(target) > 0)
                {
                    if (ended && current?.Duration is { } length)
                    {
                        var end = new MediaTimestamp(checked(current.SourceTimestamp.Value + length.Value), length.Numerator, length.Denominator);
                        if (new SourcePosition(end).CompareTo(target) <= 0) return null;
                    }
                    return current;
                }
                current = next; next = null;
            }
        }
        public void Dispose() => decoder.Dispose();
    }
    private sealed class AudioCursor : IDisposable
    {
        public long DecodedFrames { get; private set; }
        private readonly FfmpegDecoder decoder;
        private readonly AudioSeekIndex.Anchor anchor;
        private readonly int rate;
        private readonly AudioFilter? filter;
        private float[] block = [];
        private long blockStart;
        private bool initialized, ended;
        public AudioCursor(ActiveClip clip, int rate, CancellationToken cancellation)
        {
            this.rate = rate;
            anchor = AudioSeekIndex.Resolve(clip.Asset, clip.Stream, clip.SourcePosition, cancellation);
            var preroll = new SourcePosition(anchor.Pts).Shift(new(-SequenceTime.TicksPerSecond));
            decoder = new(clip.Asset, clip.Stream, preroll, rate);
            if (clip.Clip.Properties.Audio?.NoiseReductionDb is > 0 and var reduction) filter = new(rate, reduction);
        }
        public void Mix(long target, Span<float> destination, float gain, CancellationToken cancellation)
        {
            int written = 0;
            while (written < destination.Length / 2)
            {
                cancellation.ThrowIfCancellationRequested();
                if (block.Length == 0 || target >= blockStart + block.Length / 2)
                {
                    if (filter is not null)
                    {
                        while (true)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var clean = filter.Pull(out long cleanPts);
                            if (clean is not null) { block = clean; blockStart = cleanPts; break; }
                            if (ended) return;
                            if (!ReadFiltered(cancellation)) { filter.Finish(); ended = true; }
                        }
                        continue;
                    }
                    if (ended || !ReadPcm(out block, out blockStart, cancellation)) { ended = true; return; }
                    continue;
                }
                if (target < blockStart)
                {
                    int silent = (int)Math.Min(blockStart - target, destination.Length / 2 - written);
                    written += silent; target += silent; continue;
                }
                int offset = checked((int)(target - blockStart));
                int count = Math.Min(block.Length / 2 - offset, destination.Length / 2 - written);
                for (int i = 0; i < count * 2; i++) destination[written * 2 + i] += block[offset * 2 + i] * gain;
                written += count; target += count;
            }
        }
        private SourcePosition inputOrigin;
        private long inputSamples, nextOutput;
        private bool pendingFrame, inputEnded;
        private int inputRate;
        private bool ReadPcm(out float[] pcm, out long position, CancellationToken cancellation)
        {
            pcm = []; position = nextOutput;
            while (!inputEnded)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!pendingFrame)
                {
                    if (!decoder.Read(cancellation))
                    {
                        pcm = decoder.DrainAudio(); position = nextOutput; nextOutput += pcm.Length / 2;
                        if (pcm.Length == 0) inputEnded = true;
                        return pcm.Length > 0;
                    }
                    DecodedFrames++;
                    if (!initialized)
                    {
                        int compared = new SourcePosition(decoder.Timestamp).CompareTo(new(anchor.Pts));
                        if (compared < 0) continue;
                        if (compared > 0) throw new InvalidDataException("Decoder skipped the exact indexed audio anchor.");
                        inputOrigin = anchor.Position; inputSamples = 0; inputRate = decoder.SourceSampleRate;
                        nextOutput = inputOrigin.SampleIndex(rate, TimeRounding.NearestAwayFromZero); initialized = true;
                    }
                    var expected = inputOrigin.Shift(TimelineTime.FromSamples(inputSamples, inputRate));
                    var pts = decoder.Timestamp;
                    long difference = checked(pts.Value - TimelineTime.Rebase(expected, pts.Numerator, pts.Denominator, TimeRounding.NearestAwayFromZero).Value);
                    if (Math.Abs((decimal)difference) > 2 || decoder.SourceSampleRate != inputRate)
                    {
                        if (new SourcePosition(pts).CompareTo(expected) < 0) throw new NotSupportedException("Backward source audio discontinuity is ambiguous.");
                        if (filter is not null) throw new NotSupportedException("Noise cleanup across source timestamp discontinuities requires separate epochs; disable cleanup for this source.");
                        pcm = decoder.DrainAudio(); position = nextOutput;
                        decoder.ResetAudioConversion(); pendingFrame = true;
                        inputOrigin = expected.Shift(TimelineTime.Convert(new(difference, pts.Numerator, pts.Denominator)));
                        inputSamples = 0; inputRate = decoder.SourceSampleRate; nextOutput = inputOrigin.SampleIndex(rate, TimeRounding.NearestAwayFromZero);
                        if (pcm.Length > 0) return true;
                    }
                }
                pendingFrame = false;
                pcm = decoder.CopyAudio(); position = nextOutput;
                inputSamples = checked(inputSamples + decoder.SourceSampleCount); nextOutput = checked(nextOutput + pcm.Length / 2);
                if (pcm.Length > 0) return true;
            }
            return false;
        }
        private bool ReadFiltered(CancellationToken cancellation)
        {
            if (!ReadPcm(out var pcm, out long position, cancellation)) return false;
            filter!.Push(pcm, position); return true;
        }
        public void Dispose() { decoder.Dispose(); filter?.Dispose(); }
    }
}
