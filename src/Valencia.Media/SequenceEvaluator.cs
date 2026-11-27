using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Valencia.Core;
using Valencia.Core.Timeline;
using SkiaSharp;

namespace Valencia.Media;

public sealed record SequenceFrame(SequenceTime Time, int Width, int Height, byte[] Rgba, ImmutableArray<Guid> VideoClips,
    ImmutableArray<MediaTimestamp>? SourceTimestamps = null)
{
    public long ReadyHostTicks { get; init; }
    public double EvaluationMilliseconds { get; init; }
    public IReadOnlyDictionary<Guid, MediaTimestamp>? DecodedByClip { get; init; }
    public CaptionLine? ReviewCaption { get; init; }
    public VideoDecodeEvidence? PrimaryDecode { get; init; }
    public bool PrimaryUsesProxy { get; init; }
    public bool UsedOwnedSourcePixels { get; init; }
}

public sealed record EffectTransfers(long Dispatches, long SourceUploads, long TargetUploads, long TargetReadbacks);

/// <summary>Worker-confined source resolution shared verbatim by preview and offline export. Owns at most 8 active cursors of each kind.</summary>
public sealed class SequenceEvaluator : IDisposable
{
    private static readonly VideoEffects IdentityEffects = new();
    private readonly TimelineQueries queries;
    private readonly Guid sequenceId;
    private readonly long[] boundaries;
    private readonly Dictionary<Guid, VideoCursor> videos = [];
    private readonly Dictionary<Guid, AudioCursor> audios = [];
    private readonly OutputProfile? profile;
    private readonly SKTypeface? captionFont;
    private readonly CaptionIndex captionIndex;
    private readonly bool burnCaptions;
    private TitleRenderer? titleRenderer;
    private byte[]? dissolveBuffer;
    private readonly bool useProxies;
    private readonly bool retainVideoHistory;
    private readonly bool requestHardware;
    private readonly bool gpuEffects;
    private VulkanClipEffects? effectDevice;
    private SequenceTime? audioThrough;
    private TransitionWindow[]? previewTransitions;
    private (Guid Clip, Task<VideoCursor> Cursor)? warmVideo;
    /// <summary>Diagnostic work count for currently retained cursors, including frames skipped during seek.</summary>
    public long ActiveVideoDecodedFrames => videos.Values.Sum(v => v.DecodedFrames);
    public long ActiveVideoConvertedFrames => videos.Values.Sum(v => v.ConvertedFrames);
    public long ActiveVideoHistoryHits => videos.Values.Sum(v => v.HistoryHits);
    public long ActiveVideoSeeks => videos.Values.Sum(v => v.Seeks);
    /// <summary>Cursor decode work since the last discontinuous seek; excludes progressive index construction.</summary>
    public long ActiveAudioDecodedFrames => audios.Values.Sum(a => a.DecodedFrames);
    public SequenceDocument Sequence { get; }
    public string? EffectGpu => effectDevice?.DeviceName;
    public bool EffectValidationAvailable => effectDevice?.ValidationAvailable == true;
    public int EffectValidationErrors => effectDevice?.ValidationErrors ?? 0;
    public EffectTransfers? EffectTransferTotals => effectDevice is { } device
        ? new(device.Dispatches, device.SourceUploads, device.TargetUploads, device.TargetReadbacks) : null;
    public static void PrepareEffectShader(string path) => ClipEffectShader.Prepare(path);
    public SequenceEvaluator(ProjectSnapshot snapshot, Guid sequenceId, OutputProfile? profile = null,
        bool useProxies = false, bool retainVideoHistory = false, bool? useVulkanEffects = null, bool burnCaptions = true,
        bool? useHardwareDecode = null)
    {
        DocumentValidator.Validate(snapshot.Document);
        FfmpegRuntime.Initialize();
        queries = new(snapshot); this.sequenceId = sequenceId; Sequence = queries.Sequence(sequenceId);
        this.profile = profile;
        this.useProxies = useProxies;
        this.retainVideoHistory = retainVideoHistory;
        // Reverse playback and interactive scrub currently reuse the software picture history.
        requestHardware = useHardwareDecode ?? (!retainVideoHistory && VideoDecodeProvider.Requested);
        gpuEffects = useVulkanEffects ?? string.Equals(Environment.GetEnvironmentVariable("VALENCIA_EFFECTS_BACKEND"), "vulkan", StringComparison.OrdinalIgnoreCase);
        captionIndex = new(Sequence);
        this.burnCaptions = burnCaptions;
        if (burnCaptions && Sequence.Creator is { Words.IsEmpty: false } creator) captionFont = CreatorRenderer.OpenFont(creator.CaptionStyle.Font);
        if (Sequence.AudioSampleRate is < 8000 or > 192000) throw new NotSupportedException("M1b audio supports sample rates from 8 kHz through 192 kHz.");
        if ((long)Sequence.Video.Width * Sequence.Video.Height > 8_294_400 || Sequence.Video.Width % 2 != 0 || Sequence.Video.Height % 2 != 0)
            throw new NotSupportedException("M1b supports even sequence dimensions up to 3840x2160 pixels.");
        boundaries = [.. Sequence.Tracks.SelectMany(t => t.Clips).SelectMany(c => new[] { c.Start.Ticks, c.End.Ticks })
            .Concat(queries.TransitionBoundaries(sequenceId).Select(time => time.Ticks)).Distinct().Order()];
    }

