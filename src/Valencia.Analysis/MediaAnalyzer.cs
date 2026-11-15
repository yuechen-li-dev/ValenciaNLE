using System.Collections.Immutable;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.Analysis;

public static class MediaAnalyzer
{
    public const string AudioAlgorithm = "rms-peak-v1";
    public const string SceneAlgorithm = "luma64-hist16-v1";
    public static string DecoderPolicy => FfmpegRuntime.DecoderIdentity + "/same-rate-float/native-pts/luma64x36-area/v1";

    public static AnalysisArtifact Audio(MediaAsset asset, int stream, AudioSettings settings, string key, string fingerprint,
        IProgress<AnalysisProgress>? progress, CancellationToken cancellation)
    {
        settings.Validate();
        var imported = MediaAssetProbe.Import(asset.Id, asset.SourcePath);
        var analyzer = new AudioAnalyzer(settings);
        int rate = 0, channels = 0;
        AnalysisStream.Audio(asset, stream, block =>
        {
            cancellation.ThrowIfCancellationRequested();
            analyzer.Push(block);
            rate = block.SampleRate;
            channels = block.Channels;
            double fraction = Math.Clamp(analyzer.ProcessedSamples / (double)rate / imported.Duration.TotalSeconds, 0, 1);
            progress?.Report(new(analyzer.ProcessedSamples, fraction));
        }, cancellation);
        if (rate == 0) throw new InvalidDataException("No decoded audio samples.");
        var result = analyzer.Finish();
        return new(1, key, asset.Id, fingerprint, stream, "audio", AudioAlgorithm, AnalysisJson.Write(settings), DecoderPolicy,
            imported.ContainerStart, "complete", rate, channels, analyzer.ProcessedSamples, result.Waveform, result.Silence, result.Activity, [],
            result.Activity.Any(a => a.Range.Epoch != 0) ? ["Source timestamp discontinuities retained as separate epochs."] : []);
    }

    public static AnalysisArtifact Scenes(MediaAsset asset, int stream, SceneSettings settings, string key, string fingerprint,
        IProgress<AnalysisProgress>? progress, CancellationToken cancellation)
    {
        settings.Validate();
        var imported = MediaAssetProbe.Import(asset.Id, asset.SourcePath);
        byte[]? previous = null;
        MediaTimestamp? last = null;
        long processed = 0;
        int epoch = 0;
        var changes = ImmutableArray.CreateBuilder<SceneChange>();
        AnalysisStream.Video(asset, stream, settings.EveryNthFrame, frame =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (last is { } old && new SourcePosition(frame.Timestamp).CompareTo(new(old)) <= 0) { epoch++; previous = null; }
            if (previous is not null)
            {
                double difference = 0;
                int[] a = new int[16], b = new int[16];
                for (int i = 0; i < previous.Length; i++)
                {
                    difference += Math.Abs(previous[i] - frame.Pixels[i]) / 255.0;
                    a[previous[i] / 16]++;
                    b[frame.Pixels[i] / 16]++;
                }
                double histogram = a.Zip(b, (x, y) => Math.Abs(x - y)).Sum() / (2.0 * previous.Length);
                double score = .3 * histogram + .7 * difference / previous.Length;
                if (score >= settings.Threshold)
                {
                    if (changes.Count >= AudioAnalyzer.MaximumBins) throw new NotSupportedException("Scene candidate budget exceeded.");
                    changes.Add(new(frame.Timestamp, score, epoch));
                }
            }
            previous = frame.Pixels;
            last = frame.Timestamp;
            progress?.Report(new(++processed, Math.Clamp(AnalysisTime.Normalize(new(frame.Timestamp), imported.ContainerStart).TotalSeconds / imported.Duration.TotalSeconds, 0, 1)));
        }, cancellation);
        return new(1, key, asset.Id, fingerprint, stream, "scenes", SceneAlgorithm, AnalysisJson.Write(settings), DecoderPolicy,
            imported.ContainerStart, "complete", 0, 0, processed, [], [], [], changes.ToImmutable(), []);
    }
}
