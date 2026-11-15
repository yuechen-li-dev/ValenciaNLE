using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Silk.NET.Maths;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;

namespace Valencia.App.Editing;

/// <summary>Real Vulkan window resize/presentation smoke. It does not synthesize OS pointer events.</summary>
internal sealed class LayoutSmoke(string directory)
{
    private readonly Stopwatch deadline = Stopwatch.StartNew();
    private readonly (int Width, int Height, string Name)[] sizes =
        [(1280, 720, "desktop"), (1920, 1080, "desktop-wide"), (768, 1024, "vertical-tablet"),
         (390, 844, "vertical-phone"), (1280, 720, "desktop-restored")];
    private readonly List<object> results = [];
    private int sizeIndex, frames, frameStart;
    private bool resizing = true, benchmark;
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
            if (window.Graphics.MsdfGlyphCount < 100) throw new InvalidOperationException("Native MSDF text was not submitted.");
            if (!DocumentSemantics.Equal(original.Document, editor.Session.Snapshot.Document))
                throw new InvalidOperationException("A layout change altered the editing document.");
            var measured = window.FrameMilliseconds.Skip(frameStart + 5).Order().ToArray();
            var layout = EditorLayout.For(window.Graphics.Window.Size.X, window.Graphics.Window.Size.Y);
            results.Add(new { size.Name, size.Width, size.Height, profile = layout.Profile.ToString(),
                framebufferWidth = window.Graphics.Window.FramebufferSize.X, framebufferHeight = window.Graphics.Window.FramebufferSize.Y,
                msdfGlyphs = window.Graphics.MsdfGlyphCount, fallbackTextRuns = window.Graphics.PixelFallbackTextRuns,
                atlasUploads = window.Graphics.AtlasUploadCount, visibleTracks = editor.Viewport.VisibleTracks,
                frameMedianMs = measured[measured.Length / 2], frameP95Ms = measured[(int)(measured.Length * .95)] });
            sizeIndex++; resizing = true; return;
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
        if (visible.Length > 10) throw new InvalidOperationException("Visible-range culling failed.");
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
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            outcome = "passed", gpu = window.Graphics.GpuName, runtime = Environment.Version.ToString(),
            pipeline = "Machina -> analytic widget geometry + MSDF glyphs -> native Vulkan framebuffer",
            layouts = results, semanticDocumentPreservedByResize = true, syntheticClips = 5000, visibleClips = visible.Length,
            benchmarkFrames = frames, frameMedianMs = times[times.Length / 2], frameP95Ms = times[(int)(times.Length * .95)],
            inputProposalMs = timer.Elapsed.TotalMilliseconds, elapsedSeconds = deadline.Elapsed.TotalSeconds,
            unicodeFallbackExercised = true, physicalPointerActionsTested = false, nonWindowsDevicesTested = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        Finished = true;
    }
    private static SequenceTime T(int seconds) => new(seconds * SequenceTime.TicksPerSecond);
}
