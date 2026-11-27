using System.Diagnostics;
using System.Text.Json;
using Machina.Core.Actions;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.App.Editing;

internal sealed partial class EditorWindow
{
    private sealed class EffectControlsSmoke(string directory)
    {
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private ProjectSnapshot? baseline;
        private Task<ExportResult>? export;
        private ReadOnlyMemory<byte> firstHoldPicture;
        private long exportedFrames;
        private int phase;
        public bool Finished { get; private set; }

        public void Tick(EditorWindow window)
        {
            if (elapsed.Elapsed.TotalSeconds > 120) throw new TimeoutException($"Effect controls smoke stalled at phase {phase}.");
            if (window.Busy) return;
            var editor = window.Editor;
            var sequence = editor.Sequence;
            var viewport = editor.Viewport;
            SequenceTime Frame(long number) => TimelineTime.FromFrames(number, sequence.Video.FrameRate);
            switch (phase)
            {
                case 0:
                    Directory.CreateDirectory(directory);
                    baseline = editor.Session.Snapshot;
                    window.chrome.Invoke(Machina.Standard.Windowing.WindowCaptionButton.Maximize);
                    window.Action("ZoomFit");
                    window.Seek(Frame(115));
                    break;
                case 1:
                {
                    var title = sequence.Titles.Single();
                    window.Capture(Path.Combine(directory, "before.png"));
                    var picture = EditorEffects.Picture(editor, window.view.Layout.Monitor);
                    var titleClip = sequence.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == title.Id);
                    var anchor = EditorEffects.Anchor(new(titleClip, Frame(115)), picture);
                    var drop = (anchor.X + picture.Width * .12, anchor.Y + picture.Height * .155);
                    window.PointerDown(anchor);
                    window.PointerMove(drop);
                    Require(ReferenceEquals(baseline, editor.Session.Snapshot), "Title drag changed the project before release.");
                    window.Action("Cancel");
                    window.PointerUp(drop);
                    Require(ReferenceEquals(baseline, editor.Session.Snapshot), "Cancelled title drag changed the project.");
                    Drag(window, anchor, drop);
                    Require(editor.Session.Snapshot.Revision == baseline!.Revision + 1, "Title drag must commit exactly once.");
                    var after = editor.Sequence.Titles.Single().Transform!.At(Frame(22).Ticks);
                    Require(after.X == .62 && after.Y == .23, "Title handle did not write the new position at the existing key.");
                    break;
                }
                case 2:
                {
                    window.Capture(Path.Combine(directory, "inspector.png"));
                    Click(window, "fx:next");
                    Require(editor.Playhead == Frame(145), "Next key did not seek to the next title key.");
                    break;
                }
                case 3:
                {
                    var first = sequence.Tracks[0].Transitions.Single();
                    double y = viewport.TrackTop + 27;
                    Drag(window, (viewport.X(Frame(142)), y), (viewport.X(Frame(146)), y));
                    var result = editor.Sequence.Tracks[0].Transitions.Single();
                    Require(result.Id == first.Id && result.BeforeTicks == Frame(4).Ticks && result.AfterTicks == first.AfterTicks,
                        "Dragging the dissolve edge changed the cut or missed the target frame.");
                    break;
                }
                case 4:
                {
                    int trackIndex = sequence.Tracks.IndexOf(sequence.Tracks.Single(track => track.Name == "Music"));
                    viewport.FirstTrack = Math.Max(0, trackIndex - viewport.VisibleTracks + 1);
                    window.Prepare();
                    var track = sequence.Tracks[trackIndex];
                    double rowTop = viewport.TrackTop + (trackIndex - viewport.FirstTrack) * viewport.Row;
                    double y = EditorEffects.GainY(rowTop + 36, viewport.Row - 43, -9);
                    Drag(window, (viewport.X(Frame(150)), y), (viewport.X(Frame(155)), y));
                    var points = EditorEffects.Audio(editor.Sequence, track.Id)!.Envelope;
                    Require(points.Any(p => p.Time == Frame(155) && p.GainDb == -9) && !points.Any(p => p.Time == Frame(150)),
                        "Dragging gain did not move the music duck.");
                    Require(editor.Session.Snapshot.Revision == baseline!.Revision + 3, "Expected one undo entry per completed gesture.");
                    var after = editor.Session.Snapshot.Document;
                    for (int i = 0; i < 3; i++) Require(editor.History(false).Success, "Undo failed.");
                    Require(DocumentSemantics.Equal(baseline.Document, editor.Session.Snapshot.Document), "Undo restored different edits.");
                    for (int i = 0; i < 3; i++) Require(editor.History(true).Success, "Redo failed.");
                    Require(DocumentSemantics.Equal(after, editor.Session.Snapshot.Document), "Redo restored different edits.");
                    window.Seek(Frame(115));
                    break;
                }
                case 5:
                    window.Capture(Path.Combine(directory, "after.png"));
                    export = ExportAsync(window);
                    break;
                case 6:
                {
                    if (!export!.IsCompleted) return;
                    exportedFrames = export.GetAwaiter().GetResult().Frames;
                    var video = sequence.Tracks[0];
                    Require(editor.Commit(new Valencia.Core.Editing.SetTrackTransition(sequence.Id, video.Id, video.Transitions.Single().Id, null)).Success,
                        "The temporary retime project could not clear its dissolve.");
                    editor.SelectedTitle = null;
                    editor.SelectedClip = video.Clips[0].Id;
                    window.Seek(Frame(40));
                    break;
                }
                case 7:
                    window.Prepare();
                    Click(window, "fx:double");
                    Require(editor.Selection!.End == Frame(75), "The 2x inspector button did not halve picture duration.");
                    break;
                case 8:
                    using (var original = new SequenceEvaluator(baseline!, editor.SequenceId))
                    {
                        Require(window.Playback!.Current!.SourceTimestamps!.Value.SequenceEqual(original.Video(Frame(80), default).SourceTimestamps!.Value),
                            "Native 2x preview did not show the expected source timestamp.");
                    }
                    editor.SelectedClip = sequence.Tracks[0].Clips[1].Id;
                    window.Seek(Frame(230));
                    break;
                case 9:
                    window.Prepare();
                    Click(window, "fx:freeze");
                    Require(editor.Selection!.Retime?.Hold is not null && editor.Selection.End == Frame(360),
                        "Freeze did not hold the playhead frame for the selected clip duration.");
                    window.Seek(Frame(165));
                    break;
                case 10:
                    firstHoldPicture = window.Playback!.Current!.Rgba;
                    window.Seek(Frame(345));
                    break;
                case 11:
                    Require(firstHoldPicture.Span.SequenceEqual(window.Playback!.Current!.Rgba), "A later native seek changed the held picture.");
                    Require(window.InputFailures == 0, "Native input handled an exception.");
                    File.WriteAllText(Path.Combine(directory, "native.json"), JsonSerializer.Serialize(new
                    {
                        outcome = "passed", gpu = window.Graphics.GpuName, sequenceId = editor.SequenceId,
                        cancelledGestures = 1, edits = 3, undo = 3, redo = 3, window.InputFailures,
                        titleFrame = 115, titleX = .62, titleY = .23, dissolveStart = 146, musicDuckFrame = 155,
                        frames = exportedFrames, nativeRetimePreview = true
                    }));
                    Finished = true;
                    break;
            }
            phase++;
        }

