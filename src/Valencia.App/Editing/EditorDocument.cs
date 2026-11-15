using System.Collections.Immutable;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.App.Editing;

internal sealed class EditorDocument
{
    public EditingSession Session { get; private set; } = null!;
    public Guid SequenceId { get; private set; }
    public Guid? SelectedClip { get; set; }
    public Guid? SelectedAsset { get; set; }
    public Guid? SelectedProfile { get; set; }
    public Guid? TargetTrack { get; set; }
    public int FirstAsset { get; set; }
    public SequenceTime Playhead { get; set; }
    /// <summary>Transient sequence In/Out marks (Premiere I/O); not saved with the project.</summary>
    public SequenceTime? MarkIn { get; set; }
    public SequenceTime? MarkOut { get; set; }
    public ProjectDocument? SavedDocument { get; set; }
    public string? ProjectPath { get; set; }
    public bool Dirty => SavedDocument is null || !DocumentSemantics.Equal(SavedDocument, Session.Snapshot.Document);
    public string Status { get; set; } = "Import media to begin.";
    public TimelineViewport Viewport { get; } = new();
    public Dictionary<(Guid Asset, int Stream), Valencia.Analysis.WaveformIndex> Waveforms { get; } = [];
    public Valencia.Engine.RoughCutProposal? ProposedCut { get; set; }
    public Valencia.Analysis.AnalysisArtifact? Transcript { get; set; }
    public Valencia.Engine.TextEditProposal? ProposedTextCut { get; set; }
    public bool TranscriptMode { get; set; }
    public bool ObsMode { get; set; }
    public string ObsConnection { get; set; } = "Disconnected";
    public string ObsRecording { get; set; } = "No recording session";
    public int TranscriptFirstWord { get; set; }
    public int? TranscriptSelectionStart { get; set; }
    public int? TranscriptSelectionEnd { get; set; }
    public SequenceDocument Sequence => new TimelineQueries(Session.Snapshot).Sequence(SequenceId);
    public EditorDocument() => New();
    public void New()
    {
        SelectedProfile = null;
        Waveforms.Clear(); ProposedCut = null; Transcript = null; ProposedTextCut = null; TranscriptMode = false;
        Session = new(ProjectDocument.Create(Guid.NewGuid(), "Untitled")); SequenceId = Guid.NewGuid();
        var ops = new List<EditOperation> { new CreateSequence(SequenceId, "Main", new(1280, 720, new(30, 1)), 48000) };
        foreach (var (kind, name) in new[] { (TrackKind.Video, "V1"), (TrackKind.Video, "V2"), (TrackKind.Audio, "A1"), (TrackKind.Audio, "A2") })
            ops.Add(new AddTrack(SequenceId, Guid.NewGuid(), kind, ops.Count - 1, name));
        Commit(ops); Session = new(Session.Snapshot); // Required sequence/track setup is the baseline, not an undoable user edit.
        SelectedClip = null; SelectedAsset = null; TargetTrack = null; FirstAsset = 0; Viewport.Reset(); Playhead = default; MarkIn = null; MarkOut = null; SavedDocument = Session.Snapshot.Document; ProjectPath = null;
    }
    public void Open(ProjectSnapshot snapshot, string path)
    {
        SelectedProfile = null;
        Waveforms.Clear(); ProposedCut = null; Transcript = null; ProposedTextCut = null; TranscriptMode = false;
        if (snapshot.Document.Sequences.IsDefaultOrEmpty) throw new InvalidDataException("Project has no sequence. Create a sequence before editing it.");
        Session = new(snapshot); SequenceId = snapshot.Document.Sequences[0].Id; SavedDocument = snapshot.Document; ProjectPath = path;
        SelectedClip = null; SelectedAsset = snapshot.Document.Assets.FirstOrDefault()?.Id; TargetTrack = null; FirstAsset = 0; Viewport.Reset(); Playhead = default; MarkIn = null; MarkOut = null;
    }
    public EditResult Commit(params EditOperation[] operations) => Commit((IEnumerable<EditOperation>)operations);
    public EditResult Commit(IEnumerable<EditOperation> operations)
    {
        var result = Session.Execute(new EditTransaction(Guid.NewGuid(), 1, Session.Snapshot.Revision,
            [.. operations.Select(op => new EditCommand(Guid.NewGuid(), 1, op))]));
        Status = result.Success ? "Edit committed." : result.Message ?? result.Error.ToString(); return result;
    }
    public void Register(ImportedAsset imported) { var result = Commit(new AddAsset(imported.Asset)); if (result.Success) { SelectedAsset = imported.Asset.Id; FirstAsset = Math.Max(0, Session.Snapshot.Document.Assets.Length - 5); } }
    public EditResult Insert(ImportedAsset imported)
    {
        return Commit(Valencia.Engine.InsertionCommands.Build(Session.Snapshot, SequenceId, imported, Playhead, TargetTrack));
    }
    public TimelineClip? Selection => Sequence.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == SelectedClip);
    public EditResult? Split()
    {
        var clips = Selection is { } selected ? new[] { selected } : Sequence.Tracks.Where(t => !t.State.Locked).SelectMany(t => t.Clips).Where(c => c.Start.Ticks < Playhead.Ticks && c.End.Ticks > Playhead.Ticks).ToArray();
        return clips.Length == 0 ? null : Commit(clips.Select(c => (EditOperation)new SplitClip(SequenceId, c.Id, Playhead, Guid.NewGuid())));
    }
    /// <summary>Deletes the selection and its linked clips; ripple closes the gap on all unlocked tracks (see EditorEdits).</summary>
    public EditResult? Delete(bool ripple) => EditorEdits.DeleteSelection(this, ripple);
    public EditResult History(bool redo)
    {
        var request = new SessionRequest(Guid.NewGuid(), 1, Session.Snapshot.Revision);
        var result = redo ? Session.Redo(request) : Session.Undo(request); Status = result.Message ?? (redo ? "Redo" : "Undo"); return result;
    }
}
