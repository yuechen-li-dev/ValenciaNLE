using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Analysis;

/// <summary>Prepared once on a worker. Viewport changes query existing pyramid levels; no PCM or waveform rebuild.</summary>
public sealed class WaveformIndex
{
    private readonly (WaveBin[] Bins, long MaximumDuration)[] levels;
    public AnalysisArtifact Artifact { get; }
    public WaveformIndex(AnalysisArtifact artifact)
    {
        Artifact = artifact;
        levels = artifact.Waveform.Select(level =>
        {
            var bins = level.Bins.OrderBy(b => b.Start, Comparer<SourcePosition>.Create((a, b) => a.CompareTo(b))).ToArray();
            return (bins, bins.Select(b => b.End.Since(b.Start).Ticks).DefaultIfEmpty(0).Max());
        }).ToArray();
    }
    public IReadOnlyList<WaveBin> Visible(SourcePosition start, SourcePosition end, int pixels)
    {
        pixels = Math.Clamp(pixels, 1, 2048);
        foreach (var level in levels)
        {
            var bins = level.Bins;
            var earliest = start.Shift(new(-level.MaximumDuration));
            int low = 0, high = bins.Length;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (bins[middle].Start.CompareTo(earliest) < 0) low = middle + 1;
                else high = middle;
            }
            var found = new List<WaveBin>();
            for (int i = low; i < bins.Length && bins[i].Start.CompareTo(end) < 0; i++)
            {
                if (bins[i].End.CompareTo(start) <= 0) continue;
                found.Add(bins[i]);
                if (found.Count > pixels) break;
            }
            if (found.Count <= pixels || ReferenceEquals(bins, levels[^1].Bins)) return found;
        }
        return [];
    }
}
