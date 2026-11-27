using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Silk.NET.Maths;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Machina.Presentation;
using Machina.Layout.Geometry;
using Machina.Core.Authoring;
using Machina.Core.Actions;
using Machina.Core.Nodes;
using Machina.Pipeline;
using Machina.Standard.Authoring;
using Valencia.Rendering;

namespace Valencia.App.Editing;

/// <summary>Real Vulkan window resize/presentation smoke. It does not synthesize OS pointer events.</summary>
internal sealed class LayoutSmoke(string directory)
{
    private readonly Stopwatch deadline = Stopwatch.StartNew();
    private readonly (int Width, int Height, string Name)[] sizes =
        [(1280, 720, "desktop"), (1145, 670, "compact-desktop"), (1920, 1080, "desktop-wide"), (2560, 1440, "desktop-1440"), (768, 1024, "vertical-tablet"),
         (390, 844, "vertical-phone"), (1, 1, "tiny"), (20, 720, "narrow"),
         (320, 200, "short"), (2559, 1, "flat"), (1280, 720, "desktop-restored")];
    private readonly List<object> results = [];
    private int sizeIndex, frames, frameStart;
    private bool resizing = true, benchmark, stateApplied;
    private int stateIndex;
    // Transient UI states captured at the restored desktop size; none of them edits the document.
    private static readonly (string Name, Action<EditorWindow> Apply)[] States =
    [
        ("panel-transcript", w => { w.Editor.TranscriptMode = true; w.Editor.ObsMode = false; }),
        ("panel-record", w => { w.Editor.ObsMode = true; w.Editor.TranscriptMode = false; }),
        // Mix tab with fixed meter readings: one hot strip with a latched over, one nominal, one quiet, master near the ceiling.
        ("panel-mix", w =>
        {
            var e = w.Editor; e.ObsMode = false; e.TranscriptMode = false; e.MixMode = true;
            var audio = e.Sequence.Tracks.Where(t => t.Kind == TrackKind.Audio).ToArray();
            e.Meter.Pinned = true;
            double[][] readings = [[-2, -9, -1, 1], [-12, -20, -10, 0], [-30, -38, -28, 0]];
            for (int i = 0; i < audio.Length; i++)
            {
                var r = readings[i % readings.Length];
                if (!e.Meter.Tracks.TryGetValue(audio[i].Id, out var channel)) e.Meter.Tracks[audio[i].Id] = channel = new();
                channel.Set(r[0], r[1], r[2], r[3] > 0);
            }
            e.Meter.Left.Set(-3, -14, -1.2, false); e.Meter.Right.Set(-4, -15, -2.5, false);
            if (audio.FirstOrDefault(t => t.Clips.Length > 0) is { } track) { e.SelectedClip = track.Clips[0].Id; e.TargetTrack = track.Id; }
        }),
        ("editing-selection", w =>
        {
            var e = w.Editor; e.ObsMode = false; e.TranscriptMode = false; e.MixMode = false; e.Magnetic = true;
            e.Meter.Pinned = false;
            e.SelectedClip = e.Sequence.Tracks.First(t => t.Kind == TrackKind.Video && t.Clips.Length > 0).Clips[0].Id;
            e.MarkIn = T(1); e.MarkOut = T(3);
        }),
        ("source-monitor", w => { w.Editor.SelectedClip = null; w.Editor.Magnetic = false; w.Action("MonitorSource"); }),
        // Title-card moments, held open (sticky) for the capture.
        ("card-export", w =>
        {
            w.Action("MonitorProgram"); var e = w.Editor;
            w.ShowCard(new(TitleCardKind.Export, "EXPORT", "COMPLETE.", "Written to layout-smoke.mp4.",
                [$"LAYOUT-SMOKE.MP4 / {Timecode.Format(e.Sequence.Duration, e.Sequence.Video.FrameRate)} / 0 MB"]), sticky: true);
        }),
        ("card-recording", w => w.ShowCard(new(TitleCardKind.Recording, "REC", "RECORDING\n12:00:00",
            "OBS is recording. Mark highlight drops a marker without stopping.", ["OBS DISCONNECTED"]), sticky: true)),
        ("card-proposal", w =>
        {
            var e = w.Editor;
            System.Collections.Immutable.ImmutableArray<Valencia.Engine.TimelineRemoval> removed = [new(T(1), T(2)), new(new(T(3).Ticks + SequenceTime.TicksPerSecond / 2), T(4))];
            var estimated = new SequenceTime(e.Sequence.Duration.Ticks - 3 * SequenceTime.TicksPerSecond / 2);
            e.ProposedCut = new(1, true, "layout-smoke", null, null, [], removed, estimated, null, []);
            w.ShowProposal("Rough cut", removed, estimated.TotalSeconds);
        }),
        // Title clips in the Valencia look: a full-frame card, then the same clip as a lower third over footage.
        ("title-card", w =>
        {
            w.Editor.Card = null; w.Editor.ProposedCut = null; w.Editor.Playhead = T(1);
            w.NewTitle("EPISODE 01", "The beginning");
        }),
        ("title-lower-third", w => { w.SetTitleStyle(TitleStyles.LowerThird); w.EditTitleText("YUECHEN LI", "Host"); }),
        // Command palette over the editor with a typed query: ranked matches, shortcut column, top match highlighted.
        ("command-palette", w =>
        {
            w.Editor.Card = null; w.Editor.ProposedCut = null;
            w.OpenPalette();
            foreach (char c in "ex") w.HandlePalette(new Machina.Runtime.Input.UiTextEntered(c.ToString()));
        }),
        // Settings: the shortcut list mid-capture, then About.
        ("settings-shortcuts", w =>
        {
            w.ClosePalette();
            w.Editor.Card = null; w.Editor.ProposedCut = null;
            w.OpenSettings(); w.Settings.BeginCapture("TogglePlay");
        }),
        ("settings-about", w => w.OpenSettings(SettingsSection.About)),
        // Last: an empty project shows the monitor's resting state. The benchmark below reopens the original document.
        ("no-signal", w => { w.Settings.Close(); w.Editor.New(); }),
    ];
    private ProjectSnapshot? original;
    public bool Finished { get; private set; }

