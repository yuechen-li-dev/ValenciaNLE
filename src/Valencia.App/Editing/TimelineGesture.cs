using System.Collections.Immutable;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;

namespace Valencia.App.Editing;

/// <summary>
/// Transient drag/trim proposal. Release commits exactly one transaction (one undo entry); cancellation never
/// changes the document. Linked clips move/trim together, edges snap to edit points, the playhead and marks,
/// and magnetic mode turns same-track moves into ripple reorders and trims into ripple trims.
/// </summary>
internal sealed class TimelineGesture
{
    private readonly EditorDocument editor;
    private TimelineClip? original;
    private TimelineTrack? originalTrack;
    private ImmutableArray<(TimelineTrack Track, TimelineClip Clip)> group = [];
    private SequenceTime pointerStart;
    private long revision;
    private int edge;
    public const double SnapPixels = 8;
    /// <summary>The operation on the grabbed clip; the full proposal is <see cref="Operations"/>.</summary>
    public EditOperation? Proposal { get; private set; }
    public IReadOnlyList<EditOperation> Operations { get; private set; } = [];
    public TimelineClip? Ghost { get; private set; }
    public IReadOnlyList<(Guid TrackId, TimelineClip Clip)> Ghosts { get; private set; } = [];
    /// <summary>Where the dragged edge snapped, for the snap indicator.</summary>
    public SequenceTime? SnapLine { get; private set; }
    public bool Valid { get; private set; }
    public string? Reason { get; private set; }
    public bool Active => original is not null;
    public TimelineGesture(EditorDocument editor) => this.editor = editor;

    public void Begin(TimelineClip clip, int edge, double x)
    {
        original = clip; this.edge = edge; pointerStart = editor.Viewport.Time(x); revision = editor.Session.Snapshot.Revision;
        group = EditorEdits.Group(editor, clip.Id); originalTrack = group[0].Track;
        Ghost = clip; Ghosts = [.. group.Select(g => (g.Track.Id, g.Clip))]; Proposal = null; Operations = []; SnapLine = null; Valid = true; Reason = null;
        editor.SelectedClip = clip.Id;
    }

    public void Move(double x, double y)
    {
        if (original is not { } c || originalTrack is null) return;
        try
        {
            var viewport = editor.Viewport; var sequence = editor.Sequence;
            int visibleRow = (int)Math.Floor((y - viewport.TrackTop) / viewport.Row);
            int row = visibleRow + viewport.FirstTrack;
            if (x < viewport.ClipLeft || x > viewport.ClipRight || y < viewport.TrackTop || visibleRow >= viewport.VisibleTracks || row < 0 || row >= sequence.Tracks.Length)
            { Reject("Outside the timeline."); return; }
            var track = sequence.Tracks[row];
            long delta = checked(viewport.Time(x).Ticks - pointerStart.Ticks);
            SnapLine = null;
            var ids = group.Select(g => g.Clip.Id).ToHashSet();
            if (edge == 0)
            {
                if (track.Kind != originalTrack.Kind) { Reject("Clips can only move to a track of the same kind."); return; }
                long start = Math.Max(0, checked(c.Start.Ticks + delta));
                start = SnapMove(start, c.Duration.Ticks, ids);
                if (editor.Magnetic && track.Id == originalTrack.Id) MagneticMove(c, start);
                else PlainMove(c, track, start - c.Start.Ticks);
            }
            else
            {
                if (track.Id != originalTrack.Id) { Reject("Trim stays on the clip's own track."); return; }
                long edgeTicks = edge < 0 ? c.Start.Ticks : c.End.Ticks;
                long target = SnapEdge(Math.Max(0, checked(edgeTicks + delta)), ids);
                Trim(c, target - edgeTicks);
            }
            if (Operations.Count == 0) { Valid = true; Reason = null; return; }
            var preview = editor.Session.Preview(Transaction());
            Valid = preview.Success || preview.Error == EditError.NoChange;
            Reason = Valid ? null : preview.Message ?? preview.Error.ToString();
        }
        catch (Exception e) when (e is ArgumentException or OverflowException or InvalidOperationException) { Reject(e.Message); }
    }

