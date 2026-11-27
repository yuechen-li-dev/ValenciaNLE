using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.Engine;

public sealed record ExportFinding(string Severity, string Code, string Message, string? Suggestion = null,
    string? AtTicks = null, string? Clip = null, string? Track = null);
public sealed record ExportMeasurement(string Container, string? VideoCodec, string? AudioCodec, int? Width, int? Height,
    string? FrameRate, double? DurationSeconds, double? IntegratedLufs, double? TruePeakDb, long Bytes);
public sealed record ExportLintReport(int Version, ExportMeasurement? Measured, IReadOnlyList<ExportFinding> Findings)
{
    public bool HasErrors => Findings.Any(f => f.Severity == "error");
}

public static partial class ExportLint
{
    [GeneratedRegex(@"black_start:(?<start>[-0-9.]+) black_end:(?<end>[-0-9.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex BlackPattern();
    [GeneratedRegex(@"freeze_start: (?<start>[-0-9.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex FreezePattern();

    public static async Task<ExportLintReport> InspectAsync(string path, ProjectSnapshot snapshot, Guid sequenceId,
        OutputProfile? profile, ExportEncoder expected, AudioFinish? target, bool burnCaptions,
        CancellationToken cancellation = default)
    {
        var findings = new List<ExportFinding>();
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            return new(1, null, [new("error", "OUTPUT_MISSING", "The finished export is missing or empty.", "Retry to a new destination.")]);

        var timeline = new TimelineQueries(snapshot);
        var sequence = timeline.Sequence(sequenceId);
        int width = profile?.Width ?? sequence.Video.Width;
        int height = profile?.Height ?? sequence.Video.Height;
        var frameRate = profile?.FrameRate ?? sequence.Video.FrameRate;
        var start = profile?.Start ?? default;
        long end = Math.Min(profile?.End?.Ticks ?? sequence.Duration.Ticks, sequence.Duration.Ticks);
        long frames = TimelineTime.FrameIndex(new(end - start.Ticks), frameRate, TimeRounding.Ceiling);
        double duration = TimelineTime.FromFrames(frames, frameRate).TotalSeconds;
        JsonDocument probe;
        try
        {
            var (output, _) = await MediaProcess.CaptureAsync(FfmpegRuntime.ProbeExecutable,
                ["-v", "error", "-show_entries", "format=format_name,duration:stream=codec_type,codec_name,width,height,avg_frame_rate",
                 "-of", "json", path], cancellation).ConfigureAwait(false);
            probe = JsonDocument.Parse(output);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
        {
            return new(1, null, [new("error", "OUTPUT_UNREADABLE", error.Message, "Check the FFprobe installation and retry the export.")]);
        }

        using (probe)
        {
            var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var video = streams.Where(s => Read(s, "codec_type") == "video").ToArray();
            var audio = streams.Where(s => Read(s, "codec_type") == "audio").ToArray();
            var format = probe.RootElement.GetProperty("format");
            var container = Read(format, "format_name") ?? "";
            double? actualDuration = Number(Read(format, "duration"));
            var measured = new ExportMeasurement(container, Read(video.FirstOrDefault(), "codec_name"), Read(audio.FirstOrDefault(), "codec_name"),
                Integer(video.FirstOrDefault(), "width"), Integer(video.FirstOrDefault(), "height"), Read(video.FirstOrDefault(), "avg_frame_rate"),
                actualDuration, null, null, new FileInfo(path).Length);

            string expectedContainer = expected.Container == "mkv" ? "matroska" : expected.Container;
            if (!container.Split(',').Contains(expectedContainer, StringComparer.Ordinal))
                findings.Add(new("error", "OUTPUT_CONTAINER", $"Expected {expectedContainer}, found {container}."));
            if (video.Length != 1 || audio.Length != 1 || streams.Length != 2)
                findings.Add(new("error", "OUTPUT_STREAMS", $"Expected one video and one audio stream, found {video.Length} video and {audio.Length} audio of {streams.Length} streams."));
            if (measured.VideoCodec != expected.Codec || measured.AudioCodec != expected.AudioCodec)
                findings.Add(new("error", "OUTPUT_CODEC", $"Expected {expected.Codec}/{expected.AudioCodec}, found {measured.VideoCodec}/{measured.AudioCodec}."));
            if (measured.Width != width || measured.Height != height)
                findings.Add(new("error", "OUTPUT_DIMENSIONS", $"Expected {width}×{height}, found {measured.Width}×{measured.Height}."));
            if (!EquivalentRate(measured.FrameRate, frameRate))
                findings.Add(new("deviation", "OUTPUT_RATE", $"Expected {frameRate.Numerator}/{frameRate.Denominator}, found {measured.FrameRate}."));
            double tolerance = Math.Max(.05, 1.5 * frameRate.Denominator / frameRate.Numerator);
            if (actualDuration is null || Math.Abs(actualDuration.Value - duration) > tolerance)
                findings.Add(new("error", "OUTPUT_DURATION", FormattableString.Invariant($"Expected {duration:F3}s, found {actualDuration:F3}s (tolerance {tolerance:F3}s).")));

            if (video.Length == 1 && audio.Length == 1)
            {
                try
                {
                    // Decode the finished encoded output. This is separate from any encoder filter output.
                    var (_, log) = await MediaProcess.CaptureAsync(
                        ["-hide_banner", "-nostats", "-i", path, "-map", "0:v:0", "-map", "0:a:0",
                         "-vf", "blackdetect=d=0.4:pix_th=0.1,freezedetect=n=-60dB:d=1.5",
                         "-af", "loudnorm=print_format=json", "-f", "null", "NUL"], cancellation).ConfigureAwait(false);
                    var (integrated, peak) = Measure(log);
                    measured = measured with { IntegratedLufs = integrated, TruePeakDb = peak };
                    if (peak >= 0)
                        findings.Add(new("warning", "CLIPPING_RISK", $"Encoded output reached {peak:F2} dBTP.", "Lower the true-peak target and re-encode."));
                    if (target is not null)
                    {
                        if (integrated is null || Math.Abs(integrated.Value - target.TargetLufs) > 1)
                            findings.Add(new("deviation", "LOUDNESS_TARGET", $"Measured {integrated:F2} LUFS, target {target.TargetLufs:F2} LUFS ±1.0.",
                                "Review the mix and dynamics or explicitly select another loudness target."));
                        if (peak is not null && peak.Value > target.TruePeakDb + .1)
                            findings.Add(new("deviation", "TRUE_PEAK_TARGET", $"Measured {peak:F2} dBTP, ceiling {target.TruePeakDb:F2} dBTP (+0.1 dB tolerance).",
                                "Lower the selected ceiling to allow more codec headroom."));
                    }

                    foreach (Match match in BlackPattern().Matches(log).Take(12))
                    {
                        if (Number(match.Groups["start"].Value) is not { } from || Number(match.Groups["end"].Value) is not { } to) continue;
                        var midpoint = At((from + to) / 2);
                        var active = timeline.Evaluate(sequenceId, midpoint).LastOrDefault(c => c.Kind == TrackKind.Video);
                        findings.Add(new(active is null ? "advisory" : "warning", "BLACK_INTERVAL",
                            FormattableString.Invariant($"Black output for {to - from:F2}s") +
                            (active is null ? " across a picture gap." : " with a visible timeline clip."),
                            active is null ? null : "Check the clip and confirm that the black interval is intentional.", EngineJson.Exact(At(from).Ticks),
                            active is null ? null : AgentHandles.For("clip", active.Clip.Id), active is null ? null : AgentHandles.For("track", active.TrackId)));
                    }
                    foreach (Match match in FreezePattern().Matches(log).Take(12))
                    {
                        if (Number(match.Groups["start"].Value) is not { } from) continue;
                        var at = At(from);
                        var active = timeline.Evaluate(sequenceId, at).LastOrDefault(c => c.Kind == TrackKind.Video);
                        bool expectedHold = active is null || active.Clip.Retime?.Hold is not null || active.Clip.Title is not null;
                        findings.Add(new(expectedHold ? "advisory" : "warning", "FREEZE_INTERVAL", "Decoded output appears static for at least 1.5 seconds.",
                            expectedHold ? null : "Review whether this shot should be static.", EngineJson.Exact(at.Ticks),
                            active is null ? null : AgentHandles.For("clip", active.Clip.Id), active is null ? null : AgentHandles.For("track", active.TrackId)));
                    }
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
                {
                    findings.Add(new("warning", "OUTPUT_ANALYSIS_FAILED", error.Message, "Open the video independently and inspect the encode."));
                }
            }

            if (burnCaptions && sequence.Creator is { Words.Length: > 0 } creator)
                InspectCaptionTiming(sequence, profile, width, height, start.Ticks, end, creator, findings);
            return new(1, measured, findings);
        }

        SequenceTime At(double seconds) => new(checked(start.Ticks + (long)Math.Round(Math.Max(0, seconds) * SequenceTime.TicksPerSecond)));
    }

    private static void InspectCaptionTiming(SequenceDocument sequence, OutputProfile? profile, int width, int height, long start, long end,
        CreatorSettings creator, List<ExportFinding> findings)
    {
        double baseline = height * (profile?.CaptionY ?? creator.CaptionStyle.Y);
        double size = Math.Min((double)creator.CaptionStyle.Size * height / 720, width * .075);
        if (baseline - size - 4 < 0 || baseline + size * .3 > height)
            findings.Add(new("warning", "CAPTION_BOUNDS", "The caption backdrop can extend outside the output frame at the selected baseline.",
                "Move the caption baseline inward and inspect an exported frame."));
        var words = creator.Words.ToLookup(w => (w.AssetId, w.StreamIndex));
        foreach (var (track, clip) in sequence.Tracks.Where(t => t.Kind == TrackKind.Audio && !t.State.Muted)
            .SelectMany(t => t.Clips.Where(c => c.Properties.Enabled).Select(c => (t, c))).Take(4096))
        {
            Check(clip.Start.Ticks, clip.SourceIn);
            Check(clip.End.Ticks, clip.SourceOut);

            void Check(long boundary, SourcePosition source)
            {
                if (boundary < start || boundary >= end || findings.Count(f => f.Code == "CUT_INSIDE_WORD") >= 12) return;
                var word = words[(clip.AssetId, clip.StreamIndex)].FirstOrDefault(w => w.Start.CompareTo(source) < 0 && w.End.CompareTo(source) > 0);
                if (word is null) return;
                findings.Add(new("advisory", "CUT_INSIDE_WORD", $"An audio edit boundary intersects the aligned word '{word.Text}'.",
                    "Listen around the cut and adjust it if the word is unintentionally truncated.", EngineJson.Exact(boundary),
                    AgentHandles.For("clip", clip.Id), AgentHandles.For("track", track.Id)));
            }
        }
    }

    private static (double? Integrated, double? Peak) Measure(string log)
    {
        int begin = log.LastIndexOf('{'), end = log.LastIndexOf('}');
        if (begin < 0 || end < begin) throw new InvalidDataException("FFmpeg did not report encoded loudness.");
        using var json = JsonDocument.Parse(log[begin..(end + 1)]);
        return (Number(Read(json.RootElement, "input_i")), Number(Read(json.RootElement, "input_tp")));
    }

    private static string? Read(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? Integer(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out int number) ? number : null;
    private static double? Number(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        && double.IsFinite(number) ? number : null;
    private static bool EquivalentRate(string? actual, FrameRate expected)
    {
        var values = actual?.Split('/');
        return values is { Length: 2 } && int.TryParse(values[0], out int numerator) && int.TryParse(values[1], out int denominator) &&
            denominator > 0 && (long)numerator * expected.Denominator == (long)denominator * expected.Numerator;
    }
}
