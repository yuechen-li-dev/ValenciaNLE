using System.Collections.Immutable;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.Analysis;

public sealed record AudioSettings(double ThresholdDb = -35, double ActiveDb = -20, int WindowMs = 20,
    int MinimumSilenceMs = 800, int MinimumActivityMs = 0, int MergeSilenceMs = 0, int LeadingPaddingMs = 0, int TrailingPaddingMs = 0)
{
    public void Validate()
    {
        if (!double.IsFinite(ThresholdDb) || ThresholdDb is < -120 or > 0 || !double.IsFinite(ActiveDb) || ActiveDb < ThresholdDb || ActiveDb > 0
            || WindowMs is < 1 or > 1000 || MinimumSilenceMs is < 0 or > 3_600_000
            || MinimumActivityMs is < 0 or > 60_000 || MergeSilenceMs is < 0 or > 60_000
            || LeadingPaddingMs is < 0 or > 60_000 || TrailingPaddingMs is < 0 or > 60_000)
            throw new ArgumentException("Invalid audio analysis parameters.");
    }
}
public sealed record SceneSettings(double Threshold = .35, int EveryNthFrame = 1)
{
    public void Validate()
    {
        if (!double.IsFinite(Threshold) || Threshold is <= 0 or > 1 || EveryNthFrame is < 1 or > 10000)
            throw new ArgumentException("Invalid scene analysis parameters.");
    }
}
public sealed record SourceRange(SourcePosition Start, SourcePosition End, int Epoch);
public sealed record EnergyInterval(SourceRange Range, string Category, double Rms);
public sealed record ChannelPeak(float Minimum, float Maximum, double Rms);
public sealed record WaveBin(SourcePosition Start, SourcePosition End, long Samples, int Epoch, ImmutableArray<ChannelPeak> Channels);
public sealed record WaveLevel(ImmutableArray<WaveBin> Bins);
public sealed record SceneChange(MediaTimestamp Timestamp, double Score, int Epoch);
public sealed record AnalysisArtifact(int SchemaVersion, string Key, Guid AssetId, string Fingerprint, int StreamIndex,
    string Kind, string Algorithm, string Parameters, string DecoderPolicy, MediaTimestamp ContainerStart,
    string Status, int SampleRate, int Channels, long ProcessedUnits, ImmutableArray<WaveLevel> Waveform,
    ImmutableArray<SourceRange> Silence, ImmutableArray<EnergyInterval> Activity, ImmutableArray<SceneChange> Scenes,
    ImmutableArray<string> Diagnostics, TranscriptData? Transcript = null, LoudnessData? Loudness = null, ProxyData? Proxy = null, SourceIndexData? Index = null);
public sealed record AnalysisProgress(long ProcessedUnits, double Fraction);

/// <summary>Coordinates remain source-absolute until explicitly normalized or mapped to a clip.</summary>
public static class AnalysisTime
{
    public static SourcePosition SampleOffset(MediaTimestamp anchor, long sample, int rate) =>
        new SourcePosition(anchor).Shift(TimelineTime.FromSamples(sample, rate));
    public static SequenceTime Normalize(SourcePosition position, MediaTimestamp containerStart) => position.Since(new(containerStart));
    /// <summary>Boundary conversion includes SourceOut for half-open range endpoints.</summary>
    public static SequenceTime ToTimeline(TimelineClip clip, SourcePosition source) => clip.MapSourceBoundary(source);
    public static SourcePosition ToSource(TimelineClip clip, SequenceTime timeline) => clip.MapToSource(timeline);
    public static SequenceTime Milliseconds(int ms) => new(checked(ms * (SequenceTime.TicksPerSecond / 1000)));
    public static long MinimumSamples(int ms, int rate) => checked(((long)ms * rate + 999) / 1000);
}
