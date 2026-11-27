using Valencia.Core.Timeline;

namespace Valencia.Core.Editing;

/// <summary>
/// Sequence-level annotations (titles and markers) follow the picture through ripple edits, as Premiere's graphics and
/// sequence markers do. Times map through the edit; nothing is re-timed relative to the clock it was authored on.
/// </summary>
internal static class SequenceAnnotations
{
    /// <summary>
    /// Closes [<paramref name="start"/>, <paramref name="end"/>). Later annotations move left by the removed length.
    /// A marker inside the range moves to the cut (kept, never silently deleted). A title loses only its removed part,
    /// with keyframes mapped through the cut and entrance/exit clamped; a title entirely inside the range is removed.
    /// </summary>
    public static SequenceDocument Remove(SequenceDocument sequence, SequenceTime start, SequenceTime end)
    {
        long a = start.Ticks, b = end.Ticks, removed = b - a;
        long Map(long t) => t < a ? t : t < b ? a : t - removed;
        return Retime(sequence, Map, Map);
    }

    /// <summary>
    /// Opens a gap of <paramref name="shift"/> at <paramref name="at"/>. Annotations at or after it move right; a title
    /// spanning it stretches over the inserted material (titles are not split, so keyframes keep one curve).
    /// </summary>
    public static SequenceDocument Insert(SequenceDocument sequence, SequenceTime at, SequenceTime shift)
    {
        long point = at.Ticks, length = shift.Ticks;
        // Ends are exclusive: a title ending exactly at the insert point stays where it is.
        return Retime(sequence, t => t >= point ? checked(t + length) : t, t => t > point ? checked(t + length) : t);
    }

    private static SequenceDocument Retime(SequenceDocument sequence, Func<long, long> map, Func<long, long> mapEnd)
    {
        var markers = sequence.Markers.Select(marker => marker with { At = new(map(marker.At.Ticks)) })
            .OrderBy(marker => marker.At.Ticks).ThenBy(marker => marker.Id);
        var titles = new List<TitleCue>(sequence.Titles.Length);
        foreach (var title in sequence.Titles)
        {
            long start = map(title.Start.Ticks), end = mapEnd(title.End.Ticks);
            if (end <= start) continue;
            long duration = end - start, entrance = Math.Min(title.EntranceTicks, duration);
            titles.Add(title with
            {
                Start = new(start), End = new(end), EntranceTicks = entrance,
                ExitTicks = Math.Min(title.ExitTicks, duration - entrance),
                Transform = Curve(title.Transform, title.Start.Ticks, start, map),
            });
        }
        return sequence with { Markers = [.. markers], Titles = [.. titles.OrderBy(cue => cue.Start.Ticks).ThenBy(cue => cue.Id)] };
    }

    /// <summary>Keys are relative to the title start. Keys collapsed onto one instant by a cut keep the latest pose.</summary>
    private static TransformCurve? Curve(TransformCurve? curve, long oldStart, long newStart, Func<long, long> map)
    {
        if (curve is null) return null;
        var keys = curve.Keys.Select(key => key with { At = map(checked(oldStart + key.At)) - newStart })
            .GroupBy(key => key.At).Select(group => group.Last()).OrderBy(key => key.At).ToArray();
        return new TransformCurve([.. keys]);
    }
}