    public SequenceFrame Video(SequenceTime time, CancellationToken cancellation, bool includeReviewMetadata = false)
        => VideoAt(time, cancellation, includeReviewMetadata, transferPixels: false);

    internal SequenceFrame VideoForTransport(SequenceTime time, CancellationToken cancellation)
        => VideoAt(time, cancellation, includeReviewMetadata: false, transferPixels: !retainVideoHistory);

    private SequenceFrame VideoAt(SequenceTime time, CancellationToken cancellation, bool includeReviewMetadata, bool transferPixels)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        effectDevice?.BeginFrame();
        var evaluated = queries.Evaluate(sequenceId, time);
        var active = evaluated.Where(c => c.Kind == TrackKind.Video).ToArray();
        Limit(active.Length);
        Prune(videos, active.Select(a => a.Clip.Id));
        int width = profile?.Width ?? Sequence.Video.Width, height = profile?.Height ?? Sequence.Video.Height;
        var timestamps = ImmutableArray.CreateBuilder<MediaTimestamp>();
        var decoded = includeReviewMetadata ? new Dictionary<Guid, MediaTimestamp>() : null;
        CaptionLine? caption = null;
        VideoDecodeEvidence? primaryDecode = null;
        bool primaryUsesProxy = false;
        byte[]? pixels = null;
        if (transferPixels && profile is null &&
            active is [{ Transition: null, Clip: { Title: null, Properties: { Opacity: 1, Video: null } } } single])
        {
            cancellation.ThrowIfCancellationRequested();
            var cursor = Cursor(single);
            if (cursor.At(single.SourcePosition, cancellation) is { } picture && picture.Width == width && picture.Height == height
                && cursor.TryTakePixels(picture))
            {
                pixels = picture.Rgba;
                Describe(single, picture, cursor.UsesProxy);
            }
        }
        bool usedOwnedSourcePixels = pixels is not null;
        if (pixels is null)
        {
            pixels = new byte[checked(width * height * 4)];
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            for (int i = 0; i < active.Length; i++) // higher track order is on top
            {
                var layer = active[i];
                if (layer.Transition is { Incoming: false } transition)
                {
                    effectDevice?.Flush();
                    // Composite each side onto the same lower tracks, then interpolate the results.
                    // This preserves the lower tracks' contribution for both fractional clip opacities.
                    var incoming = dissolveBuffer ??= new byte[pixels.Length];
                    pixels.CopyTo(incoming, 0);
                    Draw(layer, pixels);
                    effectDevice?.Flush();
                    Draw(active[++i], incoming);
                    effectDevice?.Flush();
                    double weight = transition.Progress(time);
                    for (int offset = 0; offset < pixels.Length; offset++)
                        pixels[offset] = (byte)Math.Round(pixels[offset] + (incoming[offset] - pixels[offset]) * weight);
                }
                else Draw(layer, pixels);
            }
        }
        effectDevice?.Flush();
        if (burnCaptions && Sequence.Creator is { } creator && captionIndex.At(evaluated.Where(clip =>
            clip.Transition is null || (clip.Clip.Start.Ticks <= time.Ticks && time.Ticks < clip.Clip.End.Ticks))) is { } line)
        {
            CreatorRenderer.Captions(pixels, width, height, line, creator.CaptionStyle, profile?.CaptionY, captionFont!);
            caption = line;
        }
        return new(time, width, height, pixels, [.. active.Select(a => a.Clip.Id)], timestamps.ToImmutable())
        {
            ReadyHostTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            EvaluationMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            DecodedByClip = decoded,
            ReviewCaption = includeReviewMetadata ? caption : null,
            PrimaryDecode = primaryDecode,
            PrimaryUsesProxy = primaryUsesProxy,
            UsedOwnedSourcePixels = usedOwnedSourcePixels
        };

