using System.Globalization;
using System.Text.Json;
using Valencia.Core.Timeline;
using Valencia.Media;
using Valencia.Core;
using System.Runtime.InteropServices;

namespace Valencia.Analysis;

public sealed record LoudnessData(double? IntegratedLufs, double? TruePeakDb, double? LoudnessRange, double? ThresholdLufs,
    string Algorithm, string Measurement, double TargetLufs = -16, double PeakCeilingDb = -1)
{
    public double? SafeGainDb => IntegratedLufs is { } loudness && TruePeakDb is { } peak
        ? Math.Clamp(Math.Min(TargetLufs - loudness, PeakCeilingDb - peak), -60, 30) : null;
}

public static class LoudnessAnalyzer
{
    public static AnalysisArtifact Source(MediaAsset asset, int stream, string key, string fingerprint, CancellationToken cancellation)
    {
        if (!asset.Streams.Any(s => s.Index == stream && s.Kind == TrackKind.Audio)) throw new ArgumentException("Select a source audio stream.");
        string log = MediaProcess.RunAsync(["-hide_banner", "-nostats", "-i", asset.SourcePath, "-map", $"0:{stream}", "-vn", "-sn",
            "-af", "loudnorm=I=-16:TP=-1:LRA=11:print_format=json", "-f", "null", "NUL"], cancellation).GetAwaiter().GetResult();
        var loudness = Parse(log);
        var imported = MediaAssetProbe.Import(asset.Id, asset.SourcePath);
        return new(1, key, asset.Id, fingerprint, stream, "loudness", loudness.Algorithm, "target=-16;peak=-1;LRA=11", MediaAnalyzer.DecoderPolicy,
            imported.ContainerStart, "complete", 0, 0, 0, [], [], [], [], [], Loudness: loudness);
    }
    public static async Task<LoudnessData> SequenceAsync(ProjectSnapshot snapshot, Guid sequenceId, CancellationToken cancellation)
    {
        var sequence = new TimelineQueries(snapshot).Sequence(sequenceId);
        long samples = TimelineTime.SampleIndex(sequence.Duration, sequence.AudioSampleRate, TimeRounding.Ceiling);
        string log = await MediaProcess.WithInputAsync(["-v", "info", "-nostats", "-f", "f32le", "-ar", sequence.AudioSampleRate.ToString(CultureInfo.InvariantCulture), "-ac", "2", "-i", "pipe:0",
            "-af", "loudnorm=I=-16:TP=-1:LRA=11:print_format=json", "-f", "null", "NUL"], async output =>
        {
            using var evaluator = new SequenceEvaluator(snapshot, sequenceId);
            for (long first = 0; first < samples; first += 4096)
            {
                cancellation.ThrowIfCancellationRequested();
                float[] pcm = evaluator.Audio(default, first, (int)Math.Min(4096, samples - first), cancellation);
                await output.WriteAsync(MemoryMarshal.AsBytes(pcm.AsSpan()).ToArray(), cancellation).ConfigureAwait(false);
            }
        }, cancellation).ConfigureAwait(false);
        return Parse(log) with { Measurement = "Edited sequence through the shared audio evaluator; integrated gating and oversampled true-peak estimation" };
    }
    private static LoudnessData Parse(string log)
    {
        int begin = log.LastIndexOf('{'), end = log.LastIndexOf('}');
        if (begin < 0 || end < begin) throw new InvalidDataException("FFmpeg did not report loudness measurements.");
        using var json = JsonDocument.Parse(log[begin..(end + 1)]);
        double? Number(string name) => double.TryParse(json.RootElement.GetProperty(name).GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
        return new LoudnessData(Number("input_i"), Number("input_tp"), Number("input_lra"), Number("input_thresh"),
            "FFmpeg7.1/loudnorm/BS.1770-4/EBU-R128", "Source-stream integrated gating and oversampled true-peak estimation; not peak normalization");
    }
}