    private void PlainMove(TimelineClip c, TimelineTrack destination, long shift)
    {
        var operations = new List<EditOperation>(); var ghosts = new List<(Guid, TimelineClip)>();
        foreach (var (track, clip) in group)
        {
            var moved = clip with { Start = new(checked(clip.Start.Ticks + shift)) };
            var trackId = clip.Id == c.Id ? destination.Id : track.Id;
            operations.Add(new MoveClip(editor.SequenceId, clip.Id, trackId, moved.Start)); ghosts.Add((trackId, moved));
        }
        // Moving right: latest first; left: earliest first, so linked clips never cross each other mid-transaction.
        Set(shift > 0 ? [.. operations.OrderByDescending(o => ((MoveClip)o).Start.Ticks)] : [.. operations.OrderBy(o => ((MoveClip)o).Start.Ticks)], ghosts, c.Id);
    }

    /// <summary>Same-track magnetic move: lift the group out (closing its gap) and insert it at the nearest edit point.</summary>
    private void MagneticMove(TimelineClip c, long desiredStart)
    {
        var sequence = editor.Sequence; var tracks = EditorEdits.Unlocked(sequence);
        long gs = group.Min(g => g.Clip.Start.Ticks), ge = group.Max(g => g.Clip.End.Ticks), length = ge - gs;
        var ids = group.Select(g => g.Clip.Id).ToHashSet();
        if (tracks.SelectMany(t => t.Clips).Any(x => !ids.Contains(x.Id) && x.Start.Ticks < ge && x.End.Ticks > gs))
        { Reject("Magnetic move blocked: other media overlaps this clip's range. Lock that track or turn Magnet off."); return; }
        var removal = EditorEdits.RippleRange(editor.SequenceId, tracks, new(gs), new(ge));
        var lifted = editor.Session.Preview(new EditTransaction(Guid.NewGuid(), 1, revision, [new EditCommand(Guid.NewGuid(), 1, removal)]));
        if (!lifted.Success) { Reject(lifted.Message ?? "Cannot lift the clip."); return; }
        var after = new TimelineQueries(lifted.Snapshot).Sequence(editor.SequenceId);
        long groupStart = desiredStart - (c.Start.Ticks - gs);
        long wanted = groupStart >= gs ? Math.Max(gs, groupStart - length) : groupStart;
        // Magnetic placement always lands on an edit point of the closed-up timeline.
        long insertAt = EditorEdits.EditPoints(after).Append(after.Duration.Ticks).OrderBy(p => Math.Abs(p - wanted)).First();
        var operations = new List<EditOperation> { removal };
        operations.AddRange(EditorEdits.ShiftFrom(editor.SequenceId, EditorEdits.Unlocked(after), new(insertAt), length));
        var ghosts = new List<(Guid, TimelineClip)>();
        foreach (var (track, clip) in group)
        {
            var placed = clip with { Start = new(checked(insertAt + clip.Start.Ticks - gs)) };
            operations.Add(new InsertClip(editor.SequenceId, track.Id, placed)); ghosts.Add((track.Id, placed));
        }
        SnapLine = new(insertAt + (insertAt > gs ? length : 0));
        Set(operations, ghosts, c.Id);
    }

    private void Trim(TimelineClip c, long change)
    {
        var operations = new List<EditOperation>(); var ghosts = new List<(Guid, TimelineClip)>();
        foreach (var (track, clip) in group)
        {
            var trimmed = edge < 0
                ? editor.Magnetic ? clip with { SourceIn = clip.SourceIn.Shift(new(change)) } : clip with { Start = new(checked(clip.Start.Ticks + change)), SourceIn = clip.SourceIn.Shift(new(change)) }
                : clip with { SourceOut = clip.SourceOut.Shift(new(change)) };
            operations.Add(new TrimClip(editor.SequenceId, clip.Id, trimmed.Start, trimmed.SourceIn, trimmed.SourceOut)); ghosts.Add((track.Id, trimmed));
        }
        if (editor.Magnetic && change != 0)
        {
            // Ripple trim: everything after the trimmed edit follows it on all unlocked tracks.
            long editPoint = group.Max(g => g.Clip.End.Ticks);
            long shift = edge < 0 ? -change : change;
            var ids = group.Select(g => g.Clip.Id).ToHashSet();
            var tracks = EditorEdits.Unlocked(editor.Sequence);
            if (tracks.SelectMany(t => t.Clips).Any(x => !ids.Contains(x.Id) && x.Start.Ticks < editPoint && x.End.Ticks > editPoint))
            { Reject("Ripple trim blocked: media on another track spans this edit."); return; }
            var moves = tracks.SelectMany(t => t.Clips.Where(x => !ids.Contains(x.Id) && x.Start.Ticks >= editPoint)
                .Select(x => (EditOperation)new MoveClip(editor.SequenceId, x.Id, t.Id, new(checked(x.Start.Ticks + shift))))).ToList();
            moves = shift > 0 ? [.. moves.OrderByDescending(m => ((MoveClip)m).Start.Ticks)] : [.. moves.OrderBy(m => ((MoveClip)m).Start.Ticks)];
            // Lengthening needs room first; shortening frees room first.
            operations = shift > 0 ? [.. moves, .. operations] : [.. operations, .. moves];
        }
        Set(operations, ghosts, c.Id);
    }

