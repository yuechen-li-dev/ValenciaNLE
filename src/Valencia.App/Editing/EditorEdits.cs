using System.Collections.Immutable;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.App.Editing;

/// <summary>
/// Premiere-style timeline edits composed from existing Core operations; nothing here bypasses the reducer.
/// A locked track is treated as untargeted and is never changed. Clips are "linked" when they come from different
/// streams of the same asset, overlap on the timeline and share the same source-minus-sequence intercept
/// (the alignment rule the rough-cut and transcript planners already use). This is inferred, not persisted.
/// </summary>
internal static class EditorEdits
{
    public static bool LinkedTo(TimelineClip a, TimelineClip b) =>
        a.Id != b.Id && a.AssetId == b.AssetId && a.StreamIndex != b.StreamIndex
        && a.Start.Ticks < b.End.Ticks && b.Start.Ticks < a.End.Ticks
        && a.SourceIn.Shift(new(-a.Start.Ticks)).EquivalentTo(b.SourceIn.Shift(new(-b.Start.Ticks)));

    public static ImmutableArray<(TimelineTrack Track, TimelineClip Clip)> LinkedGroup(SequenceDocument sequence, Guid clipId)
    {
        var all = sequence.Tracks.SelectMany(t => t.Clips.Select(c => (Track: t, Clip: c))).ToArray();
        var root = all.First(x => x.Clip.Id == clipId);
        return [root, .. all.Where(x => x.Track.Id != root.Track.Id && !x.Track.State.Locked && LinkedTo(root.Clip, x.Clip))];
    }

    public static ImmutableArray<TimelineTrack> Unlocked(SequenceDocument sequence) => [.. sequence.Tracks.Where(t => !t.State.Locked)];

    public static ImmutableArray<long> EditPoints(SequenceDocument sequence) =>
        [.. Unlocked(sequence).SelectMany(t => t.Clips).SelectMany(c => new[] { c.Start.Ticks, c.End.Ticks }).Append(0L).Distinct().Order()];

    public static SequenceTime? PreviousEdit(SequenceDocument sequence, SequenceTime at)
    {
        var points = EditPoints(sequence).Where(t => t < at.Ticks).ToArray();
        return points.Length == 0 ? null : new SequenceTime(points[^1]);
    }

    public static SequenceTime? NextEdit(SequenceDocument sequence, SequenceTime at)
    {
        var points = EditPoints(sequence).Where(t => t > at.Ticks).ToArray();
        return points.Length == 0 ? null : new SequenceTime(points[0]);
    }

    /// <summary>Removes [start, end) on the given tracks and closes the gap; clips spanning the whole range get a right-fragment ID.</summary>
    public static RippleDelete RippleRange(Guid sequenceId, IReadOnlyCollection<TimelineTrack> tracks, SequenceTime start, SequenceTime end) =>
        new(sequenceId, [.. tracks.Select(t => t.Id)], start, end,
            [.. tracks.SelectMany(t => t.Clips).Where(c => c.Start.Ticks < start.Ticks && c.End.Ticks > end.Ticks)
                .Select(c => new RippleSplitId(c.Id, Guid.NewGuid()))]);

    /// <summary>Removes [start, end) on the given tracks and leaves the gap (Premiere "lift").</summary>
    public static IEnumerable<EditOperation> LiftRange(Guid sequenceId, IEnumerable<TimelineTrack> tracks, SequenceTime start, SequenceTime end)
    {
        foreach (var track in tracks)
            foreach (var clip in track.Clips.Where(c => c.Start.Ticks < end.Ticks && c.End.Ticks > start.Ticks))
            {
                Guid middle = clip.Id;
                if (clip.Start.Ticks < start.Ticks) { var right = Guid.NewGuid(); yield return new SplitClip(sequenceId, clip.Id, start, right); middle = right; }
                if (clip.End.Ticks > end.Ticks) yield return new SplitClip(sequenceId, middle, end, Guid.NewGuid());
                yield return new DeleteClip(sequenceId, middle);
            }
    }

    /// <summary>Delete/ripple-delete the selected clip together with its linked clips on other unlocked tracks.</summary>
    public static EditResult? DeleteSelection(EditorDocument editor, bool ripple)
    {
        if (editor.Selection is not { } clip) return null;
        var sequence = editor.Sequence;
        var group = LinkedGroup(sequence, clip.Id);
        if (group[0].Track.State.Locked) return Refuse(editor, "Track is locked.");
        EditResult result;
        if (!ripple) result = editor.Commit(group.Select(g => (EditOperation)new DeleteClip(editor.SequenceId, g.Clip.Id)));
        else
        {
            long start = group.Min(g => g.Clip.Start.Ticks), end = group.Max(g => g.Clip.End.Ticks);
            var ids = group.Select(g => g.Clip.Id).ToHashSet();
            var tracks = Unlocked(sequence);
            // Never desync silently: other media inside the range on a rippling track blocks the edit.
            var blocker = tracks.SelectMany(t => t.Clips.Select(c => (Track: t, Clip: c)))
                .FirstOrDefault(x => !ids.Contains(x.Clip.Id) && x.Clip.Start.Ticks < end && x.Clip.End.Ticks > start);
            if (blocker.Clip is not null)
                return Refuse(editor, $"Ripple blocked: {blocker.Track.Name} has other media in this range. Lock {blocker.Track.Name} to ripple without it.");
            result = editor.Commit(RippleRange(editor.SequenceId, tracks, new(start), new(end)));
        }
        if (result.Success) { editor.SelectedClip = null; editor.Status = Linked(group.Length, ripple ? "Ripple deleted" : "Deleted"); }
        return result;
    }

