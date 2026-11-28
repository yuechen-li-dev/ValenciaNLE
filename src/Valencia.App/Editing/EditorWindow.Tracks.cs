using Machina.Standard.Widgets;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.App.Editing;

/// <summary>
/// Track management (grouped adds, Premiere's track-header menu, delete), the track scrollbar, and dragging media from
/// the bin onto the timeline (overwrite at the drop point, Ctrl+drop inserts, as in Premiere).
/// </summary>
internal sealed partial class EditorWindow
{
    private const double DragThreshold = 6;
    private (Guid Asset, (double X, double Y) Start)? mediaCandidate;
    private bool mediaDragging;
    private Task<ImportedAsset>? mediaProbe;
    private double? trackGrab;

    // ------------------------------------------------------------------ tracks

    private void AddTrackGrouped(TrackKind kind)
    {
        var tracks = Editor.Sequence.Tracks;
        int order = TrackLayout.NewTrackOrder(tracks, kind);
        if (Editor.Commit(new AddTrack(Editor.SequenceId, Guid.NewGuid(), kind, order, kind == TrackKind.Video ? "Video" : "Audio")).Success)
            Editor.Status = $"Added {Label(Editor.Sequence.Tracks[order])}.";
    }

    private string Label(TimelineTrack track)
    {
        var tracks = Editor.Sequence.Tracks;
        int number = tracks.TakeWhile(t => t.Id != track.Id).Count(t => t.Kind == track.Kind) + 1;
        return (track.Kind == TrackKind.Video ? "V" : "A") + number;
    }

    /// <summary>Track actions from the header menu and the palette; false when the name is not one.</summary>
    private bool TrackAction(string name)
    {
        if (name == "TrackDeleteEmpty") { DeleteEmptyTracks(); return true; }
        if (name == "TrackDeleteTarget")
        {
            if (Editor.TargetTrack is { } target && Editor.Sequence.Tracks.Any(t => t.Id == target)) DeleteTrack(target);
            else Editor.Status = "Target a track first: click its V1 or A1 label.";
            return true;
        }
        if (name.StartsWith("TrackDelete:", StringComparison.Ordinal)) { DeleteTrack(Guid.Parse(name["TrackDelete:".Length..])); return true; }
        return false;
    }

    private void DeleteTrack(Guid id)
    {
        var track = Editor.Sequence.Tracks.First(t => t.Id == id);
        string label = Label(track);
        int clips = track.Clips.Length;
        PauseTransport();
        var result = Editor.Commit(new RemoveTrack(Editor.SequenceId, id));
        if (!result.Success) { Editor.Status = result.Message ?? result.Error.ToString(); return; }
        if (Editor.TargetTrack == id) Editor.TargetTrack = null;
        Editor.SetAudioTrackSolo(id, false);
        Editor.Status = clips == 0 ? $"Deleted {label}." : $"Deleted {label} and its {clips} clip{(clips == 1 ? "" : "s")}. Ctrl+Z restores it.";
        Seek(Editor.Playhead);
    }

    /// <summary>Premiere's "Delete Tracks: all empty tracks" as one undoable edit. The sequence keeps at least one track.</summary>
    private void DeleteEmptyTracks()
    {
        var tracks = Editor.Sequence.Tracks;
        var empty = tracks.Where(t => t.Clips.IsEmpty && !t.State.Locked).ToList();
        if (empty.Count == tracks.Length) empty.RemoveAt(0);
        if (empty.Count == 0) { Editor.Status = "No empty unlocked tracks to delete."; return; }
        PauseTransport();
        var result = Editor.Commit(empty.Select(t => (EditOperation)new RemoveTrack(Editor.SequenceId, t.Id)).ToArray());
        if (!result.Success) { Editor.Status = result.Message ?? result.Error.ToString(); return; }
        if (Editor.TargetTrack is { } target && empty.Any(t => t.Id == target)) Editor.TargetTrack = null;
        Editor.Status = $"Deleted {empty.Count} empty track{(empty.Count == 1 ? "" : "s")}. Ctrl+Z restores them.";
        Seek(Editor.Playhead);
    }

