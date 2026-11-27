using System.Collections.Immutable;

namespace Valencia.Core.Timeline;

public sealed record CaptionLine(ImmutableArray<CaptionWord> Words, int ActiveWord, double Opacity = 1);

/// <summary>Source-aligned caption evaluation. Edits change placement, never transcription clocks.</summary>
public static class CaptionEvaluation
{
    public static CaptionLine? At(SequenceDocument sequence, IEnumerable<ActiveClip> active)
    {
        return new CaptionIndex(sequence).At(active);
    }

    public static double GainAt(ImmutableArray<GainPoint> points, SequenceTime time)
    {
        if (points.IsDefaultOrEmpty) return 0;
        if (time.Ticks <= points[0].Time.Ticks) return points[0].GainDb;
        int left = 1, right = points.Length - 1, found = -1;
        while (left <= right)
        {
            int middle = left + (right - left) / 2;
            if (points[middle].Time.Ticks >= time.Ticks) { found = middle; right = middle - 1; }
            else left = middle + 1;
        }
        if (found >= 0)
        {
            var a = points[found - 1]; var b = points[found];
            double fraction = (double)(time.Ticks - a.Time.Ticks) / (b.Time.Ticks - a.Time.Ticks);
            return a.GainDb + (b.GainDb - a.GainDb) * fraction;
        }
        return points[^1].GainDb;
    }

    public static double Fade(long local, long duration, long fadeIn, long fadeOut)
    {
        double gain = 1;
        if (fadeIn > 0) gain = Math.Min(gain, Math.Clamp(local / (double)fadeIn, 0, 1));
        if (fadeOut > 0) gain = Math.Min(gain, Math.Clamp((duration - local) / (double)fadeOut, 0, 1));
        return gain;
    }
}
