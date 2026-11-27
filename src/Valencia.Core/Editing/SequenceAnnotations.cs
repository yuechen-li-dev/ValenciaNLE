using Valencia.Core.Timeline;

namespace Valencia.Core.Editing;

/// <summary>
/// Sequence markers follow the picture through ripple edits, as Premiere's do. Titles are clips, so they ripple as clips.
/// </summary>
internal static class SequenceAnnotations
{
    /// <summary>Closes [start, end): later markers move left; a marker inside the range moves to the cut (never deleted).</summary>
    public static SequenceDocument Remove(SequenceDocument sequence, SequenceTime start, SequenceTime end)
    {
        long a = start.Ticks, b = end.Ticks, removed = b - a;
        return Retime(sequence, t => t < a ? t : t < b ? a : t - removed);
    }

    /// <summary>Opens a gap of <paramref name="shift"/> at <paramref name="at"/>: markers at or after it move right.</summary>
    public static SequenceDocument Insert(SequenceDocument sequence, SequenceTime at, SequenceTime shift)
    {
        long point = at.Ticks, length = shift.Ticks;
        return Retime(sequence, t => t >= point ? checked(t + length) : t);
    }

    private static SequenceDocument Retime(SequenceDocument sequence, Func<long, long> map) =>
        sequence with { Markers = [.. sequence.Markers.Select(marker => marker with { At = new(map(marker.At.Ticks)) })
            .OrderBy(marker => marker.At.Ticks).ThenBy(marker => marker.Id)] };
}
