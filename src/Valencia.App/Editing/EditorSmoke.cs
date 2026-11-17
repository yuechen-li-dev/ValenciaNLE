using System.Diagnostics;
using System.Text.Json;
using System.Collections.Immutable;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.App.Editing;

internal sealed class EditorSmoke(string directory)
{
    private readonly Stopwatch deadline = Stopwatch.StartNew();
    private int phase = -2;
    private Task? operation;
    private ProjectDocument? saved;
    private bool capturedPlayback;
    private double largeInputMs;
    private bool benchStarted;
    private SequenceTime exportDuration;
    private readonly Stopwatch playbackElapsed = new();
    public bool Finished { get; private set; }
    public void Tick(EditorWindow window)
    {
        if (deadline.Elapsed.TotalSeconds > 90) throw new TimeoutException("Editor smoke timed out at phase " + phase);
        if (operation is { IsCompleted: true }) { operation.GetAwaiter().GetResult(); operation = null; }
        if (phase == 50) { window.Seek(T(0), true); playbackElapsed.Restart(); phase = 6; return; }
        if (operation is not null || window.Busy) return;
        Directory.CreateDirectory(directory);
        var e = window.Editor;
        switch (phase)
        {
            case -2:
                window.VerifyPointerFeedback(directory);
                saved = e.Session.Snapshot.Document;
                string script = Path.Combine(directory, "native-script.ts");
                File.WriteAllText(script, """
                    using Valencia.Scripting;
                    function run(api: ScriptApi): string {
                        const sequence = api.FirstSequence();
                        const plan = api.Plan(sequence);
                        plan.Move(api.FirstClip(sequence), api.FirstTrack(sequence, "video"), api.Frames("1", 30, 1));
                        api.Apply(plan);
                        api.Undo();
                        api.Redo();
                        api.Undo();
                        return api.Revision();
                    }
                    """);
                operation = window.RunTrustedScript(script);
                phase = -1;
                break;
            case -1:
                Require(e.Status == "Script completed.", e.Status);
                Require(DocumentSemantics.Equal(saved!, e.Session.Snapshot.Document), "Native script undo did not restore the editor document.");
                phase = 0;
                break;
            case 0:
                e.SelectedClip = null; e.Playhead = T(2); Require(e.Split()?.Success == true, "Split both streams failed.");
                Require(e.History(false).Success && e.History(true).Success, "Undo/redo failed.");
                // Linked selection moves the split right-hand video and audio together; drag each track's current right clip once.
                var moved = new HashSet<Guid>();
                foreach (var trackId in e.Sequence.Tracks.Where(t => t.Clips.Length > 1).Select(t => t.Id).ToArray())
                {
                    var movingTrack = e.Sequence.Tracks.First(t => t.Id == trackId);
                    var right = movingTrack.Clips[1]; double dragX = e.Viewport.X(right.Start) + 40;
                    if (!moved.Add(right.Id)) continue;
                    foreach (var linked in EditorEdits.Group(e, right.Id)) moved.Add(linked.Clip.Id);
                    window.Gesture.Begin(right, 0, dragX); window.Gesture.Move(dragX + 180, e.Viewport.TrackTop + (movingTrack.Order - e.Viewport.FirstTrack) * e.Viewport.Row + 20);
                    Require(window.Gesture.Release(), "Drag commit failed: " + e.Status);
                }
                window.Seek(T(3)); phase = 1; break;
            case 1:
                Require(window.Playback?.Current?.Rgba.Where((_, i) => i % 4 < 3).All(v => v == 0) == true, "Gap video is not black.");
                e.Playhead = T(8);
                operation = window.Import(Path.Combine(Path.GetDirectoryName(e.Session.Snapshot.Document.Assets[0].SourcePath)!, "second.mp4"));
                phase = 2; break;
            case 2:
                e.Playhead = T(8); window.Action("Insert");
                Require(e.Sequence.Tracks.SelectMany(t => t.Clips).Any(c => c.AssetId == e.SelectedAsset), "Second source insertion failed: " + e.Status);
                phase = 3; break;
            case 3:
                // Trim the tail of the last video through the same gesture code as the native handles.
                var track = e.Sequence.Tracks.First(t => t.Kind == TrackKind.Video);
                var clip = track.Clips[^1]; double x = e.Viewport.X(clip.End);
                window.Gesture.Begin(clip, 1, x); window.Gesture.Move(x - 30, e.Viewport.TrackTop + 20); Require(window.Gesture.Release(), "Trim failed.");
                var trimmedState = e.Session.Snapshot.Document;
                e.SelectedClip = clip.Id; window.Action("Delete");
                Require(e.Selection is null, "Delete selection failed."); window.Action("Undo");
                Require(DocumentSemantics.Equal(trimmedState, e.Session.Snapshot.Document), "Delete undo failed.");
                e.SelectedClip = null; saved = e.Session.Snapshot.Document;
                operation = window.SaveProject(Path.Combine(directory, "workflow.valencia")); phase = 4; break;
            case 4:
                operation = window.OpenProject(Path.Combine(directory, "workflow.valencia")); phase = 5; break;
            case 5:
                Require(DocumentSemantics.Equal(saved!, e.Session.Snapshot.Document), "Reopened state differs.");
                window.Seek(T(5)); phase = 50; break;
            case 6:
                if (!capturedPlayback && e.Playhead.Ticks >= T(5).Ticks) { window.Capture(Path.Combine(directory, "playing.png")); capturedPlayback = true; }
                if (window.Playback?.IsPlaying == true || e.Playhead.Ticks < e.Sequence.Duration.Ticks) return;
                playbackElapsed.Stop(); window.Capture(Path.Combine(directory, "editor.png")); exportDuration = e.Sequence.Duration;
                operation = SequenceExport.RunAsync(e.Session.Snapshot, e.SequenceId, Path.Combine(directory, "edited.mp4")); phase = 7; break;
            case 7:
                var snapshot = e.Session.Snapshot;
                var seed = e.Sequence.Tracks[0].Clips[0];
                var many = Enumerable.Range(0, 5000).Select(i => seed with { Id = Guid.NewGuid(), Start = T(i * 2), SourceOut = seed.SourceIn.Shift(T(1)) }).ToImmutableArray();
                e.Open(snapshot with { Document = snapshot.Document with { Sequences = [e.Sequence with { Tracks = e.Sequence.Tracks.SetItem(0, e.Sequence.Tracks[0] with { Clips = many }) }] } }, Path.Combine(directory, "synthetic-unsaved.valencia"));
                window.Seek(default); phase = 8; break;
            case 8:
                if (!benchStarted)
                {
                    var latency = Stopwatch.StartNew(); e.Viewport.Scroll(800 * 90); e.Viewport.Zoom(1.2, 700);
                    var visible = e.Viewport.Visible(e.Sequence.Tracks[0]).First(c => e.Viewport.X(c.Start) >= e.Viewport.ClipLeft);
                    window.Gesture.Begin(visible, 0, e.Viewport.X(visible.Start) + 20);
                    window.Gesture.Move(e.Viewport.X(visible.Start) + 24, e.Viewport.TrackTop + 20);
                    Require(window.Gesture.Valid, "Synthetic input proposal failed validation."); window.Gesture.Cancel();
                    largeInputMs = latency.Elapsed.TotalMilliseconds; window.FrameMilliseconds.Clear(); benchStarted = true;
                }
                if (window.FrameMilliseconds.Count < 120) return;
                window.Capture(Path.Combine(directory, "5000-clips.png"));
                Require(window.StaleGenerationsRejected > 0, "No stale seek generation was exercised.");
                File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
                {
                    outcome = window.MaximumAvSkew <= .15 ? "passed" : "failed", gpu = window.Graphics.GpuName, runtime = Environment.Version.ToString(),
                    elapsedSeconds = deadline.Elapsed.TotalSeconds, maximumAvSkewSeconds = window.MaximumAvSkew,
                    pointerFeedbackMilliseconds = window.PointerFeedbackMilliseconds,
                    realPlaybackSeconds = exportDuration.TotalSeconds, exportDurationSeconds = exportDuration.TotalSeconds,
                    playbackElapsedSeconds = playbackElapsed.Elapsed.TotalSeconds,
                    exportDurationTicks = exportDuration.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), seekRequests = window.SeekRequests,
                    staleGenerationsRejected = window.StaleGenerationsRejected, presentedFrames = window.Graphics.PresentedFrames,
                    seekLatencyMs = window.SeekLatenciesMs,
                    uploadedFrames = window.Graphics.UploadedVideoFrames,
                    syntheticFrameMedianMs = window.FrameMilliseconds.Order().ElementAt(window.FrameMilliseconds.Count / 2),
                    syntheticFrameP95Ms = window.FrameMilliseconds.Order().ElementAt((int)(window.FrameMilliseconds.Count * .95)),
                    syntheticClipCount = 5000, inputProposalMs = largeInputMs, renderedBenchmarkFrames = 120,
                    export = Path.Combine(directory, "edited.mp4"), saveReopenSemanticEquality = true,
                    copelandScriptOnLiveSession = true
                }, new JsonSerializerOptions { WriteIndented = true }));
                Require(window.MaximumAvSkew <= .15, $"Editor video/audio skew exceeded 150 ms: {window.MaximumAvSkew:0.000}s. See result.json.");
                PlaybackQualification.Require(window.MaximumAvSkew, window.Graphics.ValidationErrorCount);
                Finished = true; Console.WriteLine("M1b desktop smoke passed: " + directory); break;
        }
    }
    private static SequenceTime T(long seconds) => new(seconds * SequenceTime.TicksPerSecond);
    private static void Require(bool ok, string error) { if (!ok) throw new InvalidOperationException(error); }
}
