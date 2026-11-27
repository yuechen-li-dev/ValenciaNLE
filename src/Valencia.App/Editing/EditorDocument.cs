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
    /// <summary>Titles are clips: the selected title is the selected clip when that clip is a title.</summary>
    public Guid? SelectedTitle
    {
        get => Selection?.Title is not null ? SelectedClip : null;
        set { if (value is not null) SelectedClip = value; else if (Selection?.Title is not null) SelectedClip = null; }
    }
    public EffectDrag? EffectDrag { get; set; }
    public Guid? SelectedAsset { get; set; }
    public Guid? SelectedProfile { get; set; }
    public Guid? TargetTrack { get; set; }
    public int FirstAsset { get; set; }
    public SequenceTime Playhead { get; set; }
    /// <summary>Transient sequence In/Out marks (Premiere I/O); not saved with the project.</summary>
    public SequenceTime? MarkIn { get; set; }
    public SequenceTime? MarkOut { get; set; }
    /// <summary>Timeline behavior toggles (transient UI preferences).</summary>
    public bool Snapping { get; set; } = true;
    public bool LinkedSelection { get; set; } = true;
    public bool Magnetic { get; set; }
    private ImmutableHashSet<Guid> soloedAudioTracks = ImmutableHashSet<Guid>.Empty;
    public ImmutableHashSet<Guid> SoloedAudioTracks => soloedAudioTracks.Count == 0 ? soloedAudioTracks
        : soloedAudioTracks.Intersect(Sequence.Tracks.Where(track => track.Kind == TrackKind.Audio).Select(track => track.Id));

    public bool SetAudioTrackSolo(Guid trackId, bool soloed)
    {
        if (!Sequence.Tracks.Any(track => track.Id == trackId && track.Kind == TrackKind.Audio)) return false;
        var current = SoloedAudioTracks;
        var next = soloed ? current.Add(trackId) : current.Remove(trackId);
        if (ReferenceEquals(current, next)) return false;
        soloedAudioTracks = next;
        return true;
    }

    /// <summary>When set, the monitor shows this media instead of the sequence; Playhead is then source time.</summary>
    public SourceView? Source { get; set; }
    public SequenceTime ProgramPlayhead { get; set; }
    public SequenceTime? ScrubTarget { get; set; }
    public SequenceTime TimelinePlayhead => ScrubTarget ?? (Source is null ? Playhead : ProgramPlayhead);
    public bool Reversing { get; set; }
    public SequenceTime MonitorDuration => Source?.Duration ?? Sequence.Duration;
    public FrameRate MonitorFrameRate => Source?.Snapshot.Document.Sequences[0].Video.FrameRate ?? Sequence.Video.FrameRate;
    public Dictionary<Guid, (SequenceTime? In, SequenceTime? Out)> SourceMarks { get; } = [];
    public ProjectDocument? SavedDocument { get; set; }
    public string? ProjectPath { get; set; }
    public bool Dirty => SavedDocument is null || !DocumentSemantics.Equal(SavedDocument, Session.Snapshot.Document);
    public string Status { get; set; } = "Import media to begin.";
    public TimelineViewport Viewport { get; } = new();
    public Dictionary<(Guid Asset, int Stream), Valencia.Analysis.WaveformIndex> Waveforms { get; } = [];
    public Valencia.Engine.RoughCutProposal? ProposedCut { get; set; }
    public Valencia.Analysis.AnalysisArtifact? Transcript { get; set; }
    public Valencia.Engine.TextEditProposal? ProposedTextCut { get; set; }
    public bool HasAgentProposal { get; set; }
    public bool TranscriptMode { get; set; }
    public bool ControlsMode { get; set; }
    /// <summary>The Mix tab: one strip per audio track plus the master.</summary>
    public bool MixMode { get; set; }
    /// <summary>Meter ballistics, fed from the playback output tap every UI frame.</summary>
    public AudioMeter Meter { get; } = new();
    /// <summary>Window-space fader travel for each mix strip ("master" or a track id), written by the view.</summary>
    public Dictionary<string, Machina.Layout.Geometry.Rect> MixFaders { get; } = [];
    /// <summary>The fader being dragged and its uncommitted level.</summary>
    public (string Key, double Db)? FaderPreview { get; set; }
    public bool ObsMode { get; set; }
    public string ObsConnection { get; set; } = "Disconnected";
    public string ObsRecording { get; set; } = "No recording session";
    /// <summary>OBS reports an active (not paused) recording; the monitor shows a REC badge.</summary>
    public bool Recording { get; set; }
    /// <summary>Full-monitor title card, shown until <see cref="CardUntil"/> (window clock seconds) or any key.</summary>
    public TitleCard? Card { get; set; }
    public double CardUntil { get; set; }
    public int TranscriptFirstWord { get; set; }
    /// <summary>Words/assets the view fitted last frame; paging uses them so Prev/Next and the wheel move by a visible page.</summary>
    public int TranscriptPageSize { get; set; } = 6;
    public int MediaPageSize { get; set; } = 4;
    public int? TranscriptSelectionStart { get; set; }
    public int? TranscriptSelectionEnd { get; set; }
    public SequenceDocument Sequence => new TimelineQueries(Session.Snapshot).Sequence(SequenceId);
    public EditorDocument() => New();

    public EditResult AddSequence()
    {
        var current = Sequence;
        var id = Guid.NewGuid();
        int number = Session.Snapshot.Document.Sequences.Length + 1;
        var result = Commit(SequenceSetup(id, $"Sequence {number}", current.Video, current.AudioSampleRate));
        if (result.Success) SelectSequence(id);
        return result;
    }

    private static IEnumerable<EditOperation> SequenceSetup(Guid id, string name, VideoFormat format, int audioRate)
    {
        yield return new CreateSequence(id, name, format, audioRate);
        yield return new AddTrack(id, Guid.NewGuid(), TrackKind.Video, 0, "V1");
        yield return new AddTrack(id, Guid.NewGuid(), TrackKind.Video, 1, "V2");
        yield return new AddTrack(id, Guid.NewGuid(), TrackKind.Audio, 2, "A1");
        yield return new AddTrack(id, Guid.NewGuid(), TrackKind.Audio, 3, "A2");
    }

    public void SelectSequence(Guid id)
    {
        var sequence = new TimelineQueries(Session.Snapshot).Sequence(id);
        if (id != SequenceId) soloedAudioTracks = ImmutableHashSet<Guid>.Empty;
        SequenceId = id;
        Source = null;
        ProgramPlayhead = Playhead = default;
        SelectedTitle = SelectedClip = SelectedProfile = TargetTrack = null;
        MarkIn = MarkOut = null;
        ProposedCut = null;
        HasAgentProposal = false;
        ProposedTextCut = null;
        Card = null;
        Viewport.Reset();
        Status = $"Sequence: {sequence.Name}";
    }

    public SequenceMarker? AdjacentMarker(bool next)
    {
        var sequence = Sequence;
        long duration = sequence.Duration.Ticks;
        var available = sequence.Markers.Where(marker => marker.At.Ticks <= duration);
        return next
            ? available.Where(marker => marker.At.Ticks > TimelinePlayhead.Ticks).MinBy(marker => marker.At.Ticks)
            : available.Where(marker => marker.At.Ticks < TimelinePlayhead.Ticks).MaxBy(marker => marker.At.Ticks);
    }

    public void New()
    {
        soloedAudioTracks = ImmutableHashSet<Guid>.Empty;
        SelectedProfile = null;
        Waveforms.Clear(); ProposedCut = null; Transcript = null; ProposedTextCut = null; HasAgentProposal = false; TranscriptMode = ControlsMode = false;
        Session = new(ProjectDocument.Create(Guid.NewGuid(), "Untitled")); SequenceId = Guid.NewGuid();
        Commit(SequenceSetup(SequenceId, "Main", new(1280, 720, new(30, 1)), 48000));
        Session = new(Session.Snapshot); // Required sequence/track setup is the baseline.
        SelectedTitle = SelectedClip = null; SelectedAsset = null; TargetTrack = null; FirstAsset = 0; Viewport.Reset(); Playhead = default; MarkIn = null; MarkOut = null; Source = null; SourceMarks.Clear(); SavedDocument = Session.Snapshot.Document; ProjectPath = null;
    }
    public void Open(ProjectSnapshot snapshot, string path)
    {
        SelectedProfile = null;
        Waveforms.Clear(); ProposedCut = null; Transcript = null; ProposedTextCut = null; HasAgentProposal = false; TranscriptMode = ControlsMode = false;
        if (snapshot.Document.Sequences.IsDefaultOrEmpty) throw new InvalidDataException("Project has no sequence. Create a sequence before editing it.");
        soloedAudioTracks = ImmutableHashSet<Guid>.Empty;
        Session = new(snapshot); SequenceId = snapshot.Document.Sequences[0].Id; SavedDocument = snapshot.Document; ProjectPath = path;
        SelectedTitle = SelectedClip = null; SelectedAsset = snapshot.Document.Assets.FirstOrDefault()?.Id; TargetTrack = null; FirstAsset = 0; Viewport.Reset(); Playhead = default; MarkIn = null; MarkOut = null; Source = null; SourceMarks.Clear();
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
        var result = redo ? Session.Redo(request) : Session.Undo(request);
        if (result.Success && !result.Snapshot.Document.Sequences.Any(sequence => sequence.Id == SequenceId))
            SelectSequence(result.Snapshot.Document.Sequences[0].Id);
        Status = result.Message ?? (redo ? "Redo" : "Undo");
        return result;
    }
}

/// <summary>A transient one-asset sequence that the monitor plays in source mode; never part of the project.</summary>
internal sealed record SourceView(Guid AssetId, string Name, ProjectSnapshot Snapshot, Guid SequenceId, SequenceTime Duration, ImportedAsset Imported);

internal enum TitleCardKind { Export, Recording, Proposal }

/// <summary>
/// A title-card moment. Head is the large word; Tail the second line (newline-separated lines for a recording);
/// Body one sentence; Data the edge-code lines; Stats the proposal figures (value, label, accent).
/// </summary>
internal sealed record TitleCard(TitleCardKind Kind, string Head, string Tail, string Body, IReadOnlyList<string> Data,
    IReadOnlyList<(string Value, string Label, bool Accent)>? Stats = null);