    public void Tick(EditorWindow window)
    {
        if (deadline.Elapsed.TotalSeconds > 120) throw new TimeoutException("Native layout smoke timed out.");
        if (window.Busy) return;
        Directory.CreateDirectory(directory);
        var editor = window.Editor;
        if (original is null) original = editor.Session.Snapshot;
        if (sizeIndex < sizes.Length)
        {
            var size = sizes[sizeIndex];
            if (resizing)
            {
                window.Graphics.Window.Size = new Vector2D<int>(size.Width, size.Height);
                resizing = false; frames = 0; frameStart = window.FrameMilliseconds.Count;
                return;
            }
            if (++frames < 45) return;
            window.Capture(Path.Combine(directory, size.Name + ".png"));
            var buttonAlignment = QualifyButtons(window);
            bool compact = size.Width < 320 || size.Height < 480;
            if (!compact && window.Graphics.TextGlyphCount < 100) throw new InvalidOperationException("Native text glyphs were not submitted.");
            if (!DocumentSemantics.Equal(original.Document, editor.Session.Snapshot.Document))
                throw new InvalidOperationException("A layout change altered the editing document.");
            var measured = window.FrameMilliseconds.Skip(frameStart + 5).Order().ToArray();
            var layout = EditorLayout.For(window.Graphics.Window.Size.X, window.Graphics.Window.Size.Y);
            results.Add(new { size.Name, size.Width, size.Height, profile = layout.Profile.ToString(),
                framebufferWidth = window.Graphics.Window.FramebufferSize.X, framebufferHeight = window.Graphics.Window.FramebufferSize.Y,
                textGlyphs = window.Graphics.TextGlyphCount, msdfGlyphs = window.Graphics.MsdfGlyphCount,
                fallbackTextRuns = window.Graphics.PixelFallbackTextRuns,
                atlasUploads = window.Graphics.AtlasUploadCount, visibleTracks = editor.Viewport.VisibleTracks,
                frameMedianMs = measured[measured.Length / 2], frameP95Ms = measured[(int)(measured.Length * .95)],
                buttonAlignment });
            sizeIndex++; resizing = true; return;
        }
        if (stateIndex < States.Length)
        {
            if (!stateApplied) { States[stateIndex].Apply(window); stateApplied = true; frames = 0; return; }
            if (++frames < 60) return;
            window.Capture(Path.Combine(directory, States[stateIndex].Name + ".png"));
            stateIndex++; stateApplied = false;
            if (stateIndex == States.Length) { editor.Card = null; editor.ProposedCut = null; editor.Open(original!, "layout-fixture.valencia"); window.Action("MonitorProgram"); editor.SelectedClip = null; editor.MarkIn = null; editor.MarkOut = null; editor.Playhead = default; editor.ObsMode = false; editor.TranscriptMode = false; }
            return;
        }
        if (!benchmark)
        {
            var sequence = editor.Sequence;
            var seed = sequence.Tracks.First(t => t.Kind == TrackKind.Video && t.Clips.Length > 0).Clips[0];
            var track = sequence.Tracks.First(t => t.Clips.Any(c => c.Id == seed.Id));
            var clips = Enumerable.Range(0, 5000).Select(i => seed with { Id = Guid.NewGuid(),
                Start = T(i * 2), SourceOut = seed.SourceIn.Shift(T(1)) }).ToImmutableArray();
            var expanded = original.Document with { Sequences = original.Document.Sequences.Replace(sequence,
                sequence with { Tracks = sequence.Tracks.Replace(track, track with { Clips = clips }) }) };
            editor.Open(new(expanded, original.Revision), "layout-fixture.valencia");
            editor.Viewport.Scroll(8000 * 90); benchmark = true; frames = 0; frameStart = window.FrameMilliseconds.Count;
            return;
        }
        if (++frames < 120) return;
        window.Capture(Path.Combine(directory, "5000-clips.png"));
        var trackWithClips = editor.Sequence.Tracks.First(t => t.Clips.Length == 5000);
        var visible = editor.Viewport.Visible(trackWithClips).ToArray();
        // Clips are 1 s long every 2 s: anything beyond the visible span's share (plus edges) means culling failed.
        double span = editor.Viewport.Time(editor.Viewport.ClipRight).TotalSeconds - editor.Viewport.Origin.TotalSeconds;
        if (visible.Length > (int)Math.Ceiling(span / 2) + 2) throw new InvalidOperationException("Visible-range culling failed.");
        var clip = visible[1];
        var timer = Stopwatch.StartNew();
        double x = editor.Viewport.X(clip.Start) + 20;
        window.Gesture.Begin(clip, 0, x);
        window.Gesture.Move(x + 30, editor.Viewport.TrackTop + 20);
        bool valid = window.Gesture.Valid; window.Gesture.Cancel(); timer.Stop();
        if (!valid) throw new InvalidOperationException("Native viewport's drag proposal was rejected.");
        var times = window.FrameMilliseconds.Skip(frameStart + 5).Order().ToArray();
        editor.Status = "剪辑 — résumé / native pixel fallback";
        window.Capture(Path.Combine(directory, "unicode.png"));
        if (window.Graphics.PixelFallbackTextRuns == 0) throw new InvalidOperationException("Unicode fallback was not exercised.");
        var deviceScales = QualifyDeviceScales(window);
        if (window.Graphics.ValidationErrorCount != 0) throw new InvalidOperationException("Vulkan validation errors during UI qualification.");
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            outcome = "passed", gpu = window.Graphics.GpuName, runtime = Environment.Version.ToString(),
            pipeline = "Machina -> MSDF commands and headings + hinted data text -> Vulkan",
            validationAvailable = window.Graphics.ValidationAvailable, validationErrors = window.Graphics.ValidationErrorCount,
            deviceScales,
            layouts = results, semanticDocumentPreservedByResize = true, syntheticClips = 5000, visibleClips = visible.Length,
            benchmarkFrames = frames, frameMedianMs = times[times.Length / 2], frameP95Ms = times[(int)(times.Length * .95)],
            inputProposalMs = timer.Elapsed.TotalMilliseconds, elapsedSeconds = deadline.Elapsed.TotalSeconds,
            unicodeFallbackExercised = true, physicalPointerActionsTested = false, nonWindowsDevicesTested = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        Finished = true;
    }
    private static IReadOnlyList<object> QualifyButtons(EditorWindow window)
    {
        byte[] pixels = window.Readback();
        return QualifyButtons(window, pixels, window.Presentation);
    }
    private static IReadOnlyList<object> QualifyButtons(EditorWindow window, byte[] pixels, MachinaPreparedPresentation presentation)
    {
        int width = window.Graphics.Window.FramebufferSize.X, height = window.Graphics.Window.FramebufferSize.Y;
        double scaleX = width / (double)presentation.PresentationFrame.Viewport.Width;
        double scaleY = height / (double)presentation.PresentationFrame.Viewport.Height;
        var results = new List<object>();
        string[] actions = ["Undo", "Redo", "TogglePlay", "MarkOut", "NextFrame", "NextEdit", "OutputProfile", "Analyze", "RoughCut", "OverwriteEdit"];
        foreach (var entry in presentation.Lowering.Actions.Where(entry => actions.Contains(entry.Value.Name)))
        {
            string action = entry.Value.Name;
            Rect bounds = presentation.Resolved.Nodes[entry.Key].Rect;
            var text = presentation.PresentationFrame.Operations.OfType<PositionedTextOperation>()
                .FirstOrDefault(t => t.SourceId == entry.Key.Child("label").Value);
            if (text is null) throw new InvalidOperationException($"No label operation for {action}.");
            uint color = text.Color.Rgba;
            double foreground = .2126 * (color >> 24) + .7152 * ((color >> 16) & 255) + .0722 * ((color >> 8) & 255);
            int backgroundX = Math.Clamp((int)Math.Ceiling(bounds.X * scaleX) + 3, 0, width - 1);
            int backgroundY = Math.Clamp((int)Math.Ceiling(bounds.Y * scaleY) + 3, 0, height - 1);
            int backgroundOffset = (backgroundY * width + backgroundX) * 4;
            double background = .2126 * pixels[backgroundOffset] + .7152 * pixels[backgroundOffset + 1] + .0722 * pixels[backgroundOffset + 2];
            double direction = foreground < background ? -1 : 1;
            double fullContrast = Math.Abs(foreground - background);
            double threshold = Math.Max(12, fullContrast * .2); // include visible antialiased edges, not only near-opaque interiors
            double peakContrast = 0;
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (int y = Math.Max(0, (int)Math.Ceiling(bounds.Y * scaleY) + 2); y < Math.Min(height, (int)Math.Floor((bounds.Y + bounds.Height) * scaleY) - 2); y++)
            for (int x = Math.Max(0, (int)Math.Ceiling(bounds.X * scaleX) + 2); x < Math.Min(width, (int)Math.Floor((bounds.X + bounds.Width) * scaleX) - 2); x++)
            {
                int offset = (y * width + x) * 4;
                double luminance = .2126 * pixels[offset] + .7152 * pixels[offset + 1] + .0722 * pixels[offset + 2];
                double contrast = direction * (luminance - background);
                peakContrast = Math.Max(peakContrast, contrast);
                if (contrast < threshold) continue;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
            if (maxX < minX) throw new InvalidOperationException($"No visible ink for {action}.");
            if (peakContrast < fullContrast * .5) throw new InvalidOperationException($"Insufficient text contrast for {action}.");
            double dx = (minX + maxX + 1) / 2.0 - (bounds.X + bounds.Width / 2) * scaleX;
            double dy = (minY + maxY + 1) / 2.0 - (bounds.Y + bounds.Height / 2) * scaleY;
            if (Math.Abs(dx) > 1.5 || Math.Abs(dy) > 1.5)
                throw new InvalidOperationException($"{action} visible GPU ink is off-center: dx={dx:F2}, dy={dy:F2} framebuffer pixels.");
            results.Add(new { action, label = text.Text, role = text.Style.Size.ToString(), dx, dy, bounds, threshold, peakContrast });
        }
        return results;
    }
    private static IReadOnlyList<object> QualifyDeviceScales(EditorWindow window)
    {
        var results = new List<object>();
        window.Graphics.ClearVideo(); window.Graphics.Canvas = [];
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        foreach (var role in new[] { Machina.Core.Styling.TextSize.Md, Machina.Core.Styling.TextSize.Lg })
        {
            int width = (int)Math.Round(window.Graphics.Window.FramebufferSize.X / scale);
            int height = (int)Math.Round(window.Graphics.Window.FramebufferSize.Y / scale);
            var children = new List<UiNode>();
            int row = 0;
            foreach (var (label, action) in new[] { ("UNDO", "Undo"), ("REDO", "Redo"), ("PLAY", "TogglePlay"), ("OUT", "MarkOut"), (">", "NextFrame"), (">>", "NextEdit"), ("VIEW / SEQUENCE", "OutputProfile") })
            {
                var style = action == "TogglePlay" ? EditorView.Ui.Primary : EditorView.Ui.Plain;
                children.Add(StandardUI.Button(label, UiAction.Named(action), 30, 12 + row++ * 38, 220, 30,
                    style: style with { TextStyle = style.TextStyle with { Size = role } }));
            }
            if (role == Machina.Core.Styling.TextSize.Md)
                children.Add(StandardUI.Button("ROUGH CUT", UiAction.Named("RoughCut"), 30, 12 + row * 38, 64, 28, style: EditorView.Ui.Plain));
            var presentation = new MachinaPresentationPipeline().Prepare(UI.Surface(children: children), width, height,
                new Machina.Core.Lowering.UiLoweringOptions(NativeUiText.Instance));
            window.Graphics.UpdateUi(presentation.PresentationFrame);
            var pixels = window.Graphics.Draw(true) ?? throw new InvalidOperationException("No scaled GPU readback.");
            results.Add(new { scale, role = role.ToString(), logicalWidth = width, framebufferWidth = window.Graphics.Window.FramebufferSize.X,
                buttonAlignment = QualifyButtons(window, pixels, presentation) });
        }
        return results;
    }
    private static SequenceTime T(int seconds) => new(seconds * SequenceTime.TicksPerSecond);
}
