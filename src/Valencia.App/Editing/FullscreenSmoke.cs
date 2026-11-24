using System.Diagnostics;
using System.Text.Json;
using Machina.Core.Actions;
using Machina.Standard.Windowing;
using Silk.NET.Input;
using Silk.NET.Maths;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.App.Editing;

internal sealed partial class EditorWindow
{
    private sealed class FullscreenSmoke(string directory)
    {
        private readonly Stopwatch deadline = Stopwatch.StartNew();
        private int phase;
        private nint handle;
        private Vector2D<int> size, position;
        private SequencePlayback? transport;
        private ProjectSnapshot? snapshot;
        private SourceView? editorSource;
        private SequenceTime sourcePosition, dragTarget;
        private Task? watch;
        public bool Finished { get; private set; }

        public void Tick(EditorWindow window)
        {
            if (deadline.Elapsed.TotalSeconds > 35) throw new TimeoutException($"Fullscreen smoke timed out at phase {phase}.");
            if (window.Busy || watch?.IsCompleted == false) return;
            var editor = window.Editor;
            var native = window.Graphics.Window;
            switch (phase)
            {
                case 0:
                    if (window.view.Prepared is null) return;
                    handle = window.WindowHandle;
                    size = native.Size;
                    position = native.Position;
                    snapshot = editor.Session.Snapshot;
                    window.keys.Replace(EditorShortcuts.Default());
                    window.Action("Play");
                    break;
                case 1:
                    if (window.Playback is not { IsPlaying: true } player || player.Position.TotalSeconds < .2) return;
                    transport = player;
                    Click(window, "Fullscreen");
                    break;
                case 2:
                    var screen = Screen.FromHandle(handle).Bounds;
                    Require(window.IsFullscreen && window.WindowHandle == handle && native.Size == new Vector2D<int>(screen.Width, screen.Height)
                        && native.Position == new Vector2D<int>(screen.X, screen.Y), $"Window {native.Position}/{native.Size} did not fill {screen}.");
                    Require(window.Playback == transport && transport!.IsPlaying, "Fullscreen restarted playback.");
                    window.Action("Delete");
                    Require(editor.Session.Snapshot == snapshot, "Fullscreen allowed an editing command.");
                    Directory.CreateDirectory(directory);
                    window.Capture(Path.Combine(directory, "controls.png"));
                    window.PointerMove((50, 50));
                    break;
                case 3:
                    if (window.fullscreenControlsVisible) return;
                    Require(window.fullscreenCursorHidden && transport!.IsPlaying, "Fullscreen controls did not hide during playback.");
                    window.Capture(Path.Combine(directory, "video.png"));
                    window.PointerMove((50, 60));
                    break;
                case 4:
                    var bar = window.view.FullscreenSeekBar;
                    Require(bar.Width > 0, "Pointer movement did not restore the seek bar.");
                    double y = bar.Y + bar.Height / 2;
                    window.PointerDown((bar.X + bar.Width * .6, y));
                    var target = (bar.X + bar.Width * .3, y);
                    window.PointerMove(target);
                    dragTarget = editor.ScrubTarget ?? throw new InvalidOperationException("The scrubber did not track the drag.");
                    window.PointerUp(target);
                    break;
                case 5:
                    Require(window.Playback is { IsPlaying: true } resumed && resumed.Start == dragTarget, "Fullscreen drag failed to seek and resume.");
                    Click(window, "ToggleMute");
                    Require(window.Playback!.MonitorGain == 0, "Mute did not change monitor audio gain.");
                    transport = window.Playback;
                    Press(window, Key.F11);
                    break;
                case 6:
                    Require(!window.IsFullscreen && native.Size == size && native.Position == position, "Windowed placement was not restored.");
                    Require(window.Playback == transport && transport!.IsPlaying && transport.MonitorGain == 1, "Leaving fullscreen restarted or muted editor playback.");
                    window.Action("Pause");
                    window.Action("MonitorSource");
                    window.chrome.Invoke(WindowCaptionButton.Maximize);
                    break;
                case 7:
                    if (!window.chrome.IsMaximized) return;
                    editorSource = editor.Source ?? throw new InvalidOperationException("Source monitor did not open.");
                    sourcePosition = editor.Playhead;
                    Press(window, Key.F11);
                    break;
                case 8:
                    Require(window.IsFullscreen && editor.Source == editorSource, "Fullscreen changed the active source.");
                    watch = window.WatchVideo(editorSource!.Imported.Asset.SourcePath);
                    break;
                case 9:
                    watch!.GetAwaiter().GetResult();
                    Require(window.Playback is { IsPlaying: true, Current: not null } && editor.Source?.AssetId != editorSource!.AssetId,
                        "Direct video did not start playing in the fullscreen source monitor.");
                    Require(editor.Session.Snapshot == snapshot, "Watching a video edited the project.");
                    Press(window, Key.Escape);
                    break;
                case 10:
                    Require(!window.IsFullscreen && window.chrome.IsMaximized && editor.Source == editorSource && editor.Playhead == sourcePosition,
                        "Escape did not restore the maximized editor and its source position.");
                    Require(editor.Session.Snapshot == snapshot && window.InputFailures == 0, "Fullscreen changed the document or failed input.");
                    PlaybackQualification.Require(window.MaximumAvSkew, window.Graphics.ValidationErrorCount);
                    File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
                    {
                        outcome = "passed", gpu = window.Graphics.GpuName, windowAndDecodeSessionReused = true,
                        normalAndMaximizedRestored = true, detachedSourceRestored = true, controlsAutoHide = true,
                        scrubResumedAudio = true, projectUnchanged = true, validationAvailable = window.Graphics.ValidationAvailable,
                        validationErrors = window.Graphics.ValidationErrorCount, maximumAvSkewSeconds = window.MaximumAvSkew
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    Finished = true;
                    break;
            }
            phase++;
        }

        private static void Click(EditorWindow window, string action)
        {
            if (!window.Presentation.TryGetActionCenter(UiAction.Named(action), out var point))
                throw new InvalidOperationException($"Cannot find {action} in the active presentation.");
            window.PointerDown((point.X, point.Y));
            window.PointerUp((point.X, point.Y));
        }

        private static void Press(EditorWindow window, Key key)
        {
            window.keys.Record(key, true);
            window.keys.Record(key, false);
        }

        private static void Require(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
        }
    }
}
