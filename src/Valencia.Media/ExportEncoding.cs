using Valencia.Core.Timeline;

namespace Valencia.Media;

/// <summary>
/// Where a file is going decides how loud it should be. YouTube, Shorts, TikTok, Reels and Spotify play back
/// around -14 LUFS integrated; Apple Podcasts and spoken-word delivery use -16. Turning a quiet upload up is
/// the platform's job only in theory (YouTube turns loud uploads down but never quiet ones up), so a video that
/// ships at -16 plays 2 dB quieter than its neighbours.
/// </summary>
public static class LoudnessTargets
{
    public const double Video = -14, Podcast = -16;

    public static (double Lufs, string Destination) For(string? name, int width, int height)
    {
        string n = (name ?? "").ToLowerInvariant();
        if (n.Contains("podcast") || n.Contains("spoken") || n.Contains("audio only") || n.Contains("audio-only")) return (Podcast, "podcast");
        return (Video, height > width ? "vertical short" : "video platform");
    }

    public static AudioFinish Finish(string? name, int width, int height) => new(For(name, width, height).Lufs);
}

public sealed record AudioFinish(double TargetLufs = LoudnessTargets.Video, double TruePeakDb = -1, double LoudnessRange = 11)
{
    public void Validate()
    {
        if (!double.IsFinite(TargetLufs) || TargetLufs is < -30 or > -12 ||
            !double.IsFinite(TruePeakDb) || TruePeakDb is < -9 or > -.5 ||
            !double.IsFinite(LoudnessRange) || LoudnessRange is < 7 or > 20)
            throw new ArgumentException("Audio finishing supports targets from -30 to -12 LUFS, ceilings from -9 to -0.5 dBTP, and loudness range 7 to 20 LU.");
    }

    public string Filter => FilterFor("opus");

    internal string FilterFor(string audioCodec)
    {
        // AAC created a larger decoded peak overshoot than Opus on the controlled long-form mix.
        double headroom = audioCodec == "aac" ? 2 : .5;
        double desiredPeak = TruePeakDb - headroom;
        double attenuation = Math.Max(0, -9 - desiredPeak);
        double filterPeak = Math.Max(-9, desiredPeak);
        string normalize = FormattableString.Invariant($"loudnorm=I={TargetLufs + attenuation}:TP={filterPeak}:LRA={LoudnessRange}:linear=false");
        // Small post-filter makeup keeps the guarded AAC mix close to the integrated target.
        double gain = (audioCodec == "aac" ? .65 : 0) - attenuation;
        return gain == 0 ? normalize : normalize + FormattableString.Invariant($",volume={gain}dB");
    }
}

public sealed record ExportEncoder(string Id, string Codec, string Implementation, string Execution, string Container,
    string AudioCodec, string AudioImplementation, IReadOnlyList<string> Options, bool Compatibility,
    int MaximumWidth = 3840, int MaximumHeight = 3840, int MaximumFps = 60, string PixelFormat = "yuv420p")
{
    public string Executable => Compatibility ? FfmpegRuntime.CompatibilityExecutable : FfmpegRuntime.Executable;
    public string Extension => "." + Container;
}

public sealed record EncoderAvailability(string Id, string Codec, string Implementation, string Execution, string Container,
    string AudioCodec, int MaximumWidth, int MaximumHeight, int MaximumFps, string PixelFormat, string[] QualityOptions,
    bool Available, string? Reason);