    /// <summary>The track-header context menu, or null outside the header column's rows.</summary>
    private IReadOnlyList<WidgetOption>? TrackMenu(double y)
    {
        var viewport = Editor.Viewport; var tracks = Editor.Sequence.Tracks;
        int index = viewport.FirstTrack + (int)((y - viewport.TrackTop) / viewport.Row);
        bool anyEmpty = tracks.Any(t => t.Clips.IsEmpty && !t.State.Locked) && tracks.Length > 1;
        var items = new List<WidgetOption> { new("AddVideo", "Add video track"), new("AddAudio", "Add audio track") };
        if (index >= 0 && index < tracks.Length)
        {
            var track = tracks[index];
            string detail = track.Clips.IsEmpty ? "" : $" ({track.Clips.Length} clip{(track.Clips.Length == 1 ? "" : "s")})";
            items.Add(new("TrackDelete:" + track.Id, $"Delete track {Label(track)}{detail}", track.State.Locked || tracks.Length == 1));
        }
        items.Add(new("TrackDeleteEmpty", "Delete all empty tracks", !anyEmpty));
        return items;
    }

    // ------------------------------------------------------------------ scrollbar

    private bool BeginTrackScroll(double x, double y)
    {
        if (Editor.TrackScrollbar is not var (groove, thumb)) return false;
        if (x < groove.X || x > groove.X + groove.Width || y < groove.Y || y > groove.Y + groove.Height) return false;
        var viewport = Editor.Viewport;
        if (y >= thumb.Y && y <= thumb.Y + thumb.Height) { trackGrab = y - thumb.Y; CapturePointer(); }
        else viewport.FirstTrack = Math.Clamp(viewport.FirstTrack + (y < thumb.Y ? -1 : 1) * viewport.VisibleTracks, 0, MaxFirstTrack());
        lastUi = -1;
        return true;
    }

    private int MaxFirstTrack() => Math.Max(0, Editor.Sequence.Tracks.Length - Editor.Viewport.VisibleTracks);

    private bool MoveTrackScroll(double y)
    {
        if (trackGrab is not { } grab || Editor.TrackScrollbar is not var (groove, thumb)) return false;
        double travel = Math.Max(1, groove.Height - thumb.Height);
        Editor.Viewport.FirstTrack = (int)Math.Round(Math.Clamp((y - grab - groove.Y) / travel, 0, 1) * MaxFirstTrack());
        lastUi = -1;
        return true;
    }

    private bool ReleaseTrackScroll()
    {
        if (trackGrab is null) return false;
        trackGrab = null;
        ReleasePointer();
        return true;
    }

    // ------------------------------------------------------------------ bin -> timeline drag

    private void ArmMediaDrag(string? action, (double X, double Y) p)
    {
        mediaCandidate = action is { } name && name.StartsWith("Asset:", StringComparison.Ordinal) && Guid.TryParse(name[6..], out var id)
            ? (id, p) : null;
        mediaDragging = false;
    }

    private ImportedAsset? DraggedMedia()
    {
        if (mediaCandidate is not { } candidate) return null;
        if (imports.TryGetValue(candidate.Asset, out var imported)) return imported;
        if (mediaProbe is { IsCompletedSuccessfully: true } done && done.Result.Asset.Id == candidate.Asset)
            return imports[candidate.Asset] = done.Result;
        if (mediaProbe is null || mediaProbe.IsCompleted)
        {
            var asset = Editor.Session.Snapshot.Document.Assets.First(a => a.Id == candidate.Asset);
            mediaProbe = Task.Run(() => MediaAssetProbe.Import(candidate.Asset, asset.SourcePath));
        }
        return null;
    }

