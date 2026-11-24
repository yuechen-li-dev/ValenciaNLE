using Aurelian.Machina;
using Machina.Layout.Geometry;
using Machina.Runtime.Input;
using Silk.NET.Input;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.App.Editing;

internal sealed partial class EditorWindow
{
    private FullscreenWindow? fullscreenWindow;
    private (SourceView? Source, SequenceTime Position, SequenceTime ProgramPosition)? watchReturn;
    private bool fullscreenTogglePending, fullscreenControlsVisible, fullscreenVideoPressed, fullscreenCursorHidden;
    private bool resumeAfterFullscreenScrub, reverseAfterFullscreenScrub;
    private double fullscreenActivity;
    private float fullscreenVolume = 1;
    private bool fullscreenMuted;
    private bool IsFullscreen => fullscreenWindow is not null;
    private float EffectiveMonitorGain => IsFullscreen ? fullscreenMuted ? 0 : fullscreenVolume : 1;

    private void ApplyFullscreenToggle()
    {
        if (!fullscreenTogglePending) return;
        fullscreenTogglePending = false;
        if (EditorOperation.Try("fullscreen", () => { ToggleFullscreen(); return new Completed(); }) is Err<EditorFailure> error)
            ReportFailure(error.Error);
    }

    private void ToggleFullscreen()
    {
        CancelGesture();
        if (fullscreenWindow is { } active)
        {
            var restoredChrome = new AurelianWindowChrome(Graphics.Window);
            try { active.Restore(); }
            catch { restoredChrome.Dispose(); throw; }
            fullscreenWindow = null;
            chrome = restoredChrome;
            view.Chrome = chrome;
            SetFullscreenCursorHidden(false);
            if (watchReturn is { } previous)
            {
                Editor.Source = previous.Source;
                Editor.ProgramPlayhead = previous.ProgramPosition;
                watchReturn = null;
                Seek(previous.Position);
            }
        }
        else
        {
            var window = new FullscreenWindow(WindowHandle);
            chrome.Dispose();
            try { window.Enter(); }
            catch { chrome = new(Graphics.Window); view.Chrome = chrome; throw; }
            fullscreenWindow = window;
            fullscreenControlsVisible = true;
            RevealFullscreenControls();
        }
        view.InvalidatePrepared();
        Graphics.VideoBehindUi = IsFullscreen;
        if (Playback is { } playback) playback.MonitorGain = EffectiveMonitorGain;
        diagnostics.Record(IsFullscreen ? "fullscreen-enter" : "fullscreen-exit", generation, Editor.Playhead.Ticks);
        // Resize realizes the new swapchain and UI before sampling the audio clock for the first frame.
        Prepare();
        Graphics.SynchronizeResize();
    }

    private void PrepareFullscreen(int width, int height)
    {
        bool playing = Playback?.IsPlaying == true || resume && (opening is not null || desired);
        var result = EditorOperation.Try("fullscreen layout", () => view.PrepareFullscreen(Editor, width, height,
            fullscreenControlsVisible, playing, fullscreenVolume, fullscreenMuted));
        if (result is Err<EditorFailure> failure) { ReportFailure(failure.Error); return; }
        if (result is not Ok<Machina.Pipeline.MachinaPreparedPresentation> ready) return;
        Graphics.ProgramMonitor = new(0, 0, width, height);
        Graphics.Canvas = view.Canvas;
        feedback.Refresh(ready.Value.HitTest);
        RefreshFeedback();
    }

    private void RevealFullscreenControls()
    {
        fullscreenActivity = clock.Elapsed.TotalSeconds;
        if (!fullscreenControlsVisible) lastUi = -1;
    }

    private void UpdateFullscreenControls(double now)
    {
        if (!IsFullscreen) return;
        bool visible = Playback?.IsPlaying != true || Busy || scrubbing || fullscreenVideoPressed
            || now - fullscreenActivity < 3
            || pointerPosition is { } point && Contains(view.FullscreenControls, point.X, point.Y);
        if (visible != fullscreenControlsVisible) { fullscreenControlsVisible = visible; lastUi = -1; }
        SetFullscreenCursorHidden(focused && !visible);
    }

    private void SetFullscreenCursorHidden(bool hidden)
    {
        if (hidden == fullscreenCursorHidden) return;
        fullscreenCursorHidden = hidden;
        var mode = hidden ? CursorMode.Hidden : CursorMode.Normal;
        foreach (var mouse in input.Mice)
            if (mouse.Cursor.IsSupported(mode)) mouse.Cursor.CursorMode = mode;
    }

