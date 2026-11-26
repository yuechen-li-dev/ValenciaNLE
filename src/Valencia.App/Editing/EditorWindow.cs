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
using Aurelian.Machina;
using Machina.Core.Semantics;
using Machina.Core.Styling;
using Machina.Pipeline;

namespace Valencia.App.Editing;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed partial class EditorWindow : IDisposable
{
    public EditorDocument Editor { get; } = new();
    public NativeVideoPresenter Graphics { get; } = new() { EditorLayout = true };
    private readonly IInputContext input;
    private readonly EditorKeymap keys = EditorKeymap.ForUser();
    private readonly EditorView view = new();
    private readonly UiPointerActivation pointer = new();
    private readonly UiPointerFeedback feedback = new();
    private readonly AurelianCursor cursor;
    private readonly AurelianWidgetInput widgetInput;
    internal SettingsPage Settings { get; }
    private AurelianWindowChrome chrome;
    private long chromeRevision;
    private bool feedbackDirty;
    private System.Numerics.Vector2? observedNativePointer;
    public TimelineGesture Gesture { get; }
    private readonly ProjectFile files = new();
    private readonly Valencia.Scripting.CopelandScriptHost scriptHost = new();
    private readonly Dictionary<Guid, ImportedAsset> imports = [];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly EditorSynchronizationContext context = new();
    private readonly EditorDiagnostics diagnostics;
    private readonly SynchronizationContext? previousContext = SynchronizationContext.Current;
    private CancellationTokenSource? seekCancellation, jobCancellation;
    private Task<SequencePlayback?>? opening;
    private readonly SequencePreview pausedPreview = new(); // touched only by the serialized opening worker
    private Task<Result<Completed, EditorFailure>>? job;
    public SequencePlayback? Playback { get; private set; }
    private long generation, openingGeneration;
    private bool desired, resume, razor, scrubbing, focused = true;
    private bool reverseRequested;
    private long[]? scrubSnapPoints;
    private readonly List<CanvasRectangle> playheadOverlay = new(16);
    private SequenceTime? pendingScrub;
    private double lastUi = -1, lastTick;
    private string? lastReportedFailure;
    private readonly Stopwatch seekLatency = new();
    public List<double> SeekLatenciesMs { get; } = [];
    private readonly EditorSmoke? smoke;
    private readonly AnalysisSmoke? analysisSmoke;
    private readonly TranscriptSmoke? transcriptSmoke;
    private readonly CreatorSmoke? creatorSmoke;
    private readonly QualificationSmoke? qualificationSmoke;
    private readonly LayoutSmoke? layoutSmoke;
    private readonly ScrubSmoke? scrubSmoke;
    private readonly FullscreenSmoke? fullscreenSmoke;
    private readonly EffectControlsSmoke? effectControlsSmoke;
    private bool ScriptedPointer => scrubSmoke is not null || fullscreenSmoke is not null || effectControlsSmoke is not null;
    public long StaleGenerationsRejected { get; private set; }
    public long SeekRequests { get; private set; }
    public long InputFailures { get; private set; }
    public bool Busy => job is not null || opening is not null || desired;
    public double MaximumAvSkew { get; private set; }
    public List<double> FrameMilliseconds { get; } = [];
    public EditorWindow(string? path = null, string? smokeDirectory = null, bool analyzeSmoke = false, bool transcribeSmoke = false, bool createSmoke = false,
        bool qualifySmoke = false, bool resizeSmoke = false, bool scrubTest = false, bool fullscreenTest = false, bool effectControlsTest = false)
    {
        SynchronizationContext.SetSynchronizationContext(context);
        diagnostics = new(smokeDirectory is null ? null : Path.Combine(Path.GetFullPath(smokeDirectory), "editor-diagnostics.json"));
        AppDomain.CurrentDomain.UnhandledException += UnhandledException;
        Gesture = new(Editor); input = Graphics.Window.CreateInput(); Graphics.Window.Title = "Valencia — timeline editor";
        cursor = new(input);
        widgetInput = new(Graphics.Window, input, cursor);
        Settings = new(keys, () => Graphics.GpuName, widgetInput);
        chrome = new(Graphics.Window);
        view.Chrome = chrome;
        if (scrubTest && smokeDirectory is not null) scrubSmoke = new(smokeDirectory);
        if (fullscreenTest && smokeDirectory is not null) fullscreenSmoke = new(smokeDirectory);
        if (effectControlsTest && smokeDirectory is not null) effectControlsSmoke = new(smokeDirectory);
        diagnostics.Record("window-created", generation, 0, $"gpu={Graphics.GpuName};hwnd={WindowHandle};size={Graphics.Window.Size};framebuffer={Graphics.Window.FramebufferSize}");
        foreach (var keyboard in input.Keyboards) { keyboard.KeyDown += (_, k, _) => HandlePointer(() => { keys.Record(k, true); Editor.Card = null; }); keyboard.KeyUp += (_, k, _) => HandlePointer(() => keys.Record(k, false)); }
        foreach (var mouse in input.Mice)
        {
            if (ScriptedPointer) break;
            // Exceptions escaping GLFW callbacks terminate Windows with 0xc000041d before Main can report them.
            mouse.MouseDown += (m, b) => HandlePointer(() => { if (!Settings.IsOpen) MouseDown(m, b); });
            mouse.MouseUp += (m, b) => HandlePointer(() => { if (!Settings.IsOpen) MouseUp(m, b); });
            mouse.MouseMove += (m, p) => HandlePointer(() => { if (!Settings.IsOpen) MouseMove(m, p); });
            mouse.Scroll += (m, wheel) => HandlePointer(() =>
            {
                if (Settings.IsOpen || contextMenu is not null) return;
                if (IsFullscreen) { if (wheel.Y != 0) Action(wheel.Y > 0 ? "VolumeUp" : "VolumeDown"); return; }
                var point = Point(m); var media = view.Layout.Media;
                if (point.X >= media.X && point.X < media.X + media.Width && point.Y >= media.Y && point.Y < media.Y + media.Height)
                    Editor.FirstAsset = Math.Clamp(Editor.FirstAsset - (int)wheel.Y, 0, Math.Max(0, Editor.Session.Snapshot.Document.Assets.Length - Editor.MediaPageSize));
                else if (keys.Control) Editor.Viewport.Zoom(Math.Pow(1.2, wheel.Y), Point(m).X);
                else if (keys.Shift) Editor.Viewport.FirstTrack = Math.Clamp(Editor.Viewport.FirstTrack - (int)wheel.Y, 0, Math.Max(0, Editor.Sequence.Tracks.Length - Editor.Viewport.VisibleTracks));
                else Editor.Viewport.Scroll(-wheel.Y * 90 - wheel.X * 90);
                lastUi = -1;
            });
        }
        Graphics.Window.FocusChanged += value => HandlePointer(() => { focused = value; if (!value) { CancelGesture(); feedbackDirty |= feedback.Clear(); } keys.Reset(); });
        // Native callbacks only invalidate state. Layout/font realization runs on the
        // normal event loop, never across GLFW's unmanaged callback boundary.
        Graphics.Window.Resize += _ => HandlePointer(() => { diagnostics.Record("resize", generation, Editor.Playhead.Ticks, $"size={Graphics.Window.Size};framebuffer={Graphics.Window.FramebufferSize}"); CancelGesture(); });
        Graphics.Window.Closing += () => HandlePointer(() => { if (smokeDirectory is null && !DiscardAllowed()) { Graphics.Window.IsClosing = false; return; } jobCancellation?.Cancel(); seekCancellation?.Cancel(); Gesture.Cancel(); });
        if (smokeDirectory is not null && !ScriptedPointer)
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
        if (keys.LoadWarning is { } shortcutWarning) Editor.Status = shortcutWarning;
        while (!Graphics.Window.IsClosing)
        {
            var frameClock = Stopwatch.StartNew(); Graphics.Window.DoEvents(); context.Pump(ReportFailure);
            if (!IsFullscreen) chrome.Update();
            if (chromeRevision != chrome.VisualRevision) { chromeRevision = chrome.VisualRevision; lastUi = -1; }
            double now = clock.Elapsed.TotalSeconds;
            // Widget input is polled every iteration so the shared transport never accumulates stale events.
            bool menuOwnsKeys = contextMenu is not null;
            foreach (var item in widgetInput.Poll().Events)
            {
                if (Settings.IsOpen) widgetInput.SetPointerCapture(Settings.Handle(item, clock.Elapsed));
                else if (contextMenu is not null) HandlePointer(() => HandleContextMenu(item));
            }
            // The keymap keeps running under Settings: it feeds InputMan's rebind capture. Commands are dropped there.
            if (focused) foreach (var action in keys.Read(now - lastTick)) if (!Settings.IsOpen && !menuOwnsKeys) Action(action.ToString()); lastTick = now;
            ApplyFullscreenToggle();
            if (Settings.TakeDirty()) lastUi = -1;
            if ((Gesture.Active || scrubbing || Editor.EffectDrag is not null) && GetCapture() != WindowHandle) CancelGesture();
            if (job?.IsCompleted == true)
            {
                if (job.GetAwaiter().GetResult() is Err<EditorFailure> failed) ReportFailure(failed.Error);
                job = null;
                lastUi = -1;
            }
            CompleteSeek();
            // At most one decode plus one latest pointer position. Continuous movement cannot repeatedly
            // cancel every frame before it is ready, nor build an unbounded queue of obsolete seeks.
            if (scrubbing && pendingScrub is { } target && opening is null)
            {
                pendingScrub = null;
                if (target != Editor.Playhead) Seek(target);
            }
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
            scrubSmoke?.Tick(this);
            fullscreenSmoke?.Tick(this);
            effectControlsSmoke?.Tick(this);
            if (!ScriptedPointer && input.Mice.Count > 0 && !Settings.IsOpen && contextMenu is null)
            {
                var point = input.Mice[0].Position;
                if (observedNativePointer != point)
                {
                    observedNativePointer = point;
                    PointerMove((point.X, point.Y));
                }
                if (point.X < 0 || point.Y < 0 || point.X >= Graphics.Window.Size.X || point.Y >= Graphics.Window.Size.Y)
                {
                    feedbackDirty |= feedback.Clear();
                    pointerPosition = null;
                }
            }
            Editor.Reversing = resume && reverseRequested && (opening is not null || desired)
                || Playback is { IsPlaying: true, IsReverse: true };
            UpdateFullscreenControls(now);
            if (now - lastUi >= .1) Prepare();
            else if (feedbackDirty) RefreshFeedback();
            if (contextDirty || contextMenu is not null) HandlePointer(() => RefreshContextMenu());
            cursor.Set(Settings.IsOpen ? Settings.Cursor : PointerCursor());
            UpdatePlayheadOverlay();
            Graphics.Draw();
            // A/V skew is a property of what reached the display: frames skipped because composition is throttled
            // are not observations. The loop itself keeps running at full rate either way.
            if (Graphics.LastDrawPresented && !desired && opening is null && Playback is { IsPlaying: true, Current: { } presentedFrame } presentedPlayback)
            {
                double observedClock = presentedPlayback.Position.TotalSeconds;
                Graphics.ObservePresentationClock(presentedFrame.Time.TotalSeconds, observedClock);
                MaximumAvSkew = Math.Max(MaximumAvSkew, Math.Abs(observedClock - presentedFrame.Time.TotalSeconds));
            }
            if (FrameMilliseconds.Count < 10000) FrameMilliseconds.Add(frameClock.Elapsed.TotalMilliseconds);
            if (smoke?.Finished == true || analysisSmoke?.Finished == true || transcriptSmoke?.Finished == true || creatorSmoke?.Finished == true || qualificationSmoke?.Finished == true || layoutSmoke?.Finished == true || scrubSmoke?.Finished == true || fullscreenSmoke?.Finished == true || effectControlsSmoke?.Finished == true) return;
            // While composition is throttled there is nothing to present; poll gently instead of spinning.
            Thread.Sleep(Graphics.LastDrawPresented ? 1 : 4);
        }
        if (smoke is not null || analysisSmoke is not null || transcriptSmoke is not null || creatorSmoke is not null || qualificationSmoke is not null || layoutSmoke is not null || ScriptedPointer) throw new InvalidOperationException("Editor smoke closed before completing its assertions.");
    }
    private void Prepare()
    {
        lastUi = clock.Elapsed.TotalSeconds;
        var size = Graphics.Window.Size;
        if (size.X <= 0 || size.Y <= 0) return;
        if (IsFullscreen) { PrepareFullscreen(size.X, size.Y); return; }
        long preparedAt = Stopwatch.GetTimestamp();
        Editor.ObsConnection = Obs?.Recording.Client.Status.State.ToString() ?? "Disconnected";
        Editor.ObsRecording = Obs is { } obs ? obs.Recording.Snapshot.State + " / " + obs.Recording.Snapshot.Markers.Length + " markers" : "No recording session";
        Editor.Recording = Obs?.Recording.Snapshot.State == "recording";
        if (Editor.Card is { } card && (clock.Elapsed.TotalSeconds > Editor.CardUntil
            || card.Kind == TitleCardKind.Proposal && Editor.ProposedCut is null && Editor.ProposedTextCut is null)) Editor.Card = null;
        if (Settings.IsOpen) { PrepareSettings(); return; }
        var result = view.TryPrepare(Editor, Gesture, Playback?.IsPlaying == true || resume && (opening is not null || desired), razor,
            Graphics.Window.Size.X, Graphics.Window.Size.Y, job is not null);
        if (result is not Ok<Machina.Pipeline.MachinaPreparedPresentation> ready)
        {
            if (result is Err<EditorFailure> failed) ReportFailure(failed.Error);
            else ReportFailure(new("layout", "Uninitialized preparation result.", false, null));
            return; // Retain the last valid frame and hit-test tree.
        }
        var prepared = ready.Value;
        Graphics.ProgramMonitor = view.HideVideo ? new(0, 0, 0, 0) : view.Layout.Monitor;
        Graphics.RecordCpuPhase("layoutAndPresentationBuildMs", Stopwatch.GetElapsedTime(preparedAt).TotalMilliseconds);
        long realizedAt = Stopwatch.GetTimestamp();
        feedback.Refresh(prepared.HitTest);
        RefreshFeedback();
        Graphics.Canvas = view.Canvas; lastUi = clock.Elapsed.TotalSeconds;
        lastReportedFailure = null;
        Graphics.RecordCpuPhase("nativeUiPreparationMs", Stopwatch.GetElapsedTime(realizedAt).TotalMilliseconds);
        Graphics.Window.Title = $"Valencia — {(Editor.Dirty ? "* " : "")}{Path.GetFileName(Editor.ProjectPath ?? "Untitled.valencia")}";
    }
    private void PrepareSettings()
    {
        var frame = Settings.Prepare(Graphics.Window.Size.X, Graphics.Window.Size.Y, chrome);
        Graphics.ProgramMonitor = new(0, 0, 0, 0);
        Graphics.Canvas = [];
        Graphics.UpdateUi(frame.PresentationFrame);
        lastUi = clock.Elapsed.TotalSeconds;
        Graphics.Window.Title = "Valencia - Settings";
    }
    internal void OpenSettings(SettingsSection section = SettingsSection.Shortcuts)
    {
        PauseTransport(); CancelGesture(); keys.Reset();
        Settings.Open(section); lastUi = -1;
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
        var rgba = Readback();
        using var bitmap = new Bitmap(Graphics.Window.FramebufferSize.X, Graphics.Window.FramebufferSize.Y, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { Marshal.Copy(rgba, 0, data.Scan0, rgba.Length); } finally { bitmap.UnlockBits(data); }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
    internal Machina.Pipeline.MachinaPreparedPresentation Presentation => view.Prepared ?? throw new InvalidOperationException("UI is not prepared.");
    internal byte[] Readback()
    {
        Prepare();
        RefreshContextMenu();
        UpdatePlayheadOverlay();
        return Graphics.Draw(true) ?? throw new InvalidOperationException("No GPU readback.");
    }
    public void Seek(SequenceTime position, bool play = false,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "", [System.Runtime.CompilerServices.CallerLineNumber] int line = 0,
        bool reverse = false)
    {
        Editor.Playhead = new(Math.Clamp(position.Ticks, 0, Editor.MonitorDuration.Ticks));
        resume = play; reverseRequested = reverse; generation++; SeekRequests++; desired = true;
        // The caller makes every seek attributable in diagnostics (a seek's origin is otherwise invisible).
        diagnostics.Record("seek-request", generation, Editor.Playhead.Ticks, $"play={play};reverse={reverse};scrubbing={scrubbing};from={caller}:{line}");
        seekLatency.Restart();
        Playback?.Pause(); seekCancellation?.Cancel();
        // Keep the last confirmed picture during a drag instead of destroying/reallocating its texture per mouse event.
        if (!scrubbing)
        {
            Graphics.ClearVideo();
            lastUi = -1;
        }
    }
    private void BeginSeek()
    {
        desired = false; openingGeneration = generation; var old = Playback; Playback = null;
        diagnostics.Record("seek-start", generation, Editor.Playhead.Ticks);
        var previousCancellation = seekCancellation; seekCancellation = new(); var token = seekCancellation.Token;
        var snapshot = Editor.Source?.Snapshot ?? Editor.Session.Snapshot; var id = Editor.Source?.SequenceId ?? Editor.SequenceId; var position = Editor.Playhead;
        var profile = Editor.Source is null ? Editor.Sequence.Creator?.Profiles.FirstOrDefault(p => p.Id == Editor.SelectedProfile) : null;
        bool prepareAudio = resume;
        bool reverse = reverseRequested;
        opening = Task.Run(async () =>
        {
            if (old is not null) await old.DisposeAsync(); previousCancellation?.Dispose();
            if (new TimelineQueries(snapshot).Duration(id).Ticks == 0) return null;
            if (prepareAudio) pausedPreview.Dispose();
            return prepareAudio ? await SequencePlayback.OpenAsync(snapshot, id, position, token, profile, reverse)
                : SequencePlayback.OpenPreview(pausedPreview, snapshot, id, position, token, profile);
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
                // The next opening worker retires this generation. Never join decoder/audio workers on the UI thread.
                StaleGenerationsRejected++; Playback = opened;
                diagnostics.Record("seek-stale", openingGeneration, Editor.Playhead.Ticks);
            }
            else
            {
                Playback = opened;
                if (opened is not null) opened.MonitorGain = EffectiveMonitorGain;
                diagnostics.Record("seek-complete", generation, Editor.Playhead.Ticks, $"elapsedMs={seekLatency.Elapsed.TotalMilliseconds:F2}");
                if (opened?.Current is { } frame)
                {
                    Graphics.UpdateVideo(frame.Width, frame.Height, frame.Rgba);
                    diagnostics.Record("seek-frame-uploaded", generation, frame.Time.Ticks,
                        $"sourcePTS={string.Join(';', frame.SourceTimestamps ?? [])};evaluationMs={frame.EvaluationMilliseconds:F2}");
                    if (SeekLatenciesMs.Count < 1000) SeekLatenciesMs.Add(seekLatency.Elapsed.TotalMilliseconds);
                }
                if (resume && opened is not null)
                {
                    if (opened.CanPlay) opened.Play(); else Seek(Editor.Playhead, true, reverse: reverseRequested);
                }
            }
        }
        catch (OperationCanceledException) when (openingGeneration != generation) { StaleGenerationsRejected++; diagnostics.Record("seek-cancelled", openingGeneration, Editor.Playhead.Ticks); }
        catch (Exception error) { Editor.Status = "Preview error: " + error.Message; Console.Error.WriteLine(error); diagnostics.Record("seek-error", generation, Editor.Playhead.Ticks, error.ToString()); diagnostics.Save(error); }
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
        jobCancellation?.Dispose(); jobCancellation = new(); job = EditorOperation.TryAsync("job", action);
    }
    private string? Pick(bool save, string filter, string title)
    {
        Playback?.Pause(); CancelGesture(); keys.Reset();
        using FileDialog dialog = save ? new SaveFileDialog() : new OpenFileDialog(); dialog.Filter = filter; dialog.Title = title;
        string? result = dialog.ShowDialog(new DialogOwner(WindowHandle)) == DialogResult.OK ? dialog.FileName : null;
        keys.Reset(); return result;
    }
    private sealed record DialogOwner(nint Handle) : IWin32Window;
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

    internal async Task RunWorkflow(string path)
    {
        PauseTransport();
        CancelGesture();
        await using var engine = new Valencia.Engine.EditingEngine(Editor.Session, projectPath: Editor.ProjectPath);
        var result = await scriptHost.RunWorkflowFileAsync(path, engine,
            new(SequenceId: Editor.SequenceId, Commit: true), jobCancellation?.Token ?? default);
        if (result.Committed) Seek(Editor.Playhead);
        if (!result.Success)
            Editor.Status = "Workflow failed: " + string.Join("; ", result.Script.Diagnostics.Select(d => d.Message));
        else
            Editor.Status = result.Committed ? "Workflow applied. Ctrl+Z undoes the entire edit." : "Workflow completed without edits.";
    }
    public void Action(string name)
    {
        try
        {
            if (name == "Fullscreen" || IsFullscreen && name == "Cancel")
            {
                fullscreenTogglePending = !fullscreenTogglePending;
                return;
            }
            if (IsFullscreen && HandleFullscreenAction(name)) return;
            if (name.StartsWith("window.", StringComparison.Ordinal))
            {
                var button = name switch
                {
                    "window.minimize" => Machina.Standard.Windowing.WindowCaptionButton.Minimize,
                    "window.maximize" => Machina.Standard.Windowing.WindowCaptionButton.Maximize,
                    "window.close" => Machina.Standard.Windowing.WindowCaptionButton.Close,
                    _ => Machina.Standard.Windowing.WindowCaptionButton.None
                };
                chrome.Invoke(button);
                return;
            }
            if (name == "Cancel") { CancelGesture(); jobCancellation?.Cancel(); Editor.ProposedCut = null; Editor.ProposedTextCut = null; return; }
            if (job is not null) return;
            if (EffectAction(name)) { lastUi = -1; return; }
            // Timeline edits act on the program playhead; leave source mode first (as clicking the timeline panel does in Premiere).
            if (Editor.Source is not null && name is "Split" or "Delete" or "RippleDelete" or "RippleTrimPrevious" or "RippleTrimNext" or "Lift" or "Extract"
                or "PreviousEdit" or "NextEdit" or "Undo" or "Redo" or "Insert" or "RazorTool" or "ZoomFit") ExitSource();
            switch (name)
            {
                case "Settings": case "KeyboardShortcuts": OpenSettings(); return;
                case "About": OpenSettings(SettingsSection.About); return;
                case "ObsMode": Editor.ObsMode = !Editor.ObsMode; Editor.ControlsMode = false; break;
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
                case "TranscriptMode": Editor.TranscriptMode = !Editor.TranscriptMode; Editor.ControlsMode = false; break;
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
                    if (Pick(false, "Copeland TS (*.ts, *.tsx)|*.ts;*.tsx", "Run Copeland TS") is { } scriptPath)
                    {
                        bool workflow = scriptPath.EndsWith(".workflow.ts", StringComparison.OrdinalIgnoreCase)
                            || scriptPath.EndsWith(".workflow.tsx", StringComparison.OrdinalIgnoreCase);
                        RunJob(() => workflow ? RunWorkflow(scriptPath) : RunTrustedScript(scriptPath));
                    }
                    break;
                case "Import":
                    if (Pick(false, "Media|*.mp4;*.mkv;*.mov;*.wav;*.mp3;*.webm|All files|*.*", "Import media") is { } media) RunJob(() => Import(media)); break;
                case "Insert": InsertSelected(); break;
                case "New": if (DiscardAllowed()) { Editor.New(); imports.Clear(); Seek(default); } break;
                case "NewSequence":
                    if (Editor.AddSequence().Success) { CancelGesture(); Seek(default); }
                    break;
                case "NextSequence":
                {
                    var sequences = Editor.Session.Snapshot.Document.Sequences;
                    if (sequences.Length < 2) break;
                    int current = sequences.IndexOf(Editor.Sequence);
                    CancelGesture();
                    Editor.SelectSequence(sequences[(current + 1) % sequences.Length].Id);
                    Seek(default);
                    break;
                }
                case "AddSequenceMarker":
                {
                    var at = Editor.TimelinePlayhead;
                    var marker = new SequenceMarker(Guid.NewGuid(), at, "Marker");
                    if (Editor.Commit(new SetSequenceMarker(Editor.SequenceId, marker)).Success)
                        Editor.Status = $"Marker added at {Timecode.Format(at, Editor.Sequence.Video.FrameRate)}.";
                    break;
                }
                case "NextSequenceMarker": case "PreviousSequenceMarker":
                    if (Editor.AdjacentMarker(name == "NextSequenceMarker") is { } markerTarget)
                    {
                        Editor.Source = null;
                        Seek(markerTarget.At);
                        Editor.Status = markerTarget.Label;
                    }
                    else Editor.Status = "No marker in that direction.";
                    break;
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
                case "TogglePlay": if (Playback?.IsPlaying == true || resume && (opening is not null || desired)) PauseTransport(); else StartPlaying(); break;
                case "Pause": PauseTransport(); break;
                case "Play": StartPlaying(); break;
                case "PlayReverse": StartPlaying(reverse: true); break;
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
                case "Tab:Media": case "Tab:Transcript": case "Tab:Record": case "Tab:Controls":
                    Editor.TranscriptMode = name == "Tab:Transcript";
                    Editor.ObsMode = name == "Tab:Record";
                    Editor.ControlsMode = name == "Tab:Controls";
                    break;
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
                    var frameRate = Editor.MonitorFrameRate;
                    long index = TimelineTime.FrameIndex(Editor.Playhead, frameRate, TimeRounding.NearestAwayFromZero);
                    Seek(TimelineTime.FromFrames(Math.Max(0, index + (name == "NextFrame" ? 1 : -1)), frameRate)); break;
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
    private void StartPlaying(bool reverse = false)
    {
        if (reverse && Editor.Playhead.Ticks <= 0)
        {
            PauseTransport();
            return;
        }
        if (opening is not null || desired)
        {
            if (reverseRequested != reverse) Seek(Editor.Playhead, true, reverse: reverse);
            else resume = true;
            return;
        }
        if (!reverse && Editor.Playhead.Ticks >= Editor.MonitorDuration.Ticks)
            Seek(default, true);
        else if (Playback is null || !Playback.CanPlay || Playback.IsReverse != reverse)
            Seek(Editor.Playhead, true, reverse: reverse);
        else
            Playback.Play();
    }
    private void PauseTransport() { resume = false; Playback?.Pause(); lastUi = -1; }
    private static (double X, double Y) Point(IMouse mouse) => (mouse.Position.X, mouse.Position.Y);
    private void MouseDown(IMouse mouse, MouseButton button)
    {
        if (button == MouseButton.Right)
        {
            OpenContextMenu(Point(mouse));
            return;
        }
        if (button != MouseButton.Left || job is not null) return;
        PointerDown(Point(mouse));
    }
    internal double RulerY => view.Layout.RulerY;
    internal void PointerDown((double X, double Y) p)
    {
        if (contextMenu is not null) return;
        pointerPosition = new(p.X, p.Y);
        feedbackDirty |= feedback.Press(new(p.X, p.Y));
        if (IsFullscreen) { FullscreenPointerDown(p); return; }
        if (view.Prepared is null || view.IsCompact) return;
        diagnostics.Record("pointer-down", generation, Editor.Playhead.Ticks, $"x={p.X};y={p.Y}");
        diagnostics.Save(); // persist the last gesture even if a native fault prevents managed unwinding
        var viewport = Editor.Viewport;
        if (BeginMonitorEffect(p.X, p.Y) || BeginTimelineEffect(p.X, p.Y)) return;
        if (p.X >= viewport.ClipLeft && p.X <= viewport.ClipRight && p.Y >= view.Layout.RulerY && p.Y < viewport.TrackTop + viewport.VisibleTracks * viewport.Row)
        {
            PauseTransport(); ExitSource();
            if (Editor.Viewport.Hit(Editor.Sequence, p.X, p.Y) is { } hit && p.Y >= viewport.TrackTop)
            {
                Editor.SelectedTitle = null;
                Editor.SelectedClip = hit.Clip.Id;
                if (razor) { Seek(Snap(p.X)); Editor.Split(); Seek(Editor.Playhead); }
                else { Gesture.Begin(hit.Clip, hit.Edge, p.X); CapturePointer(); }
            }
            else
            {
                Editor.SelectedClip = null;
                scrubSnapPoints = Editor.Snapping ? CollectSnapPoints() : null;
                scrubbing = true;
                CapturePointer();
                UpdateScrubTarget(p.X);
            }
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
            var points = scrubSnapPoints ?? CollectSnapPoints();
            int index = Array.BinarySearch(points, time.Ticks);
            if (index >= 0) return time;
            index = ~index;
            long before = index > 0 ? time.Ticks - points[index - 1] : long.MaxValue;
            long after = index < points.Length ? points[index] - time.Ticks : long.MaxValue;
            if (before <= after && before <= threshold) return new(points[index - 1]);
            if (after <= threshold) return new(points[index]);
        }
        return TimelineTime.SnapToFrame(time, Editor.Sequence.Video.FrameRate, TimeRounding.NearestAwayFromZero);
    }
    private long[] CollectSnapPoints() => EditorEdits.EditPoints(Editor.Sequence)
        .Concat(Editor.Sequence.Markers.Select(marker => marker.At.Ticks))
        .Concat(new[] { Editor.MarkIn?.Ticks, Editor.MarkOut?.Ticks }.OfType<long>())
        .Distinct().Order().ToArray();

    private void UpdateScrubTarget(double x)
    {
        var time = Snap(x);
        Editor.ScrubTarget = pendingScrub = new SequenceTime(Math.Clamp(time.Ticks, 0, Editor.MonitorDuration.Ticks));
    }
    private void OpenSelectedSource()
    {
        if (Editor.SelectedAsset is not { } id) { Editor.Status = "Select media in the Media tab to view it in the source monitor."; return; }
        if (!imports.TryGetValue(id, out var imported))
        {
            var asset = Editor.Session.Snapshot.Document.Assets.First(a => a.Id == id);
            RunJob(async () => { imports[id] = await Task.Run(() => MediaAssetProbe.Import(id, asset.SourcePath)); OpenSelectedSource(); }); return;
        }
        var source = CreateSourceView(imported);
        ShowSource(source);
        Editor.Status = "Source: " + source.Name + ". I/O mark, , insert, . overwrite at the timeline playhead.";
    }

    // Source monitoring uses the editor evaluator on a transient one-asset sequence.
    private SourceView CreateSourceView(ImportedAsset imported)
    {
        var sequence = Editor.Sequence;
        var video = imported.Asset.Streams.FirstOrDefault(s => s.Kind == TrackKind.Video);
        var format = video is { Width: { } width, Height: { } height }
            ? new VideoFormat(width, height, video.FrameRate ?? sequence.Video.FrameRate) : sequence.Video;
        var sourceId = Guid.NewGuid();
        var document = ProjectDocument.Create(Guid.NewGuid(), imported.Asset.Name) with
        {
            Assets = [imported.Asset], Sequences = [new SequenceDocument(sourceId, "Source", format, sequence.AudioSampleRate, [])]
        };
        var session = new EditingSession(new ProjectSnapshot(document, 0));
        var operations = Valencia.Engine.InsertionCommands.Build(session.Snapshot, sourceId, imported, default);
        var built = session.Execute(new EditTransaction(Guid.NewGuid(), 1, 0, [.. operations.Select(o => new EditCommand(Guid.NewGuid(), 1, o))]));
        if (!built.Success) throw new InvalidDataException("Cannot open source: " + (built.Message ?? built.Error.ToString()));
        return new(imported.Asset.Id, imported.Asset.Name, built.Snapshot, sourceId,
            new TimelineQueries(built.Snapshot).Duration(sourceId), imported);
    }

    private void ShowSource(SourceView source, bool play = false, bool useMarks = true)
    {
        if (Editor.Source is null) Editor.ProgramPlayhead = Editor.Playhead;
        Editor.Source = source;
        // Start on the first picture: recordings often begin audio a few ms before video (OBS AAC starts at -21 ms).
        var firstVideo = source.Snapshot.Document.Sequences[0].Tracks.Where(t => t.Kind == TrackKind.Video)
            .SelectMany(t => t.Clips).Select(c => c.Start.Ticks).DefaultIfEmpty().Min();
        var mark = useMarks ? Editor.SourceMarks.GetValueOrDefault(source.AssetId).In : null;
        Seek(mark ?? new SequenceTime(firstVideo), play);
    }
    private void ExitSource()
    {
        if (Editor.Source is null) return;
        Editor.Source = null; Seek(Editor.ProgramPlayhead);
    }

    private void MouseMove(IMouse mouse, System.Numerics.Vector2 position)
    {
        observedNativePointer = position;
        PointerMove((position.X, position.Y));
    }
    internal void PointerMove((double X, double Y) p)
    {
        if (contextMenu is not null) return;
        feedbackDirty |= feedback.Move(new(p.X, p.Y));
        pointerPosition = new(p.X, p.Y);
        if (IsFullscreen)
        {
            RevealFullscreenControls();
            if (scrubbing) UpdateFullscreenScrub(p.X);
            return;
        }
        if (Editor.EffectDrag is { } effect) { effect.Move(Editor.Session, p.X, p.Y); lastUi = -1; }
        else if (Gesture.Active) { Gesture.Move(p.X, p.Y); lastUi = -1; }
        else if (scrubbing) UpdateScrubTarget(p.X);
    }
    private void MouseUp(IMouse mouse, MouseButton button)
    {
        if (button != MouseButton.Left) return;
        PointerUp(Point(mouse));
    }
    internal void PointerUp((double X, double Y) p)
    {
        if (contextMenu is not null) return;
        pointerPosition = new(p.X, p.Y);
        feedbackDirty |= feedback.Release(new(p.X, p.Y));
        if (IsFullscreen) { FullscreenPointerUp(p); return; }
        diagnostics.Record("pointer-up", generation, Editor.Playhead.Ticks);
        if (Editor.EffectDrag is not null) ReleaseEffect(p.X, p.Y);
        else if (Gesture.Active) { Gesture.Move(p.X, p.Y); if (Gesture.Release()) Seek(Editor.Playhead); ReleasePointer(); }
        else if (scrubbing)
        {
            UpdateScrubTarget(p.X);
            var target = Editor.ScrubTarget!.Value;
            ClearScrub();
            ReleasePointer();
            if (target != Editor.Playhead) Seek(target);
        }
        else { if (pointer.Release(view.Prepared?.HitTest.HitTest(new PointerPoint(p.X, p.Y)))?.Name is { } name) Action(name); }
        lastUi = -1;
        diagnostics.Save();
    }
    private void ClearScrub()
    {
        scrubbing = false;
        Editor.ScrubTarget = pendingScrub = null;
        scrubSnapPoints = null;
    }
    private void CancelGesture() { CloseContextMenu(); Gesture.Cancel(); Editor.EffectDrag = null; ClearScrub(); fullscreenVideoPressed = false; pointer.Reset(); feedbackDirty |= feedback.Clear(); pointerPosition = null; ReleasePointer(); lastUi = -1; }
    private nint WindowHandle => Graphics.Window.Native?.Win32?.Hwnd ?? 0;
    private void CapturePointer()
    {
        if (WindowHandle == 0) throw new InvalidOperationException("Timeline input requires a Win32 window handle.");
        SetCapture(WindowHandle);
        if (GetCapture() != WindowHandle) throw new InvalidOperationException("Could not capture timeline pointer.");
    }
    private void ReleasePointer() { if (WindowHandle != 0 && GetCapture() == WindowHandle) ReleaseCapture(); }
    internal void HandlePointer(Action action)
    {
        var result = EditorOperation.Try("input", () => { action(); return new Completed(); });
        if (result is Err<EditorFailure> failed)
        {
            InputFailures++;
            CancelGesture();
            var error = failed.Error.Cause;
            Editor.Status = "Timeline input error: " + failed.Error.Message;
            Console.Error.WriteLine(error);
            string directory = diagnostics.DirectoryPath;
            try { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "last-input-error.txt"), error?.ToString() ?? failed.Error.Message); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            diagnostics.Record("pointer-error", generation, Editor.Playhead.Ticks, error?.ToString()); diagnostics.Save(error);
        }
    }
    private void ReportFailure(EditorFailure error)
    {
        Editor.Status = error.Cancelled ? "Cancelled." : $"{error.Operation} error: {error.Message}";
        string identity = error.Operation + ": " + (error.Cause?.ToString() ?? error.Message);
        if (lastReportedFailure == identity) return;
        lastReportedFailure = identity;
        if (error.Cause is not null && !error.Cancelled) Console.Error.WriteLine(error.Cause);
        diagnostics.Record(error.Cancelled ? "operation-cancelled" : "operation-error", generation,
            Editor.Playhead.Ticks, error.Cause?.ToString() ?? error.Message);
        diagnostics.Save(error.Cancelled ? null : error.Cause);
    }
    private void UnhandledException(object sender, UnhandledExceptionEventArgs args) => diagnostics.Save(args.ExceptionObject as Exception);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint GetCapture();
    public void Dispose()
    {
        AppDomain.CurrentDomain.UnhandledException -= UnhandledException;
        diagnostics.Record("shutdown", generation, Editor.Playhead.Ticks); diagnostics.Save();
        jobCancellation?.Cancel(); seekCancellation?.Cancel();
        while (job is { IsCompleted: false }) { context.Pump(); Thread.Sleep(1); }
        if (job is not null) try { job.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } catch (Exception e) { Console.Error.WriteLine(e); }
        if (opening is not null) try { opening.GetAwaiter().GetResult()?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch (OperationCanceledException) { } catch (Exception e) { Console.Error.WriteLine(e); }
        Playback?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        CloseContextMenu();
        widgetInput.Dispose();
        pausedPreview.Dispose();
        qualificationSmoke?.Dispose();
        obsEngine?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        scriptHost.Dispose(); chrome.Dispose(); cursor.Dispose(); input.Dispose(); Graphics.Dispose(); seekCancellation?.Dispose(); jobCancellation?.Dispose();
        SynchronizationContext.SetSynchronizationContext(previousContext);
    }
}