    /// <summary>Q / W: ripple-trim from the previous edit point to the playhead, or from the playhead to the next edit point, on all unlocked tracks.</summary>
    public static EditResult? RippleTrim(EditorDocument editor, bool previous)
    {
        var sequence = editor.Sequence; var tracks = Unlocked(sequence);
        if (tracks.Length == 0) return Refuse(editor, "All tracks are locked.");
        var at = new SequenceTime(Math.Min(editor.Playhead.Ticks, sequence.Duration.Ticks));
        SequenceTime? bound = previous ? PreviousEdit(sequence, at) : NextEdit(sequence, at);
        if (bound is not { } edge) return Refuse(editor, previous ? "No edit point before the playhead." : "No edit point after the playhead.");
        var (start, end) = previous ? (edge, at) : (at, edge);
        var result = editor.Commit(RippleRange(editor.SequenceId, tracks, start, end));
        if (result.Success) { editor.Playhead = start; editor.Status = previous ? "Ripple trimmed previous edit to playhead." : "Ripple trimmed next edit to playhead."; }
        return result;
    }

    public static bool MarkedRange(EditorDocument editor, out SequenceTime start, out SequenceTime end)
    {
        long duration = editor.Sequence.Duration.Ticks;
        start = editor.MarkIn ?? default; end = new(Math.Min(editor.MarkOut?.Ticks ?? duration, duration));
        return (editor.MarkIn is not null || editor.MarkOut is not null) && end.Ticks > start.Ticks;
    }

    /// <summary>"'" extract (ripple) or ";" lift the marked In/Out range on all unlocked tracks.</summary>
    public static EditResult? RemoveMarked(EditorDocument editor, bool extract)
    {
        if (!MarkedRange(editor, out var start, out var end)) return Refuse(editor, "Set In (I) and/or Out (O) around media first.");
        var tracks = Unlocked(editor.Sequence);
        if (tracks.Length == 0) return Refuse(editor, "All tracks are locked.");
        List<EditOperation> operations = extract ? [RippleRange(editor.SequenceId, tracks, start, end)] : [.. LiftRange(editor.SequenceId, tracks, start, end)];
        if (operations.Count == 0) return Refuse(editor, "Nothing to lift between In and Out.");
        var result = editor.Commit(operations);
        if (result.Success) { editor.Playhead = start; editor.MarkIn = null; editor.MarkOut = null; editor.SelectedClip = null; editor.Status = extract ? "Extracted In/Out range." : "Lifted In/Out range."; }
        return result;
    }

    /// <summary>"," insert (shift later media right on all unlocked tracks) or "." overwrite (replace media on the receiving tracks).</summary>
    public static EditResult InsertAt(EditorDocument editor, ImportedAsset imported, bool overwrite)
    {
        var sequence = editor.Sequence; var at = editor.Playhead;
        var built = Valencia.Engine.InsertionCommands.Build(editor.Session.Snapshot, editor.SequenceId, imported, at, editor.TargetTrack);
        var inserts = built.OfType<InsertClip>().ToArray();
        if (inserts.Length == 0) return editor.Commit(built);
        var end = new SequenceTime(inserts.Max(i => i.Clip.End.Ticks));
        var operations = new List<EditOperation>(built.OfType<AddTrack>());
        if (overwrite)
        {
            var receiving = inserts.Select(i => i.TrackId).ToHashSet();
            operations.AddRange(LiftRange(editor.SequenceId, sequence.Tracks.Where(t => receiving.Contains(t.Id)), at, end));
        }
        else
        {
            long shift = end.Ticks - at.Ticks;
            foreach (var track in Unlocked(sequence))
            {
                var moves = new List<(Guid Id, long Start)>();
                foreach (var clip in track.Clips)
                {
                    if (clip.Start.Ticks >= at.Ticks) moves.Add((clip.Id, clip.Start.Ticks));
                    else if (clip.End.Ticks > at.Ticks)
                    {
                        var right = Guid.NewGuid(); operations.Add(new SplitClip(editor.SequenceId, clip.Id, at, right));
                        moves.Add((right, at.Ticks));
                    }
                }
                // Latest first, so every intermediate document stays non-overlapping.
                foreach (var (id, start) in moves.OrderByDescending(m => m.Start))
                    operations.Add(new MoveClip(editor.SequenceId, id, track.Id, new(checked(start + shift))));
            }
        }
        operations.AddRange(inserts);
        var result = editor.Commit(operations);
        if (result.Success) editor.Status = overwrite ? "Overwrite edit." : "Insert edit.";
        return result;
    }

    private static string Linked(int count, string verb) => count > 1 ? $"{verb} {count} linked clips." : $"{verb} clip.";
    private static EditResult? Refuse(EditorDocument editor, string message) { editor.Status = message; return null; }
}