    /// <summary>Where a drop at <paramref name="p"/> lands: a time on the timeline and the track under the pointer (null below the last track).</summary>
    private (SequenceTime At, Guid? Track)? DropPoint((double X, double Y) p)
    {
        var viewport = Editor.Viewport;
        if (p.X < viewport.ClipLeft || p.X > viewport.ClipRight || p.Y < view.Layout.RulerY || p.Y > viewport.TrackBottom) return null;
        var at = new SequenceTime(Math.Max(0, Snap(p.X).Ticks));
        int index = viewport.FirstTrack + (int)Math.Floor((p.Y - viewport.TrackTop) / viewport.Row);
        var tracks = Editor.Sequence.Tracks;
        return (at, p.Y >= viewport.TrackTop && index >= 0 && index < tracks.Length ? tracks[index].Id : null);
    }

    private bool MoveMediaDrag((double X, double Y) p)
    {
        if (mediaCandidate is not { } candidate) return false;
        if (!mediaDragging)
        {
            if (Math.Abs(p.X - candidate.Start.X) < DragThreshold && Math.Abs(p.Y - candidate.Start.Y) < DragThreshold) return false;
            mediaDragging = true;
            pointer.Reset(); // a drag is not a click on the bin row
            Editor.SelectedAsset = candidate.Asset;
            CapturePointer();
        }
        var imported = DraggedMedia();
        var drop = DropPoint(p);
        if (imported is null) { Editor.DropGhosts = []; Editor.Status = "Reading media for the drop..."; }
        else if (drop is not { } point) { Editor.DropGhosts = []; Editor.Status = "Drag onto the timeline: drop to overwrite, Ctrl+drop to insert."; }
        else
        {
            var plan = EditorEdits.Plan(Editor, imported, point.At, point.Track);
            Editor.DropGhosts = [.. plan.OfType<InsertClip>().Select(i => (i.TrackId, i.Clip.Start, i.Clip.End))];
            bool insert = keys.Control || Editor.Magnetic;
            int added = plan.OfType<AddTrack>().Count();
            Editor.Status = $"{(insert ? "Insert" : "Overwrite")} at {Timecode.Format(point.At, Editor.Sequence.Video.FrameRate)}"
                + (added > 0 ? $" (+{added} new track{(added == 1 ? "" : "s")})" : "") + ". Release to place, Esc cancels.";
        }
        lastUi = -1;
        return true;
    }

    private bool ReleaseMediaDrag((double X, double Y) p)
    {
        if (mediaCandidate is null) return false;
        bool dragging = mediaDragging;
        var imported = dragging ? DraggedMedia() : null;
        var drop = dragging ? DropPoint(p) : null;
        mediaCandidate = null; mediaDragging = false; Editor.DropGhosts = [];
        if (!dragging) return false; // an ordinary click selects the bin row
        ReleasePointer();
        if (imported is null || drop is not { } point)
        {
            Editor.Status = imported is null ? "Media was still loading; drag it again." : "Drop cancelled.";
            return true;
        }
        PauseTransport();
        ExitSource();
        var playhead = Editor.Playhead;
        // Dragging places media without moving the playhead, unlike the , and . edits.
        var result = EditorEdits.InsertAt(Editor, imported, overwrite: !keys.Control, at: point.At, target: point.Track);
        if (result.Success)
        {
            Editor.Playhead = playhead;
            ConsiderPreviewProxy(imported);
            Editor.Status = (keys.Control || Editor.Magnetic ? "Inserted " : "Placed ") + imported.Asset.Name
                + " at " + Timecode.Format(point.At, Editor.Sequence.Video.FrameRate) + ".";
            Seek(playhead);
        }
        else Editor.Status = result.Message ?? result.Error.ToString();
        return true;
    }

    private void CancelTrackGestures()
    {
        trackGrab = null;
        mediaCandidate = null;
        mediaDragging = false;
        Editor.DropGhosts = [];
    }
}
