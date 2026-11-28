using System.Collections.Immutable;

namespace Valencia.Core.Timeline;

/// <summary>Where tracks go: video tracks stay grouped above audio tracks, as in Premiere.</summary>
public static class TrackLayout
{
    /// <summary>The order for a new track of <paramref name="kind"/>: after the last video track, or at the end for audio.</summary>
    public static int NewTrackOrder(IReadOnlyList<TimelineTrack> tracks, TrackKind kind)
    {
        if (kind != TrackKind.Video) return tracks.Count;
        int last = -1;
        for (int i = 0; i < tracks.Count; i++) if (tracks[i].Kind == TrackKind.Video) last = i;
        return last + 1;
    }

    /// <summary>
    /// Receiving tracks for media of one kind dropped or edited onto <paramref name="target"/>: the target first, then the
    /// tracks of that kind after it, then those before it. Linked streams therefore stack downward from where they land.
    /// </summary>
    public static IEnumerable<TimelineTrack> FromTarget(IReadOnlyList<TimelineTrack> tracks, TrackKind kind, Guid? target)
    {
        var ofKind = tracks.Where(t => t.Kind == kind).ToList();
        int start = target is { } id ? Math.Max(0, ofKind.FindIndex(t => t.Id == id)) : 0;
        return ofKind.Skip(start).Concat(ofKind.Take(start));
    }
}