        VideoCursor Cursor(ActiveClip a)
        {
            if (videos.TryGetValue(a.Clip.Id, out var cursor)) return cursor;
            if (warmVideo is { } warmed && warmed.Clip == a.Clip.Id)
            {
                warmVideo = null;
                cursor = warmed.Cursor.GetAwaiter().GetResult();
            }
            else cursor = new(a.Asset, a.Stream, a.SourcePosition, Sequence.AudioSampleRate, useProxies, retainVideoHistory, requestHardware);
            videos.Add(a.Clip.Id, cursor);
            return cursor;
        }

        void Describe(ActiveClip clip, VideoFrame source, bool usesProxy)
        {
            if (timestamps.Count == 0)
            {
                primaryDecode = source.Decode;
                primaryUsesProxy = usesProxy;
            }
            timestamps.Add(source.SourceTimestamp);
            if (decoded is not null) decoded[clip.Clip.Id] = source.SourceTimestamp;
        }

        void Draw(ActiveClip a, byte[] target)
        {
            cancellation.ThrowIfCancellationRequested();
            if (a.Clip.Title is { } title)
            {
                effectDevice?.Flush();
                // Titles have no decoder: they draw at their track's position, over lower tracks and under higher ones.
                float fade = a.Clip.Properties.Video is { } titleEffects
                    ? (float)CaptionEvaluation.Fade(time.Ticks - a.Clip.Start.Ticks, a.Clip.Duration.Ticks, titleEffects.FadeInTicks, titleEffects.FadeOutTicks) : 1;
                (titleRenderer ??= new TitleRenderer()).Draw(target, width, height, a.Clip, title, time, a.SourcePosition,
                    (float)a.Clip.Properties.Opacity * fade, cancellation);
                return;
            }
            var cursor = Cursor(a);
            var source = cursor.At(a.SourcePosition, cancellation);
            if (source is not null)
            {
                Describe(a, source, cursor.UsesProxy);
                var effects = a.Clip.Properties.Video;
                float opacity = (float)a.Clip.Properties.Opacity;
                double rotation = 0;
                if (effects is not null) opacity *= (float)CaptionEvaluation.Fade(time.Ticks - a.Clip.Start.Ticks, a.Clip.Duration.Ticks, effects.FadeInTicks, effects.FadeOutTicks);
                if (effects?.Transform is { } transform)
                {
                    var pose = transform.Curve.At(a.SourcePosition.Since(transform.Origin).Ticks);
                    opacity *= (float)pose.Opacity;
                    rotation = pose.Rotation % 360;
                    var transformed = effects with { X = pose.X, Y = pose.Y, Scale = pose.Scale, Transform = null };
                    effects = transformed == IdentityEffects ? null : transformed;
                }
                if (effects?.Motion is { } motion)
                {
                    var animated = motion.Apply(effects with { Motion = null }, time.Ticks - a.Clip.Start.Ticks, a.Clip.Duration.Ticks);
                    effects = animated == IdentityEffects ? null : animated;
                }
                if (effects is null && profile is null && rotation == 0)
                {
                    effectDevice?.Flush();
                    Composite(target, width, height, source, opacity);
                }
                else
                {
                    var device = gpuEffects && rotation == 0 && opacity > 0 && effects is not null && (effects.Region is not null || ClipEffectFrame.HasColor(effects))
                        ? effectDevice ??= new VulkanClipEffects() : null;
                    if (device is null) effectDevice?.Flush();
                    CreatorRenderer.Composite(target, width, height, source, a.Stream, opacity, effects ?? IdentityEffects, profile, time, device, cancellation, rotation);
                }
            }
        }
    }

    /// <summary>Warm one cold transition input off the playback worker before that input becomes visible.</summary>
    internal void PrepareTransition(SequenceTime time, bool reverse, bool initial, CancellationToken cancellation)
    {
        if (warmVideo is not null) return;
        long lead = SequenceTime.TicksPerSecond / 2;
        previewTransitions ??= reverse ? [.. queries.VisibleVideoTransitions(sequenceId).OrderByDescending(window => window.End.Ticks)]
            : [.. queries.VisibleVideoTransitions(sequenceId).OrderBy(window => window.Start.Ticks)];
        foreach (var window in previewTransitions)
        {
            long distance = reverse ? time.Ticks - window.End.Ticks : window.Start.Ticks - time.Ticks;
            if (distance < 0 || distance > lead) continue;
            var clip = reverse ? window.Outgoing : window.Incoming;
            if (clip.IsTitle() || videos.ContainsKey(clip.Id)) continue;
            var source = reverse ? clip.SourceOut.Shift(new(window.Transition.AfterTicks - 1))
                : clip.SourceIn.Shift(new(-window.Transition.BeforeTicks));
            var asset = queries.Assets.First(item => item.Id == clip.AssetId);
            var stream = asset.Streams.First(item => item.Index == clip.StreamIndex);
            warmVideo = (clip.Id, Task.Run(() =>
            {
                var cursor = new VideoCursor(asset, stream, source, Sequence.AudioSampleRate, useProxies, retainVideoHistory, requestHardware);
                try
                {
                    cursor.At(source, cancellation);
                    return cursor;
                }
                catch { cursor.Dispose(); throw; }
            }, cancellation));
            // If playback is launched immediately before a dissolve, prime both sides before starting the audio clock.
            if (initial) warmVideo.Value.Cursor.GetAwaiter().GetResult();
            return;
        }
    }

    public float[] Audio(SequenceTime origin, long firstSample, int count, CancellationToken cancellation)
        => Audio(origin, firstSample, count, cancellation, soloAudioTracks: null);

    internal float[] Audio(SequenceTime origin, long firstSample, int count, CancellationToken cancellation,
        ImmutableHashSet<Guid>? soloAudioTracks)
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
            var active = queries.Evaluate(sequenceId, time).Where(c => c.Kind == TrackKind.Audio
                && (soloAudioTracks is not { Count: > 0 } || soloAudioTracks.Contains(c.TrackId))).ToArray();
            Limit(active.Length); Prune(audios, active.Select(a => a.Clip.Id));
            foreach (var a in active)
            {
                if (!audios.TryGetValue(a.Clip.Id, out var cursor)) audios.Add(a.Clip.Id, cursor = new(a, rate, cancellation));
                float[] clipSamples = new float[run * 2];
                long sourceSample = a.SourcePosition.SampleIndex(rate, TimeRounding.NearestAwayFromZero);
                if (a.Clip.Properties.Audio?.AlignStartToSample == true)
                {
                    long first = TimelineTime.SampleIndex(new(checked(a.Clip.Start.Ticks - origin.Ticks)), rate, TimeRounding.Ceiling);
                    sourceSample = checked(a.Clip.SourceIn.SampleIndex(rate, TimeRounding.NearestAwayFromZero) + sample - first);
                }
                cursor.Mix(sourceSample, clipSamples, 1, cancellation);
                var processing = Sequence.Creator?.Audio.FirstOrDefault(t => t.TrackId == a.TrackId);
                long step = TimelineTime.FromSamples(1, rate).Ticks;
                double baseGain = (double)a.Clip.Properties.Gain * Math.Pow(10, (processing?.GainDb ?? 0) / 20);
                for (int frame = 0; frame < run; frame++)
                {
                    var at = new SequenceTime(checked(time.Ticks + frame * step));
                    double gain = baseGain * (processing is null || processing.Envelope.IsEmpty ? 1 : Math.Pow(10, CaptionEvaluation.GainAt(processing.Envelope, at) / 20));
                    if (a.Clip.Properties.Audio is { } effects) gain *= CaptionEvaluation.Fade(at.Ticks - a.Clip.Start.Ticks, a.Clip.Duration.Ticks, effects.FadeInTicks, effects.FadeOutTicks);
                    if (a.Transition is { } transition) gain *= transition.EqualPower(at);
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

    /// <summary>Render the forward sample range ending just before endSample, then reverse stereo frames.</summary>
    internal float[] ReverseAudio(long endSample, int count, CancellationToken cancellation, ImmutableHashSet<Guid>? soloAudioTracks = null)
    {
        if (count < 0 || endSample < count) throw new ArgumentOutOfRangeException(nameof(count));
        var samples = Audio(default, endSample - count, count, cancellation, soloAudioTracks);
        for (int left = 0, right = samples.Length - 2; left < right; left += 2, right -= 2)
        {
            (samples[left], samples[right]) = (samples[right], samples[left]);
            (samples[left + 1], samples[right + 1]) = (samples[right + 1], samples[left + 1]);
        }
        return samples;
    }

    private static void Composite(byte[] output, int width, int height, VideoFrame source, float opacity)
    {
        if (opacity <= 0) return;
        if (source.Width == width && source.Height == height && opacity == 1) { source.Rgba.CopyTo(output, 0); return; }
        double scale = Math.Min((double)width / source.Width, (double)height / source.Height);
        int w = Math.Max(1, (int)(source.Width * scale)), h = Math.Max(1, (int)(source.Height * scale));
        int left = (width - w) / 2, top = (height - h) / 2;
        if (opacity == 1 && BitConverter.IsLittleEndian)
        {
            CompositeOpaque(output, width, height, source, w, h, left, top);
            return;
        }
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

    private static void CompositeOpaque(byte[] output, int width, int height, VideoFrame source, int w, int h, int left, int top)
    {
        const uint alpha = 0xff000000;
        // Preserve the old integer nearest-neighbor coordinates and the destination alpha.
        // A 640px proxy otherwise pays three blend and clamp operations for every output pixel.
        var destination = MemoryMarshal.Cast<byte, uint>(output.AsSpan());
        var input = MemoryMarshal.Cast<byte, uint>(source.Rgba.AsSpan());
        Span<int> columns = w <= 4096 ? stackalloc int[w] : new int[w];
        for (int x = 0; x < w; x++) columns[x] = (int)((long)x * source.Width / w);
        for (int y = 0; y < height; y++)
        {
            var row = destination.Slice(y * width, width);
            if (y < top || y >= top + h)
            {
                ClearRgb(row);
                continue;
            }
            ClearRgb(row[..left]);
            var center = row.Slice(left, w);
            var sourceRow = input.Slice((int)((long)(y - top) * source.Height / h) * source.Width, source.Width);
            for (int x = 0; x < w; x++) center[x] = (center[x] & alpha) | (sourceRow[columns[x]] & ~alpha);
            ClearRgb(row[(left + w)..]);
        }

        static void ClearRgb(Span<uint> pixels)
        {
            for (int x = 0; x < pixels.Length; x++) pixels[x] &= alpha;
        }
    }
    private static void Limit(int count) { if (count > 8) throw new NotSupportedException("M1b supports up to eight simultaneous clips per media kind."); }
    private static void Prune<T>(Dictionary<Guid, T> cursors, IEnumerable<Guid> active) where T : IDisposable
    {
        var keep = active.ToHashSet();
        foreach (var id in cursors.Keys.Where(k => !keep.Contains(k)).ToArray()) { cursors[id].Dispose(); cursors.Remove(id); }
    }
    public void Dispose()
    {
        if (warmVideo is { } warmed)
        {
            // A prepared input can be left unconsumed if playback is cancelled.
            // The producer owns both outcomes and the warm worker disposes its cursor on failure.
            try { warmed.Cursor.GetAwaiter().GetResult().Dispose(); }
            catch (Exception) when (warmed.Cursor.IsFaulted || warmed.Cursor.IsCanceled) { }
            warmVideo = null;
        }
        foreach (var video in videos.Values) video.Dispose();
        foreach (var audio in audios.Values) audio.Dispose();
        videos.Clear();
        audios.Clear();
        captionFont?.Dispose();
        titleRenderer?.Dispose();
        effectDevice?.Dispose();
    }

    private sealed class VideoCursor : IDisposable
    {
        private FfmpegDecoder decoder;
        private readonly MediaAsset asset;
        private readonly AssetStream stream;
        private readonly int rate;
        private ProxySource? proxy;
        private readonly bool retainHistory;
        private bool requestHardware;
        private string? hardwareFailure;
        public VideoCursor(MediaAsset asset, AssetStream stream, SourcePosition position, int rate, bool useProxy, bool retainHistory,
            bool requestHardware = false)
        {
            this.asset = asset; this.stream = stream; this.rate = rate;
            this.retainHistory = retainHistory;
            this.requestHardware = requestHardware;
            proxy = useProxy ? MediaProxy.ResolvePreview(asset, stream) : null;
            try { decoder = Open(position); }
            catch (Exception error) when (proxy is not null && error is IOException or InvalidOperationException or NotSupportedException)
            { proxy = null; decoder = Open(position); }
        }

        private FfmpegDecoder Open(SourcePosition target)
        {
            try { return new(proxy?.Asset ?? asset, proxy?.Stream ?? stream, target, rate, requestHardware && proxy is null); }
            catch (HardwareVideoDecodeException error)
            {
                requestHardware = false;
                hardwareFailure = error.Message;
                return new(asset, stream, target, rate);
            }
        }
        private VideoFrame? current;
        private MediaTimestamp? nextTimestamp;
        private bool retainedDirty;
        private bool ended;
        private SourcePosition? requested;
        public long DecodedFrames { get; private set; }
        public long ConvertedFrames { get; private set; }
        public long HistoryHits { get; private set; }
        public long Seeks { get; private set; }
        public bool UsesProxy => proxy is not null;
        public bool TryTakePixels(VideoFrame picture)
        {
            if (!ReferenceEquals(picture, current)) return false;
            // The result owns this writable array. A repeat request can materialize the retained
            // native picture again, without sharing the output or keeping another RGBA buffer.
            current = null;
            retainedDirty = true;
            return true;
        }
        public VideoFrame? At(SourcePosition target, CancellationToken cancellation)
        {
            try { return ReadAt(target, cancellation); }
            catch (HardwareVideoDecodeException error)
            {
                cancellation.ThrowIfCancellationRequested();
                requestHardware = false;
                hardwareFailure = error.Message;
                Reopen(target);
                return ReadAt(target, cancellation);
            }
            catch (Exception error) when (proxy is not null && error is IOException or InvalidOperationException or NotSupportedException)
            {
                proxy = null;
                Reopen(target);
                return ReadAt(target, cancellation);
            }
        }

        private void Reopen(SourcePosition target)
        {
            decoder.Dispose();
            decoder = Open(target);
            current = null; nextTimestamp = null; retainedDirty = false; ended = false; requested = null;
        }
        private VideoFrame? ReadAt(SourcePosition target, CancellationToken cancellation)
        {
            if (requested is { } previous && (target.CompareTo(previous) < 0 || target.Since(previous).Ticks > SequenceTime.TicksPerSecond))
            {
                cancellation.ThrowIfCancellationRequested();
                if (retainHistory && target.CompareTo(previous) < 0 && decoder.HistoryAt(target, out bool converted) is { } historic)
                {
                    HistoryHits++;
                    if (converted) ConvertedFrames++;
                    return historic.SourceTimestamp == current?.SourceTimestamp ? WithinDecodedStream(historic, target) : historic;
                }
                Seeks++;
                try { decoder.SeekVideo(target); }
                catch (InvalidDataException)
                {
                    decoder.Dispose();
                    decoder = Open(target);
                }
                current = null; nextTimestamp = null; retainedDirty = false; ended = false;
            }
            requested = target;
            while (true)
            {
                if (nextTimestamp is null && !ended)
                {
                    if (decoder.Read(cancellation)) { DecodedFrames++; nextTimestamp = decoder.Timestamp; } else ended = true;
                }
                if (nextTimestamp is null || new SourcePosition(nextTimestamp.Value).CompareTo(target) > 0)
                {
                    if (retainedDirty)
                    {
                        current = decoder.CopyRetainedVideo();
                        if (hardwareFailure is not null && current.Decode is { } evidence)
                            current = current with { Decode = evidence with { Fallback = hardwareFailure } };
                        ConvertedFrames++;
                        retainedDirty = false;
                    }
                    return WithinDecodedStream(current, target);
                }
                if (retainHistory) decoder.RememberVideo(nextTimestamp.Value);
                decoder.RetainVideo(); retainedDirty = true; nextTimestamp = null;
            }
        }
        private VideoFrame? WithinDecodedStream(VideoFrame? picture, SourcePosition target)
        {
            if (ended && picture?.Duration is { } length)
            {
                var end = new MediaTimestamp(checked(picture.SourceTimestamp.Value + length.Value), length.Numerator, length.Denominator);
                if (new SourcePosition(end).CompareTo(target) <= 0) return null;
            }
            return picture;
        }
        public void Dispose() => decoder.Dispose();
    }
    internal static VideoFrame ReadSourceFrame(MediaAsset asset, AssetStream stream, SourcePosition position, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        FfmpegRuntime.Initialize();
        using var cursor = new VideoCursor(asset, stream, position, 48000, useProxy: false, retainHistory: false);
        return cursor.At(position, cancellation) ?? throw new ArgumentException("No source picture covers the requested timestamp.");
    }
    private sealed class AudioCursor : IDisposable
    {
        public long DecodedFrames { get; private set; }
        private readonly FfmpegDecoder decoder;
        private readonly AudioSeekIndex.Anchor anchor;
        private readonly MediaAsset asset;
        private readonly int rate;
        private readonly AudioFilter? filter;
        private float[] block = [];
        private long blockStart;
        private bool initialized, ended;
        public AudioCursor(ActiveClip clip, int rate, CancellationToken cancellation)
        {
            this.rate = rate;
            asset = clip.Asset;
            anchor = AudioSeekIndex.Resolve(clip.Asset, clip.Stream, clip.SourcePosition, cancellation);
            var preroll = AudioSeekIndex.DecoderStart(clip.Asset, clip.Stream, anchor.Pts);
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
                        if (compared > 0) throw AudioSeekIndex.SkippedAnchor(asset, anchor.Pts, decoder.Timestamp);
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
