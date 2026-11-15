using System.Collections.Immutable;

namespace Valencia.Core.Timeline;

/// <summary>Immutable per-clip caption groups compiled once; frame evaluation performs bounded binary searches.</summary>
public sealed class CaptionIndex
{
    private readonly Dictionary<Guid, ImmutableArray<CaptionWord>[]> groups = [];
    public CaptionIndex(SequenceDocument sequence)
    {
        if (sequence.Creator is not { } creator) return;
        int retained = 0;
        foreach (var clip in sequence.Tracks.SelectMany(t => t.Clips))
        {
            var words = creator.Words.Where(w => w.AssetId == clip.AssetId && w.Start.CompareTo(clip.SourceOut) < 0 && w.End.CompareTo(clip.SourceIn) > 0)
                .OrderBy(w => w.Start, Comparer<SourcePosition>.Create((a, b) => a.CompareTo(b))).ToArray();
            retained = checked(retained + words.Length);
            if (retained > 500000) throw new NotSupportedException("Caption placement index exceeds its word budget.");
            groups.Add(clip.Id, words.Chunk(6).Select(ImmutableArray.CreateRange).ToArray());
        }
    }
    public CaptionLine? At(IEnumerable<ActiveClip> active)
    {
        foreach (var clip in active.OrderByDescending(c => c.Kind == TrackKind.Video).ThenByDescending(c => c.TrackOrder))
        {
            if (!groups.TryGetValue(clip.Clip.Id, out var lines)) continue;
            int left = 0, right = lines.Length - 1, found = -1;
            while (left <= right)
            {
                int middle = left + (right - left) / 2;
                if (lines[middle][0].Start.CompareTo(clip.SourcePosition) <= 0) { found = middle; left = middle + 1; }
                else right = middle - 1;
            }
            if (found < 0 || lines[found][^1].End.CompareTo(clip.SourcePosition) <= 0) continue;
            int highlighted = -1;
            for (int i = 0; i < lines[found].Length; i++)
                if (lines[found][i].Start.CompareTo(clip.SourcePosition) <= 0 && lines[found][i].End.CompareTo(clip.SourcePosition) > 0) highlighted = i;
            return new(lines[found], highlighted);
        }
        return null;
    }
}