public sealed record ExportPreset(string Id, int Version, string Name, int Width, int Height, FrameRate FrameRate, string Fit,
    double FocalX, double FocalY, double CaptionY, bool BurnCaptions, string Encoder, AudioFinish? Audio, int? Quality = null)
{
    public OutputProfile Apply(SequenceDocument sequence, OutputProfile? selected)
    {
        if (Version != 1 || Width is < 64 or > 3840 || Height is < 64 or > 3840 || (long)Width * Height > 8_294_400
            || Width % 2 != 0 || Height % 2 != 0 || FrameRate.Numerator <= 0 || FrameRate.Denominator <= 0
            || (long)FrameRate.Numerator > 60L * FrameRate.Denominator
            || Fit is not ("fit" or "fill" or "center") || !InUnit(FocalX) || !InUnit(FocalY) || !InUnit(CaptionY))
            throw new ArgumentException("Preset requires version 1, even SDR dimensions within 8.29 MP, an exact rate up to 60 fps, and valid framing.");
        _ = TimelineTime.FromFrames(1, FrameRate);
        Audio?.Validate();
        var basis = selected ?? new OutputProfile(sequence.Id, Name, Width, Height, FrameRate);
        // An explicit project's profile retains its designed framing and caption placement.
        return basis with { Width = Width, Height = Height, FrameRate = FrameRate,
            Fit = selected?.Fit ?? Fit, FocalX = selected?.FocalX ?? FocalX, FocalY = selected?.FocalY ?? FocalY,
            CaptionY = selected?.CaptionY ?? CaptionY };
        static bool InUnit(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
    }
}

/// <summary>Closed presets and encoder mappings. User presets are versioned request data using the same validation.</summary>
public static class ExportCatalog
{
    public static IReadOnlyList<ExportEncoder> Encoders { get; } =
    [
        new("av1", "av1", "libaom-av1", "software", "webm", "opus", "libopus",
            ["-cpu-used", "6", "-row-mt", "1", "-crf", "30", "-b:v", "0"], false),
        new("vp9", "vp9", "libvpx-vp9", "software", "webm", "opus", "libopus",
            ["-deadline", "good", "-cpu-used", "4", "-row-mt", "1", "-crf", "27", "-b:v", "0"], false),
        new("svt-av1", "av1", "libsvtav1", "software", "webm", "opus", "libopus",
            ["-preset", "8", "-crf", "30"], true),
        new("h264", "h264", "libx264", "software", "mp4", "aac", "aac", ["-preset", "veryfast", "-crf", "18"], true),
        new("hevc", "hevc", "libx265", "software", "mp4", "aac", "aac", ["-preset", "fast", "-crf", "23"], true),
        new("h264-mf", "h264", "h264_mf", "platform", "mp4", "aac", "aac", ["-b:v", "12M"], true),
        new("hevc-mf", "hevc", "hevc_mf", "platform", "mp4", "aac", "aac", ["-b:v", "12M"], true),
        new("h264-nvenc", "h264", "h264_nvenc", "hardware", "mp4", "aac", "aac", ["-preset", "p4", "-b:v", "12M"], true),
        new("hevc-nvenc", "hevc", "hevc_nvenc", "hardware", "mp4", "aac", "aac", ["-preset", "p4", "-b:v", "10M"], true),
        new("av1-nvenc", "av1", "av1_nvenc", "hardware", "webm", "opus", "libopus", ["-preset", "p4", "-b:v", "10M"], true),
        new("av1-amf", "av1", "av1_amf", "hardware", "webm", "opus", "libopus", ["-b:v", "10M"], true),
        new("av1-qsv", "av1", "av1_qsv", "hardware", "webm", "opus", "libopus", ["-b:v", "10M"], true),
        new("archive", "ffv1", "ffv1", "software", "mkv", "flac", "flac", ["-level", "3"], false, PixelFormat: "yuv444p")
    ];

    public static IReadOnlyList<ExportPreset> Presets { get; } =
    [
        new("landscape-1080p60", 1, "Landscape 1080p60", 1920, 1080, new(60, 1), "fit", .5, .5, .84, true, "vp9", new()),
        new("landscape-1440p60", 1, "Landscape 1440p60", 2560, 1440, new(60, 1), "fit", .5, .5, .84, true, "av1", new()),
        new("vertical-1080x1920", 1, "Vertical 1080×1920", 1080, 1920, new(60, 1), "fill", .5, .5, .79, true, "vp9", new()),
        new("review-360p", 1, "Review 360p", 640, 360, new(30, 1), "fit", .5, .5, .84, true, "vp9", new(), 34),
        new("intermediate-1080p", 1, "FFV1 intermediate", 1920, 1080, new(30, 1), "fit", .5, .5, .84, true, "archive", null)
    ];

    public static ExportEncoder Encoder(string id) => Encoders.FirstOrDefault(encoder => encoder.Id == id)
        ?? throw new ArgumentException($"Unknown encoder provider '{id}'.");

    public static ExportPreset Preset(string id) => Presets.FirstOrDefault(preset => preset.Id == id)
        ?? throw new ArgumentException($"Unknown output preset '{id}'.");

    public static ExportEncoder ForDestination(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".webm" => Encoder("vp9"),
        ".mp4" => Encoder("h264"),
        ".mkv" => Encoder("archive"),
        _ => throw new ArgumentException("Use .webm for AV1 or VP9, .mp4 for the configured compatibility provider, or .mkv for FFV1.")
    };

    public static string[] EncoderArguments(ExportEncoder encoder, int? quality)
    {
        var options = encoder.Options.ToArray();
        if (quality is { } crf)
        {
            int position = Array.IndexOf(options, "-crf");
            int max = encoder.Codec is "h264" or "hevc" ? 51 : 63;
            if (position < 0 || crf < 0 || crf > max)
                throw new ArgumentException($"CRF is unavailable or outside the validity range for {encoder.Id}.");
            options[position + 1] = crf.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return ["-c:v", encoder.Implementation, .. options, "-pix_fmt", encoder.PixelFormat,
            "-c:a", encoder.AudioImplementation, .. (encoder.AudioCodec == "flac" ? [] : new[] { "-b:a", encoder.AudioCodec == "opus" ? "128k" : "192k" })];
    }

    public static async Task<EncoderAvailability> ProbeAsync(ExportEncoder encoder, CancellationToken cancellation = default)
    {
        string? reason = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await MediaProcess.CaptureAsync(encoder.Executable,
                ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=size=640x360:rate=30:duration=0.1",
                 "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-frames:v", "2", "-t", "0.1",
                 .. EncoderArguments(encoder, null), "-f", "null", "NUL"], timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when ((error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            || (error is OperationCanceledException && !cancellation.IsCancellationRequested))
        {
            reason = error.Message.Length <= 500 ? error.Message : error.Message[..500];
        }
        return new(encoder.Id, encoder.Codec, encoder.Implementation, encoder.Execution, encoder.Container, encoder.AudioCodec,
            encoder.MaximumWidth, encoder.MaximumHeight, encoder.MaximumFps, encoder.PixelFormat, [.. encoder.Options], reason is null, reason);
    }
}