    private long SnapMove(long start, long length, HashSet<Guid> ids)
    {
        if (!editor.Snapping) return Frame(start);
        var (head, headDistance) = Nearest(start, ids); var (tail, tailDistance) = Nearest(start + length, ids);
        if (head is { } h && headDistance <= tailDistance && headDistance <= Threshold) { SnapLine = new(h); return Math.Max(0, h); }
        if (tail is { } t && tailDistance < headDistance && tailDistance <= Threshold && t >= length) { SnapLine = new(t); return t - length; }
        return Frame(start);
    }

    private long SnapEdge(long position, HashSet<Guid> ids)
    {
        if (!editor.Snapping) return Frame(position);
        var (target, distance) = Nearest(position, ids);
        if (target is { } t && distance <= Threshold) { SnapLine = new(t); return t; }
        return Frame(position);
    }

    private long Threshold => (long)(SnapPixels / editor.Viewport.PixelsPerSecond * SequenceTime.TicksPerSecond);
    private long Frame(long ticks) => TimelineTime.SnapToFrame(new(ticks), editor.Sequence.Video.FrameRate, TimeRounding.NearestAwayFromZero).Ticks;

    private (long? Target, long Distance) Nearest(long position, HashSet<Guid> ids)
    {
        var points = editor.Sequence.Tracks.Where(t => !t.State.Locked).SelectMany(t => t.Clips).Where(x => !ids.Contains(x.Id))
            .SelectMany(x => new[] { x.Start.Ticks, x.End.Ticks }).Append(0L).Append(editor.TimelinePlayhead.Ticks);
        if (editor.MarkIn is { } i) points = points.Append(i.Ticks);
        if (editor.MarkOut is { } o) points = points.Append(o.Ticks);
        long? best = null; long distance = long.MaxValue;
        foreach (long p in points) { long d = Math.Abs(p - position); if (d < distance) { distance = d; best = p; } }
        return (best, distance);
    }

    private void Set(List<EditOperation> operations, List<(Guid, TimelineClip)> ghosts, Guid primary)
    {
        Operations = operations; Ghosts = ghosts;
        Ghost = ghosts.FirstOrDefault(g => g.Item2.Id == primary).Item2;
        Proposal = operations.FirstOrDefault(o => o is MoveClip m && m.ClipId == primary || o is TrimClip t && t.ClipId == primary || o is InsertClip i && i.Clip.Id == primary);
    }

    private void Reject(string reason) { Proposal = null; Operations = []; Ghost = original; Ghosts = [.. group.Select(g => (g.Track.Id, g.Clip))]; Valid = false; Reason = reason; SnapLine = null; }

    private EditTransaction Transaction() =>
        new(Guid.NewGuid(), 1, revision, [.. Operations.Select(o => new EditCommand(Guid.NewGuid(), 1, o))]);

    public bool Release()
    {
        bool committed = false;
        if (Valid && Operations.Count > 0)
        {
            var result = editor.Session.Execute(Transaction());
            editor.Status = result.Error == EditError.NoChange ? "Selected " + original?.Properties.Name : result.Message ?? (Operations.Count > 1 && group.Length > 1 ? $"Edited {group.Length} linked clips." : "Edit committed.");
            committed = result.Success;
        }
        else if (!Valid) editor.Status = "Rejected: " + (Reason ?? "locked/incompatible track, overlap, range or invalid destination.");
        Cancel(); return committed;
    }

    public void Cancel() { original = null; originalTrack = null; group = []; Proposal = null; Operations = []; Ghost = null; Ghosts = []; SnapLine = null; Valid = false; }
}
