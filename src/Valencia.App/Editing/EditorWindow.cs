using System.Diagnostics;
using System.Runtime.InteropServices;
using Machina.Runtime.Input;
using Silk.NET.Input;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Media;
using Valencia.Persistence;
using Valencia.Rendering;
using PointerPoint = Machina.Runtime.Input.PointerPoint;

namespace Valencia.App.Editing;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed partial class EditorWindow : IDisposable
{
    public EditorDocument Editor { get; } = new();
    public NativeVideoPresenter Graphics { get; } = new() { EditorLayout = true };
    private readonly IInputContext input;
    private readonly EditorKeymap keys = new();
    private readonly EditorView view = new();
    private readonly UiPointerActivation pointer = new();
    public TimelineGesture Gesture { get; }
    private readonly ProjectFile files = new();
    private readonly Valencia.Scripting.CopelandScriptHost scriptHost = new();
    private readonly Dictionary<Guid, ImportedAsset> imports = [];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly EditorSynchronizationContext context = new();
    private readonly SynchronizationContext? previousContext = SynchronizationContext.Current;
    private CancellationTokenSource? seekCancellation, jobCancellation;
    private Task<SequencePlayback?>? opening;
    private Task? job;
    public SequencePlayback? Playback { get; private set; }
    private long generation, openingGeneration;
    private bool desired, resume, razor, scrubbing, focused = true;
    private (double X, double Y)? pointerPosition;
    private double lastUi = -1, lastTick;
    private readonly Stopwatch seekLatency = new();
    public List<double> SeekLatenciesMs { get; } = [];
    private readonly EditorSmoke? smoke;
    private readonly AnalysisSmoke? analysisSmoke;
    private readonly TranscriptSmoke? transcriptSmoke;
    private readonly CreatorSmoke? creatorSmoke;
    private readonly QualificationSmoke? qualificationSmoke;
    private readonly LayoutSmoke? layoutSmoke;
    public long StaleGenerationsRejected { get; private set; }
    public long SeekRequests { get; private set; }
    public bool Busy => job is not null || opening is not null || desired;
    public double MaximumAvSkew { get; private set; }
    public List<double> FrameMilliseconds { get; } = [];
    public EditorWindow(string? path = null, string? smokeDirectory = null, bool analyzeSmoke = false, bool transcribeSmoke = false, bool createSmoke = false, bool qualifySmoke = false, bool resizeSmoke = false)
    {
        SynchronizationContext.SetSynchronizationContext(context);
        Gesture = new(Editor); input = Graphics.Window.CreateInput(); Graphics.Window.Title = "Valencia — timeline editor";
        foreach (var keyboard in input.Keyboards) { keyboard.KeyDown += (_, k, _) => { keys.Record(k, true); Editor.Card = null; }; keyboard.KeyUp += (_, k, _) => keys.Record(k, false); }
        foreach (var mouse in input.Mice)
        {
            mouse.MouseDown += MouseDown; mouse.MouseUp += MouseUp; mouse.MouseMove += MouseMove;
            mouse.Scroll += (m, wheel) =>
            {
                var point = Point(m); var media = view.Layout.Media;
                if (point.X >= media.X && point.X < media.X + media.Width && point.Y >= media.Y && point.Y < media.Y + media.Height)
                    Editor.FirstAsset = Math.Clamp(Editor.FirstAsset - (int)wheel.Y, 0, Math.Max(0, Editor.Session.Snapshot.Document.Assets.Length - Editor.MediaPageSize));
                else if (keys.Control) Editor.Viewport.Zoom(Math.Pow(1.2, wheel.Y), Point(m).X);
                else if (keys.Shift) Editor.Viewport.FirstTrack = Math.Clamp(Editor.Viewport.FirstTrack - (int)wheel.Y, 0, Math.Max(0, Editor.Sequence.Tracks.Length - Editor.Viewport.VisibleTracks));
                else Editor.Viewport.Scroll(-wheel.Y * 90 - wheel.X * 90);
                lastUi = -1;
            };
        }
        Graphics.Window.FocusChanged += value => { focused = value; if (!value) CancelGesture(); keys.Reset(); };
        Graphics.Window.Resize += _ => { CancelGesture(); pointerPosition = null; Prepare(); };
        Graphics.Window.Closing += () => { if (smoke is null && analysisSmoke is null && transcriptSmoke is null && creatorSmoke is null && qualificationSmoke is null && layoutSmoke is null && !DiscardAllowed()) { Graphics.Window.IsClosing = false; return; } jobCancellation?.Cancel(); seekCancellation?.Cancel(); Gesture.Cancel(); };
        if (smokeDirectory is not null)
        {
            if (resizeSmoke) layoutSmoke = new(smokeDirectory);
            else if (qualifySmoke) qualificationSmoke = new(smokeDirectory);
            else if (createSmoke) creatorSmoke = new(smokeDirectory);
            else if (transcribeSmoke) transcriptSmoke = new(smokeDirectory);
            else if (analyzeSmoke) analysisSmoke = new(smokeDirectory);
            else smoke = new(smokeDirectory);
        }
        if (path is not null) RunJob(async () =>
        {
            if (Path.GetExtension(path).Equals(".valencia", StringComparison.OrdinalIgnoreCase)) await OpenProject(path);
            else { await Import(path); InsertSelected(); }
        });
    }
    public void Run()
    {
        while (!Graphics.Window.IsClosing)
        {
            var frameClock = Stopwatch.StartNew(); Graphics.Window.DoEvents(); context.Pump();
            double now = clock.Elapsed.TotalSeconds;
            if (focused) foreach (var action in keys.Read(now - lastTick)) Action(action.ToString()); lastTick = now;
            if (Gesture.Active && GetCapture() == 0) CancelGesture();
            if (job?.IsCompleted == true) { try { job.GetAwaiter().GetResult(); } catch (Exception e) { Editor.Status = "ERROR: " + e.Message; Console.Error.WriteLine(e); } job = null; lastUi = -1; }
            CompleteSeek();
            if (desired && opening is null) BeginSeek();
            if (!desired && opening is null && Playback is { } playback)
            {
                if (playback.Failure is { } error) { playback.Pause(); Editor.Status = "ERROR: " + error.Message; }
                long selectedAt = Stopwatch.GetTimestamp();
                if (playback.Update() && playback.Current is { } frame)
                {
                    Graphics.RecordMediaFrame(new { frame.Time, frame.SourceTimestamps, frame.ReadyHostTicks,
                        frame.EvaluationMilliseconds, selectedHostTicks = Stopwatch.GetTimestamp(), audioClock = playback.Position });
                    Graphics.UpdateVideo(frame.Width, frame.Height, frame.Rgba);
                }
                Graphics.RecordCpuPhase("frameSelectionAndUploadMs", Stopwatch.GetElapsedTime(selectedAt).TotalMilliseconds);
                Editor.Playhead = playback.Position;
                if (playback.IsPlaying && playback.Current is { } presented) MaximumAvSkew = Math.Max(MaximumAvSkew, Math.Abs(Editor.Playhead.TotalSeconds - presented.Time.TotalSeconds));
            }
            smoke?.Tick(this);
            analysisSmoke?.Tick(this);
            transcriptSmoke?.Tick(this);
            creatorSmoke?.Tick(this);
            qualificationSmoke?.Tick(this);
            layoutSmoke?.Tick(this);
            if (now - lastUi >= .1) Prepare();
            Graphics.Draw();
            if (!desired && opening is null && Playback is { IsPlaying: true, Current: { } presentedFrame } presentedPlayback)
            {
                double observedClock = presentedPlayback.Position.TotalSeconds;
                Graphics.ObservePresentationClock(presentedFrame.Time.TotalSeconds, observedClock);
                MaximumAvSkew = Math.Max(MaximumAvSkew, Math.Abs(observedClock - presentedFrame.Time.TotalSeconds));
            }
            if (FrameMilliseconds.Count < 10000) FrameMilliseconds.Add(frameClock.Elapsed.TotalMilliseconds);
            if (smoke?.Finished == true || analysisSmoke?.Finished == true || transcriptSmoke?.Finished == true || creatorSmoke?.Finished == true || qualificationSmoke?.Finished == true || layoutSmoke?.Finished == true) return;
            Thread.Sleep(1);
        }
        if (smoke is not null || analysisSmoke is not null || transcriptSmoke is not null || creatorSmoke is not null || qualificationSmoke is not null || layoutSmoke is not null) throw new InvalidOperationException("Editor smoke closed before completing its assertions.");
    }
    private void Prepare()
    {
        long preparedAt = Stopwatch.GetTimestamp();
        Editor.ObsConnection = Obs?.Recording.Client.Status.State.ToString() ?? "Disconnected";
        Editor.ObsRecording = Obs is { } obs ? obs.Recording.Snapshot.State + " / " + obs.Recording.Snapshot.Markers.Length + " markers" : "No recording session";
        Editor.Recording = Obs?.Recording.Snapshot.State == "recording";
        if (Editor.Card is { } card && (clock.Elapsed.TotalSeconds > Editor.CardUntil
            || card.Kind == TitleCardKind.Proposal && Editor.ProposedCut is null && Editor.ProposedTextCut is null)) Editor.Card = null;
        var prepared = view.Prepare(Editor, Gesture, Playback?.IsPlaying == true, razor,
            Math.Max(1, Graphics.Window.Size.X), Math.Max(1, Graphics.Window.Size.Y), job is not null);
        Graphics.ProgramMonitor = view.HideVideo ? new(0, 0, 0, 0) : view.Layout.Monitor;
        Graphics.RecordCpuPhase("layoutAndPresentationBuildMs", Stopwatch.GetElapsedTime(preparedAt).TotalMilliseconds);
        long realizedAt = Stopwatch.GetTimestamp();
        Graphics.UpdateUi(prepared.PresentationFrame); Graphics.Canvas = view.Canvas; lastUi = clock.Elapsed.TotalSeconds;
        Graphics.RecordCpuPhase("nativeUiPreparationMs", Stopwatch.GetElapsedTime(realizedAt).TotalMilliseconds);
        Graphics.Window.Title = $"Valencia — {(Editor.Dirty ? "* " : "")}{Path.GetFileName(Editor.ProjectPath ?? "Untitled.valencia")}";
    }
    internal int CanvasCount => view.Canvas.Count;
    /// <summary>Title-card moments last 900 ms; a sticky card (a pending proposal) stays until a key, Apply or Cancel.</summary>
    internal void ShowCard(TitleCard card, bool sticky = false)
    {
        Editor.Card = card; Editor.CardUntil = sticky ? double.MaxValue : clock.Elapsed.TotalSeconds + .9; lastUi = -1;
    }
    internal void ShowProposal(string title, IReadOnlyList<Valencia.Engine.TimelineRemoval> removed, double resultingSeconds)
    {
        double seconds = removed.Sum(r => (r.End.Ticks - r.Start.Ticks) / (double)SequenceTime.TicksPerSecond);
        ShowCard(new(TitleCardKind.Proposal, title.ToUpperInvariant(), "", "", ["ONE UNDO STEP / RED RANGES ON TIMELINE"],
            [(removed.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "REMOVALS", false),
             ("-" + Timecode.Duration(seconds), "REMOVED", true), (Timecode.Duration(Math.Max(0, resultingSeconds)), "RESULTING LENGTH", false)]), sticky: true);
    }
    internal Task ExportQualification(string path)
    {
        Editor.Status = "Export starting";
        return SequenceExport.RunAsync(Editor.Session.Snapshot, Editor.SequenceId, path,
            new Progress<ExportProgress>(p => Editor.Status = $"Export {p.Frames}/{p.TotalFrames}"));
    }
    internal double MeasureWaveformFrame()
    {
        var timer = Stopwatch.StartNew();
        Prepare();
        Graphics.Draw();
        return timer.Elapsed.TotalMilliseconds;
    }
    public void Capture(string path)
    {
        Prepare(); var rgba = Graphics.Draw(true) ?? throw new InvalidOperationException("No GPU readback.");
        using var bitmap = new Bitmap(Graphics.Window.FramebufferSize.X, Graphics.Window.FramebufferSize.Y, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { Marshal.Copy(rgba, 0, data.Scan0, rgba.Length); } finally { bitmap.UnlockBits(data); }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
    public void Seek(SequenceTime position, bool play = false)
    {
        Editor.Playhead = new(Math.Clamp(position.Ticks, 0, Editor.MonitorDuration.Ticks));
        resume = play; generation++; SeekRequests++; desired = true;
        seekLatency.Restart();
        Playback?.Pause(); seekCancellation?.Cancel(); Graphics.ClearVideo(); lastUi = -1;
    }
    private void BeginSeek()
    {
        desired = false; openingGeneration = generation; var old = Playback; Playback = null;
        var previousCancellation = seekCancellation; seekCancellation = new(); var token = seekCancellation.Token;
        var snapshot = Editor.Source?.Snapshot ?? Editor.Session.Snapshot; var id = Editor.Source?.SequenceId ?? Editor.SequenceId; var position = Editor.Playhead;
        var profile = Editor.Source is null ? Editor.Sequence.Creator?.Profiles.FirstOrDefault(p => p.Id == Editor.SelectedProfile) : null;
        opening = Task.Run(async () =>
        {
            if (old is not null) await old.DisposeAsync(); previousCancellation?.Dispose();
            if (new TimelineQueries(snapshot).Duration(id).Ticks == 0) return null;
            return await SequencePlayback.OpenAsync(snapshot, id, position, token, profile);
        });
    }
    private void CompleteSeek()
    {
        if (opening?.IsCompleted != true) return;
        try
        {
            var opened = opening.GetAwaiter().GetResult();
            if (openingGeneration != generation)
            {
                StaleGenerationsRejected++; if (opened is not null) opened.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            else { Playback = opened; if (opened?.Current is { } frame) { Graphics.UpdateVideo(frame.Width, frame.Height, frame.Rgba); if (SeekLatenciesMs.Count < 1000) SeekLatenciesMs.Add(seekLatency.Elapsed.TotalMilliseconds); } if (resume) opened?.Play(); }
        }
        catch (OperationCanceledException) when (openingGeneration != generation) { StaleGenerationsRejected++; }
        catch (Exception error) { Editor.Status = "Preview error: " + error.Message; Console.Error.WriteLine(error); }
        opening = null;
    }
    public async Task Import(string path)
    {
        var imported = await Task.Run(() => MediaAssetProbe.Import(Guid.NewGuid(), path));
        // Continuations of UI jobs run on this window's synchronization context.
        Editor.Register(imported); imports[imported.Asset.Id] = imported; Editor.Status = "Imported " + imported.Asset.Name;
    }
    private enum InsertMode { Place, Insert, Overwrite }
    /// <summary>Place keeps the original overlap-rejecting Insert button; Insert/Overwrite are the Premiere comma/period edits.</summary>
    private void InsertSelected(InsertMode mode = InsertMode.Place)
    {
        if (Editor.SelectedAsset is not { } id) return;
        if (!imports.TryGetValue(id, out var imported))
        {
            var asset = Editor.Session.Snapshot.Document.Assets.First(a => a.Id == id);
            RunJob(async () => { imports[id] = await Task.Run(() => MediaAssetProbe.Import(id, asset.SourcePath)); InsertSelected(mode); }); return;
        }
        var result = mode == InsertMode.Place ? Editor.Insert(imported) : EditorEdits.InsertAt(Editor, imported, mode == InsertMode.Overwrite);
        if (result.Success) Seek(Editor.Playhead);
    }
    public async Task OpenProject(string path)
    {
        var candidate = await files.LoadAsync(path, jobCancellation?.Token ?? default);
        Editor.Open(candidate.Snapshot, Path.GetFullPath(path)); imports.Clear(); Seek(default);
        Editor.Status = candidate.MissingMedia.Length == 0 ? "Project opened." : $"Missing media: {candidate.MissingMedia.Length}. Select media and Relink.";
    }
    public async Task SaveProject(string path)
    {
        var saved = Editor.Session.Snapshot; await files.SaveAsync(path, saved, jobCancellation?.Token ?? default);
        Editor.ProjectPath = Path.GetFullPath(path); Editor.SavedDocument = saved.Document; Editor.Status = "Project saved.";
    }
    private void RunJob(Func<Task> action)
    {
        if (job is not null) { Editor.Status = "A job is running. Cancel or wait."; return; }
        jobCancellation?.Dispose(); jobCancellation = new(); job = action();
    }
    private string? Pick(bool save, string filter, string title)
    {
        Playback?.Pause(); CancelGesture(); keys.Reset();
        using FileDialog dialog = save ? new SaveFileDialog() : new OpenFileDialog(); dialog.Filter = filter; dialog.Title = title;
        string? result = dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null; keys.Reset(); return result;
    }
    private bool DiscardAllowed() => !Editor.Dirty || MessageBox.Show("Discard unsaved project edits?", "Valencia", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    internal async Task RunTrustedScript(string scriptPath)
    {
        Playback?.Pause();
        CancelGesture();
        await using var engine = new Valencia.Engine.EditingEngine(Editor.Session, projectPath: Editor.ProjectPath);
        var result = await scriptHost.RunFileAsync(scriptPath, engine,
            cancellation: jobCancellation?.Token ?? default, allowProjectReplacement: false);
        if (engine.SavedDocument is { } saved)
        {
            Editor.SavedDocument = saved;
            Editor.ProjectPath = engine.ProjectPath;
        }
        Seek(Editor.Playhead);
        Editor.Status = result.Success ? "Script completed." : "Script failed: " + string.Join("; ", result.Diagnostics.Select(d => d.Message));
    }
    public void Action(string name)
    {
        try
        {
            if (name == "Cancel") { CancelGesture(); jobCancellation?.Cancel(); Editor.ProposedCut = null; Editor.ProposedTextCut = null; return; }
            if (job is not null) return;
            // Timeline edits act on the program playhead; leave source mode first (as clicking the timeline panel does in Premiere).
            if (Editor.Source is not null && name is "Split" or "Delete" or "RippleDelete" or "RippleTrimPrevious" or "RippleTrimNext" or "Lift" or "Extract"
                or "PreviousEdit" or "NextEdit" or "Undo" or "Redo" or "Insert" or "RazorTool" or "ZoomFit") ExitSource();
            switch (name)
            {
                case "ObsMode": Editor.ObsMode = !Editor.ObsMode; break;
                case "ObsConnect": RunJob(ConnectObs); break;
                case "ObsStart": RunJob(() => ObsAction("start")); break;
                case "ObsStop": RunJob(() => ObsAction("stop")); break;
                case "ObsPause": RunJob(() => ObsAction("pause")); break;
                case "ObsMarker": RunJob(() => ObsAction("marker")); break;
                case "ObsImport": RunJob(() => ObsAction("import")); break;
                case "OutputProfile":
                    var profiles = Editor.Sequence.Creator?.Profiles ?? [];
                    int selected = Array.FindIndex(profiles.ToArray(), p => p.Id == Editor.SelectedProfile);
                    Editor.SelectedProfile = selected + 1 < profiles.Length ? profiles[selected + 1].Id : null;
                    Seek(Editor.Playhead); break;
                case "Transcribe": RunJob(TranscribeSelected); break;
                case "TranscriptMode": Editor.TranscriptMode = !Editor.TranscriptMode; break;
                case "TranscriptPrev": Editor.TranscriptFirstWord = Math.Max(0, Editor.TranscriptFirstWord - Editor.TranscriptPageSize); break;
                case "TranscriptNext": Editor.TranscriptFirstWord = Math.Min(Math.Max(0, (Editor.Transcript?.Transcript?.Words.Length ?? 0) - 1), Editor.TranscriptFirstWord + Editor.TranscriptPageSize); break;
                case "TranscriptFind": FindTranscript(); break;
                case "TextRetain": RunJob(() => ProposeTextCut("retain")); break;
                case "TextRemove": RunJob(() => ProposeTextCut("remove")); break;
                case "ApplyTextCut": RunJob(ApplyTextCut); break;
                case "CancelTextCut": Editor.ProposedTextCut = null; Editor.Status = "Text edit cancelled."; break;
                case "Analyze": RunJob(AnalyzeSelected); break;
                case "RoughCut": RunJob(ProposeRoughCut); break;
                case "ApplyCut": RunJob(ApplyRoughCut); break;
                case "CancelCut": Editor.ProposedCut = null; Editor.Status = "Rough cut cancelled; no edits applied."; break;
                case "RunScript":
                    if (Pick(false, "Trusted Copeland script|*.ts", "Run trusted local script (full CLR privileges)") is { } scriptPath)
                    {
                        RunJob(() => RunTrustedScript(scriptPath));
                    }
                    break;
                case "Import":
                    if (Pick(false, "Media|*.mp4;*.mkv;*.mov;*.wav;*.mp3;*.webm|All files|*.*", "Import media") is { } media) RunJob(() => Import(media)); break;
                case "Insert": InsertSelected(); break;
                case "New": if (DiscardAllowed()) { Editor.New(); imports.Clear(); Seek(default); } break;
                case "Open": if (DiscardAllowed() && Pick(false, "Valencia project|*.valencia", "Open project") is { } path) RunJob(() => OpenProject(path)); break;
                case "Save": case "SaveAs":
                    string? save = name == "Save" ? Editor.ProjectPath : null;
                    save ??= Pick(true, "Valencia project|*.valencia", "Save project");
                    if (save is not null) RunJob(() => SaveProject(save)); break;
                case "Export":
                    if (Pick(true, "MP4 video|*.mp4", "Export edited sequence") is { } output)
                    {
                        var snapshot = Editor.Session.Snapshot; var id = Editor.SequenceId;
                        RunJob(async () => { await SequenceExport.RunAsync(snapshot, id, output, new Progress<ExportProgress>(p => Editor.Status = $"Export {p.Frames}/{p.TotalFrames}"), jobCancellation!.Token); Editor.Status = "Export complete: " + output;
                            long bytes = File.Exists(output) ? new FileInfo(output).Length : 0;
                            ShowCard(new(TitleCardKind.Export, "EXPORT", "COMPLETE.", "Written to " + Path.GetFileName(output) + ".",
                                [$"{Path.GetFileName(output).ToUpperInvariant()} / {Timecode.Format(snapshot.Document.Sequences.First(s => s.Id == id).Duration, Editor.Sequence.Video.FrameRate)} / {bytes / 1048576.0:0.#} MB"])); });
                    }
                    break;
                case "Relink":
                    if (Editor.SelectedAsset is { } assetId && Pick(false, "Media|*.*", "Relink selected media") is { } replacement)
                        RunJob(async () => { var imported = await Task.Run(() => MediaAssetProbe.Import(assetId, replacement)); if (Editor.Commit(new RelinkAsset(imported.Asset)).Success) {
                            imports[assetId] = imported;
                            foreach (var key in Editor.Waveforms.Keys.Where(k => k.Asset == assetId).ToArray()) Editor.Waveforms.Remove(key);
                            Editor.ProposedCut = null; Seek(Editor.Playhead);
                            if (Editor.Transcript?.AssetId == assetId) { Editor.Transcript = null; Editor.ProposedTextCut = null; }
                        } });
                    break;
                case "TogglePlay": if (Playback?.IsPlaying == true) Playback.Pause(); else StartPlaying(); break;
                case "Pause": Playback?.Pause(); break;
                case "Play": StartPlaying(); break;
                case "ReverseUnsupported": Playback?.Pause(); Editor.Status = "J: reverse/shuttle playback is not implemented; playback paused."; break;
                case "MarkIn" or "MarkOut" or "ClearMarks" when Editor.Source is { } markSource:
                {
                    var current = Editor.SourceMarks.GetValueOrDefault(markSource.AssetId);
                    current = name switch
                    {
                        "MarkIn" => (Editor.Playhead, current.Out is { } o && o.Ticks <= Editor.Playhead.Ticks ? null : current.Out),
                        "MarkOut" => (current.In is { } i && i.Ticks >= Editor.Playhead.Ticks ? null : current.In, Editor.Playhead),
                        _ => (null, null)
                    };
                    Editor.SourceMarks[markSource.AssetId] = current; Editor.Status = $"Source {name}"; break;
                }
                case "MarkIn": Editor.MarkIn = Editor.Playhead; if (Editor.MarkOut is { } markOut && markOut.Ticks <= Editor.Playhead.Ticks) Editor.MarkOut = null; Editor.Status = $"In {Editor.Playhead.TotalSeconds:0.000}s"; break;
                case "MarkOut": Editor.MarkOut = Editor.Playhead; if (Editor.MarkIn is { } markIn && markIn.Ticks >= Editor.Playhead.Ticks) Editor.MarkIn = null; Editor.Status = $"Out {Editor.Playhead.TotalSeconds:0.000}s"; break;
                case "ClearMarks": Editor.MarkIn = null; Editor.MarkOut = null; Editor.Status = "Cleared In/Out."; break;
                case "PreviousEdit": if (EditorEdits.PreviousEdit(Editor.Sequence, Editor.Playhead) is { } previousEdit) Seek(previousEdit); break;
                case "NextEdit": if (EditorEdits.NextEdit(Editor.Sequence, Editor.Playhead) is { } nextEdit) Seek(nextEdit); break;
                case "GoToStart": Seek(default); break;
                case "GoToEnd": Seek(Editor.MonitorDuration); break;
                case "RippleTrimPrevious": case "RippleTrimNext": Playback?.Pause(); if (EditorEdits.RippleTrim(Editor, name == "RippleTrimPrevious")?.Success == true) Seek(Editor.Playhead); break;
                case "Lift": case "Extract": Playback?.Pause(); if (EditorEdits.RemoveMarked(Editor, name == "Extract")?.Success == true) Seek(Editor.Playhead); break;
                case "InsertEdit": case "OverwriteEdit":
                    Playback?.Pause();
                    if (Editor.Source is { } insertSource)
                    {
                        var range = Editor.SourceMarks.GetValueOrDefault(insertSource.AssetId);
                        var inserted = EditorEdits.InsertAt(Editor, insertSource.Imported, name == "OverwriteEdit", (range.In ?? default, range.Out ?? insertSource.Duration));
                        if (!inserted.Success) Editor.Status = inserted.Message ?? inserted.Error.ToString();
                    }
                    else InsertSelected(name == "InsertEdit" ? InsertMode.Insert : InsertMode.Overwrite);
                    break;
                case "MonitorSource": OpenSelectedSource(); break;
                case "MonitorProgram": ExitSource(); break;
                case "ToggleSnap": Editor.Snapping = !Editor.Snapping; Editor.Status = Editor.Snapping ? "Snapping on." : "Snapping off."; break;
                case "ToggleLink": Editor.LinkedSelection = !Editor.LinkedSelection; Editor.Status = Editor.LinkedSelection ? "Linked selection on: audio and video move together." : "Linked selection off: clips edit independently."; break;
                case "ToggleMagnet": Editor.Magnetic = !Editor.Magnetic; Editor.Status = Editor.Magnetic ? "Magnetic timeline on: deletes ripple, same-track moves reorder, overwrite inserts." : "Magnetic timeline off."; break;
                case "Tab:Media": Editor.TranscriptMode = false; Editor.ObsMode = false; break;
                case "Tab:Transcript": Editor.TranscriptMode = true; Editor.ObsMode = false; break;
                case "Tab:Record": Editor.ObsMode = true; Editor.TranscriptMode = false; break;
                case "ZoomFit":
                {
                    int firstTrack = Editor.Viewport.FirstTrack; double span = Math.Max(1, Editor.Sequence.Duration.TotalSeconds);
                    Editor.Viewport.Reset(); Editor.Viewport.FirstTrack = firstTrack;
                    Editor.Viewport.Zoom((Editor.Viewport.ClipRight - Editor.Viewport.ClipLeft - 16) / (span * Editor.Viewport.PixelsPerSecond), Editor.Viewport.ClipLeft);
                    break;
                }
                case "SelectTool": razor = false; break;
                case "RazorTool": razor = true; break;
                case "Split": if (Editor.Split()?.Success == true) Seek(Editor.Playhead); break;
                case "Delete": case "RippleDelete": if (Editor.Delete(name == "RippleDelete")?.Success == true) Seek(Editor.Playhead); break;
                case "Undo": case "Redo": if (Editor.History(name == "Redo").Success) Seek(Editor.Playhead); break;
                case "ZoomIn": case "ZoomOut": Editor.Viewport.Zoom(name == "ZoomIn" ? 1.4 : 1 / 1.4, Math.Clamp(Editor.Viewport.X(Editor.Playhead), 166, 1256)); break;
                case "PreviousFrame": case "NextFrame":
                    long index = TimelineTime.FrameIndex(Editor.Playhead, Editor.Sequence.Video.FrameRate, TimeRounding.NearestAwayFromZero);
                    Seek(TimelineTime.FromFrames(Math.Max(0, index + (name == "NextFrame" ? 1 : -1)), Editor.Sequence.Video.FrameRate)); break;
                case "AddVideo": case "AddAudio":
                    Editor.Commit(new AddTrack(Editor.SequenceId, Guid.NewGuid(), name == "AddVideo" ? TrackKind.Video : TrackKind.Audio, Editor.Sequence.Tracks.Length, name == "AddVideo" ? "Video" : "Audio")); break;
                default:
                    if (name.StartsWith("TranscriptWord:", StringComparison.Ordinal)) { SelectTranscriptWord(int.Parse(name[15..])); break; }
                    if (name.StartsWith("Asset:", StringComparison.Ordinal)) { Editor.SelectedAsset = Guid.Parse(name[6..]); if (Editor.Source is not null) OpenSelectedSource(); }
                    else if (name.StartsWith("Target:", StringComparison.Ordinal)) Editor.TargetTrack = Guid.Parse(name[7..]);
                    else if (name.StartsWith("Lock:", StringComparison.Ordinal) || name.StartsWith("State:", StringComparison.Ordinal))
                    {
                        bool locked = name.StartsWith("Lock:", StringComparison.Ordinal); Guid trackId = Guid.Parse(name[(locked ? 5 : 6)..]);
                        var track = Editor.Sequence.Tracks.First(t => t.Id == trackId);
                        var state = locked ? track.State with { Locked = !track.State.Locked } : track.Kind == TrackKind.Audio ? track.State with { Muted = !track.State.Muted } : track.State with { Visible = !track.State.Visible };
                        if (Editor.Commit(new SetTrackState(Editor.SequenceId, trackId, state)).Success) Seek(Editor.Playhead);
                    }
                    break;
            }
        }
        catch (Exception e) { Editor.Status = "ERROR: " + e.Message; Console.Error.WriteLine(e); }
        lastUi = -1;
    }
    private void StartPlaying() { if (opening is not null || desired) resume = true; else if (Editor.Playhead.Ticks >= Editor.MonitorDuration.Ticks) Seek(default, true); else if (Playback is null) Seek(Editor.Playhead, true); else Playback.Play(); }
    private static (double X, double Y) Point(IMouse mouse) => (mouse.Position.X, mouse.Position.Y);
    private void MouseDown(IMouse mouse, MouseButton button)
    {
        if (button != MouseButton.Left || job is not null) return;
        var p = pointerPosition ?? Point(mouse);
        var viewport = Editor.Viewport;
        if (p.X >= viewport.ClipLeft && p.X <= viewport.ClipRight && p.Y >= view.Layout.RulerY && p.Y < viewport.TrackTop + viewport.VisibleTracks * viewport.Row)
        {
            Playback?.Pause(); ExitSource();
            if (Editor.Viewport.Hit(Editor.Sequence, p.X, p.Y) is { } hit && p.Y >= viewport.TrackTop)
            {
                Editor.SelectedClip = hit.Clip.Id;
                if (razor) { Seek(Snap(p.X)); Editor.Split(); Seek(Editor.Playhead); }
                else { Gesture.Begin(hit.Clip, hit.Edge, p.X); SetCapture(Graphics.Window.Native!.DXHandle!.Value); }
            }
            else { Editor.SelectedClip = null; scrubbing = true; SetCapture(Graphics.Window.Native!.DXHandle!.Value); Seek(Snap(p.X)); }
            lastUi = -1; return;
        }
        pointer.Press(view.Prepared?.HitTest.HitTest(new PointerPoint(p.X, p.Y)));
    }
    private SequenceTime Snap(double x)
    {
        var time = Editor.Viewport.Time(x);
        if (Editor.Snapping)
        {
            long threshold = (long)(TimelineGesture.SnapPixels / Editor.Viewport.PixelsPerSecond * SequenceTime.TicksPerSecond);
            var points = EditorEdits.EditPoints(Editor.Sequence).Concat(new[] { Editor.MarkIn?.Ticks, Editor.MarkOut?.Ticks }.OfType<long>());
            long nearest = points.OrderBy(p => Math.Abs(p - time.Ticks)).DefaultIfEmpty(-1).First();
            if (nearest >= 0 && Math.Abs(nearest - time.Ticks) <= threshold) return new(nearest);
        }
        return TimelineTime.SnapToFrame(time, Editor.Sequence.Video.FrameRate, TimeRounding.NearestAwayFromZero);
    }
    private void OpenSelectedSource()
    {
        if (Editor.SelectedAsset is not { } id) { Editor.Status = "Select media in the Media tab to view it in the source monitor."; return; }
        if (!imports.TryGetValue(id, out var imported))
        {
            var asset = Editor.Session.Snapshot.Document.Assets.First(a => a.Id == id);
            RunJob(async () => { imports[id] = await Task.Run(() => MediaAssetProbe.Import(id, asset.SourcePath)); OpenSelectedSource(); }); return;
        }
        // A transient one-asset sequence built with the same placement rules as Insert; it never enters the project.
        var sequence = Editor.Sequence; var sourceId = Guid.NewGuid();
        var document = Editor.Session.Snapshot.Document with { Sequences = [new SequenceDocument(sourceId, "Source", sequence.Video, sequence.AudioSampleRate, [])] };
        var session = new EditingSession(new ProjectSnapshot(document, 0));
        var operations = Valencia.Engine.InsertionCommands.Build(session.Snapshot, sourceId, imported, default);
        var built = session.Execute(new EditTransaction(Guid.NewGuid(), 1, 0, [.. operations.Select(o => new EditCommand(Guid.NewGuid(), 1, o))]));
        if (!built.Success) { Editor.Status = "Cannot open source: " + (built.Message ?? built.Error.ToString()); return; }
        if (Editor.Source is null) Editor.ProgramPlayhead = Editor.Playhead;
        Editor.Source = new(id, imported.Asset.Name, built.Snapshot, sourceId, new TimelineQueries(built.Snapshot).Duration(sourceId), imported);
        Editor.Status = "Source: " + imported.Asset.Name + ". I/O mark, , insert, . overwrite at the timeline playhead.";
        // Start on the first picture: recordings often begin audio a few ms before video (OBS AAC starts at -21 ms).
        var firstVideo = new TimelineQueries(built.Snapshot).Sequence(sourceId).Tracks.Where(t => t.Kind == TrackKind.Video).SelectMany(t => t.Clips).Select(c => c.Start).DefaultIfEmpty().Min(t => t.Ticks);
        Seek(Editor.SourceMarks.GetValueOrDefault(id).In ?? new SequenceTime(firstVideo));
    }
    private void ExitSource()
    {
        if (Editor.Source is null) return;
        Editor.Source = null; Seek(Editor.ProgramPlayhead);
    }

    private void MouseMove(IMouse mouse, System.Numerics.Vector2 position)
    {
        var p = (X: (double)position.X, Y: (double)position.Y);
        pointerPosition = p;
        if (Gesture.Active) { Gesture.Move(p.X, p.Y); lastUi = -1; } else if (scrubbing) Seek(Snap(p.X));
    }
    private void MouseUp(IMouse mouse, MouseButton button)
    {
        if (button != MouseButton.Left) return;
        if (Gesture.Active) { var p = pointerPosition ?? Point(mouse); Gesture.Move(p.X, p.Y); if (Gesture.Release()) Seek(Editor.Playhead); ReleaseCapture(); }
        else if (scrubbing) { scrubbing = false; ReleaseCapture(); }
        else { var p = Point(mouse); if (pointer.Release(view.Prepared?.HitTest.HitTest(new PointerPoint(p.X, p.Y)))?.Name is { } name) Action(name); }
        lastUi = -1;
    }
    private void CancelGesture() { Gesture.Cancel(); scrubbing = false; pointer.Reset(); ReleaseCapture(); lastUi = -1; }
    [DllImport("user32.dll")] private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint GetCapture();
    public void Dispose()
    {
        jobCancellation?.Cancel(); seekCancellation?.Cancel();
        while (job is { IsCompleted: false }) { context.Pump(); Thread.Sleep(1); }
        if (job is not null) try { job.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } catch (Exception e) { Console.Error.WriteLine(e); }
        if (opening is not null) try { opening.GetAwaiter().GetResult()?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch (OperationCanceledException) { } catch (Exception e) { Console.Error.WriteLine(e); }
        Playback?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        qualificationSmoke?.Dispose();
        obsEngine?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        scriptHost.Dispose(); input.Dispose(); Graphics.Dispose(); seekCancellation?.Dispose(); jobCancellation?.Dispose();
        SynchronizationContext.SetSynchronizationContext(previousContext);
    }
}
