using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;

namespace Valencia.App.Editing;

/// <summary>Transient proposal only. Release dispatches exactly one edit; cancellation never changes the document.</summary>
internal sealed class TimelineGesture
{
    private readonly EditorDocument editor;
    private TimelineClip? original;
    private SequenceTime pointerStart;
    private long revision;
    private int edge;
    public EditOperation? Proposal { get; private set; }
    public TimelineClip? Ghost { get; private set; }
    public bool Valid { get; private set; }
    public bool Active => original is not null;
    public TimelineGesture(EditorDocument editor) => this.editor = editor;
    public void Begin(TimelineClip clip, int edge, double x)
    {
        original = clip; this.edge = edge; pointerStart = editor.Viewport.Time(x); revision = editor.Session.Snapshot.Revision;
        Ghost = clip; Proposal = null; Valid = true; editor.SelectedClip = clip.Id;
    }
    public void Move(double x, double y)
    {
        if (original is not { } c) return;
        try
        {
            long delta = checked(editor.Viewport.Time(x).Ticks - pointerStart.Ticks);
            var snapped = TimelineTime.SnapToFrame(new(Math.Max(0, checked((edge == 1 ? c.End.Ticks : c.Start.Ticks) + delta))), editor.Sequence.Video.FrameRate, TimeRounding.NearestAwayFromZero);
            var viewport = editor.Viewport;
            int visibleRow = (int)Math.Floor((y - viewport.TrackTop) / TimelineViewport.RowHeight);
            int row = visibleRow + viewport.FirstTrack;
            if (x < viewport.ClipLeft || x > viewport.ClipRight || y < viewport.TrackTop || visibleRow >= viewport.VisibleTracks || row < 0 || row >= editor.Sequence.Tracks.Length) { Proposal = null; Ghost = c; Valid = false; return; }
            var track = editor.Sequence.Tracks[row];
            if (edge != 0 && !track.Clips.Any(clip => clip.Id == c.Id)) { Proposal = null; Ghost = c; Valid = false; return; }
            if (edge == 0)
            {
                Ghost = c with { Start = snapped };
                Proposal = new MoveClip(editor.SequenceId, c.Id, track.Id, snapped);
            }
            else
            {
                long change = checked(snapped.Ticks - (edge < 0 ? c.Start.Ticks : c.End.Ticks));
                Ghost = edge < 0 ? c with { Start = snapped, SourceIn = c.SourceIn.Shift(new(change)) } : c with { SourceOut = c.SourceOut.Shift(new(change)) };
                Proposal = new TrimClip(editor.SequenceId, c.Id, Ghost.Start, Ghost.SourceIn, Ghost.SourceOut);
            }
            var preview = editor.Session.Preview(new EditCommand(Guid.NewGuid(), 1, Proposal, revision));
            Valid = preview.Success || preview.Error == EditError.NoChange;
        }
        catch (Exception e) when (e is ArgumentException or OverflowException) { Proposal = null; Ghost = c; Valid = false; }
    }
    public bool Release()
    {
        bool committed = false;
        if (Valid && Proposal is { } operation)
        {
            var result = editor.Session.Execute(new EditCommand(Guid.NewGuid(), 1, operation, revision));
            editor.Status = result.Error == EditError.NoChange ? "Selected " + original?.Properties.Name : result.Message ?? "Edit committed."; committed = result.Success;
        }
        else if (!Valid) editor.Status = "Rejected: locked/incompatible track, overlap, range or invalid destination.";
        Cancel(); return committed;
    }
    public void Cancel() { original = null; Proposal = null; Ghost = null; Valid = false; }
}