        private async Task<ExportResult> ExportAsync(EditorWindow window)
        {
            var editor = window.Editor;
            var after = editor.Session.Snapshot.Document;
            string project = Path.Combine(directory, "native.valencia");
            await window.files.SaveAsync(project, editor.Session);
            var reopened = await window.files.LoadAsync(project);
            Require(DocumentSemantics.Equal(after, reopened.Snapshot.Document), "The saved project reopened with different edits.");
            var profile = editor.Sequence.Creator!.Profiles.Single(p => p.Name == "BBB-portrait");
            return await SequenceExport.RunAsync(reopened.Snapshot, editor.SequenceId, Path.Combine(directory, "native.mp4"), profile: profile);
        }

        private static void Drag(EditorWindow window, (double X, double Y) start, (double X, double Y) end)
        {
            var snapshot = window.Editor.Session.Snapshot;
            window.PointerDown(start);
            window.PointerMove(((start.X + end.X) / 2, (start.Y + end.Y) / 2));
            window.PointerMove(end);
            Require(ReferenceEquals(snapshot, window.Editor.Session.Snapshot), "Moving the pointer committed an edit.");
            window.PointerUp(end);
        }

        private static void Click(EditorWindow window, string action)
        {
            Require(window.Presentation.TryGetActionCenter(UiAction.Named(action), out var center), $"Missing native control {action}.");
            window.PointerDown((center.X, center.Y));
            window.PointerUp((center.X, center.Y));
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