    // Fullscreen keyboard commands act only on the monitor, even on a dirty project.
    private bool HandleFullscreenAction(string name)
    {
        switch (name)
        {
            case "TogglePlay" or "Play" or "Pause" or "PlayReverse": RevealFullscreenControls(); return false;
            case "WatchVideo" or "Open":
                if (job is not null) return true;
                var (playing, reverse) = PlaybackIntent();
                PauseTransport();
                if (Pick(false, "Video files|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v|All files|*.*", "Open video in Valencia") is { } path)
                    RunJob(() => WatchVideo(path));
                else if (playing) StartPlaying(reverse);
                break;
            case "PreviousFrame" or "NextFrame" or "PlayerBack" or "PlayerForward":
                var (keepPlaying, keepReverse) = PlaybackIntent();
                long delta = 5 * SequenceTime.TicksPerSecond;
                Int128 target = (Int128)Editor.Playhead.Ticks + (name is "PreviousFrame" or "PlayerBack" ? -delta : delta);
                Seek(FullscreenPosition(target), keepPlaying, reverse: keepReverse);
                break;
            case "GoToStart" or "GoToEnd":
                var (continuePlaying, continueReverse) = PlaybackIntent();
                Seek(FullscreenPosition(name == "GoToStart" ? 0 : Editor.MonitorDuration.Ticks), continuePlaying, reverse: continueReverse);
                break;
            case "VolumeUp" or "VolumeDown" or "PreviousEdit" or "NextEdit":
                fullscreenVolume = Math.Clamp(fullscreenVolume + (name is "VolumeUp" or "PreviousEdit" ? .05f : -.05f), 0, 1);
                fullscreenMuted = false;
                if (Playback is { } audio) audio.MonitorGain = EffectiveMonitorGain;
                break;
            case "ToggleMute":
                fullscreenMuted = !fullscreenMuted;
                if (Playback is { } output) output.MonitorGain = EffectiveMonitorGain;
                break;
            default: return true;
        }
        RevealFullscreenControls();
        lastUi = -1;
        return true;
    }

    internal async Task WatchVideo(string path)
    {
        var window = fullscreenWindow;
        var imported = await Task.Run(() => MediaAssetProbe.Import(Guid.NewGuid(), path));
        if (window is null || window != fullscreenWindow) return;
        // A player uses the first video and audio streams. Alternate tracks are still available when imported for editing.
        imported = imported with { Asset = imported.Asset with { Streams = [.. imported.Asset.Streams.GroupBy(s => s.Kind).Select(g => g.First())] } };
        var source = CreateSourceView(imported);
        watchReturn ??= (Editor.Source, Editor.Playhead, Editor.ProgramPlayhead);
        ShowSource(source, play: true, useMarks: false);
        Editor.Status = "Playing " + source.Name;
    }

    private (bool Playing, bool Reverse) PlaybackIntent() => opening is not null || desired
        ? (resume, reverseRequested) : (Playback?.IsPlaying == true, Playback?.IsReverse == true);

    private SequenceTime FullscreenPosition(Int128 ticks)
    {
        var clamped = new SequenceTime((long)Int128.Clamp(ticks, 0, Math.Max(0, Editor.MonitorDuration.Ticks - 1)));
        return TimelineTime.SnapToFrame(clamped, Editor.MonitorFrameRate, TimeRounding.Floor);
    }

    private void FullscreenPointerDown((double X, double Y) point)
    {
        RevealFullscreenControls();
        if (Contains(view.FullscreenSeekBar, point.X, point.Y))
        {
            (resumeAfterFullscreenScrub, reverseAfterFullscreenScrub) = PlaybackIntent();
            PauseTransport();
            scrubbing = true;
            CapturePointer();
            UpdateFullscreenScrub(point.X);
        }
        else if (view.Prepared?.HitTest.HitTest(new(point.X, point.Y)) is { } hit) pointer.Press(hit);
        else fullscreenVideoPressed = !Contains(view.FullscreenControls, point.X, point.Y);
    }

    private void UpdateFullscreenScrub(double x)
    {
        var bar = view.FullscreenSeekBar;
        if (bar.Width < 10) return;
        double fraction = Math.Clamp((x - bar.X - 5) / (bar.Width - 10), 0, 1);
        Editor.ScrubTarget = pendingScrub = FullscreenPosition((Int128)(fraction * Editor.MonitorDuration.Ticks));
        lastUi = -1;
    }

    private void FullscreenPointerUp((double X, double Y) point)
    {
        if (scrubbing)
        {
            UpdateFullscreenScrub(point.X);
            var target = Editor.ScrubTarget ?? Editor.Playhead;
            ClearScrub();
            ReleasePointer();
            if (target != Editor.Playhead || resumeAfterFullscreenScrub)
                Seek(target, resumeAfterFullscreenScrub, reverse: reverseAfterFullscreenScrub);
        }
        else if (pointer.Release(view.Prepared?.HitTest.HitTest(new(point.X, point.Y)))?.Name is { } name) Action(name);
        else if (fullscreenVideoPressed && !Contains(view.FullscreenControls, point.X, point.Y)) Action("TogglePlay");
        fullscreenVideoPressed = false;
        RevealFullscreenControls();
        lastUi = -1;
    }

    private static bool Contains(Rect rect, double x, double y) =>
        x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height;
}
