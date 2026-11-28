using Machina.Core.Semantics;
using Machina.Core.Styling;
using Machina.Layout.Rows;
using Machina.Pipeline;
using Machina.Runtime.Input;
using Machina.Standard.Widgets;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Media;
using Valencia.Rendering;
using Ui = Valencia.App.Editing.EditorView.Ui;

namespace Valencia.App.Editing;

internal sealed partial class EditorWindow
{
    private static readonly NodeId TimelineMenu = new("editor.timeline.context");
    private static readonly WidgetStyle ContextStyle = new(ColorToken.Hex(Ui.Panel), ColorToken.Hex(Ui.Bone),
        ColorToken.Hex(Ui.Rule), ColorToken.Hex(Ui.Orange), ColorToken.Hex(Ui.Ink), ColorToken.Hex(Ui.Mute),
        ColorToken.Hex(Ui.Cyan), Height: 30, Gap: 2, Padding: 6);
    private readonly MachinaWidgetPipeline contextPipeline = new();
    private WidgetSession? contextMenu;
    private MachinaPreparedPresentation? contextFrame;
    private PointerPoint contextAnchor;
    private TimeSpan contextOpened;
    private SequenceTime contextTime;
    private bool contextDirty;
    private bool contextAnimating;
    private IReadOnlyList<WidgetOption> contextItems = [];
    private string contextTitle = "Timeline actions";

    private void OpenContextMenu((double X, double Y) point)
    {
        CloseContextMenu();
        if (palette is not null || Settings.IsOpen || IsFullscreen || view.IsCompact || view.Prepared is null || job is not null
            || Gesture.Active || Editor.EffectDrag is not null || scrubbing) return;
        var viewport = Editor.Viewport;
        if (point.X < viewport.ClipLeft && point.Y >= viewport.TrackTop && point.Y < viewport.TrackBottom)
        {
            // Track header: Premiere's track menu (add, delete, delete empty tracks).
            if (TrackMenu(point.Y) is { } items) ShowMenu(point, "Track", items);
            return;
        }
        if (point.X < viewport.ClipLeft || point.X >= viewport.ClipRight
            || point.Y < view.Layout.RulerY || point.Y >= viewport.TrackBottom) return;

        contextTime = new(Math.Clamp(Snap(point.X).Ticks, 0, Editor.Sequence.Duration.Ticks));
        var hit = viewport.Hit(Editor.Sequence, point.X, point.Y);
        Editor.SelectedTitle = null;
        Editor.SelectedClip = hit?.Clip.Id;
        contextItems = hit is { } clip
            ?
            [
                new("Split", "Split at playhead", clip.Track.State.Locked
                    || Editor.TimelinePlayhead.Ticks <= clip.Clip.Start.Ticks || Editor.TimelinePlayhead.Ticks >= clip.Clip.End.Ticks),
                new("Delete", "Delete clip", clip.Track.State.Locked),
                new("RippleDelete", "Ripple delete", clip.Track.State.Locked),
                new("seek", "Move playhead here")
            ]
            : [new("seek", "Move playhead here"), new("marker", "Add marker here"),
                new("CreateAgentCheckpoint", "Checkpoint this revision"), new("ListAgentCheckpoints", "Show project checkpoints"),
                .. ExportCatalog.Presets.Select(preset => new WidgetOption("export-preset/" + preset.Id, "Export " + preset.Name)),
                new("RenderQueueStatus", "Show render queue"), new("RetryRenderJob", "Retry interrupted render"),
                new("CancelRenderJob", "Cancel active render"), new("ProductDiagnostics", "System diagnostics"),
                new("ManageSpeechModel", "Manage speech model")];
        contextItems = [.. contextItems, .. ContextEffects(point.X, point.Y)];
        ShowMenu(point, "Timeline actions", contextItems);
    }

    /// <summary>Opens the shared popup menu at a window point. Choices run through <see cref="Action"/>.</summary>
    private void ShowMenu((double X, double Y) point, string title, IReadOnlyList<WidgetOption> items)
    {
        contextItems = items;
        contextTitle = title;
        pointer.Reset();
        feedbackDirty |= feedback.Clear();
        lastUi = -1;
        contextAnchor = new(point.X, point.Y);
        contextOpened = clock.Elapsed;
        contextAnimating = SystemInformation.IsMenuAnimationEnabled;
        contextDirty = true;
        contextMenu = new();
    }

    private void RefreshContextMenu(TimeSpan? elapsedOverride = null)
    {
        if (contextMenu is null)
        {
            if (contextDirty) Graphics.UpdateOverlay(null);
            contextDirty = false;
            return;
        }
        if (!contextDirty && !contextAnimating && elapsedOverride is null) return;
        TimeSpan? elapsed = elapsedOverride ?? (contextAnimating ? clock.Elapsed - contextOpened : null);
        var size = Graphics.Window.Size;
        if (size.X <= 0 || size.Y <= 0) return;
        contextFrame = contextPipeline.PrepareOverlay(contextMenu,
            widgets => widgets.ContextMenu(TimelineMenu, contextTitle, contextAnchor, contextItems, elapsed: elapsed),
            size.X, size.Y, new(NativeUiText.Instance), style: ContextStyle);
        Graphics.UpdateOverlay(contextFrame.PresentationFrame);
        contextDirty = false;
        if (elapsed >= WidgetBuilder.ContextMenuEntranceDuration) contextAnimating = false;
    }

    private void HandleContextMenu(UiInputEvent inputEvent)
    {
        if (contextMenu is null || inputEvent is UiPointerButtonChanged { Button: UiPointerButton.Secondary }) return;
        if (inputEvent is UiFocusChanged { Focused: false } or UiPointerCaptureLost or UiSurfaceResized)
        {
            CloseContextMenu();
            return;
        }
        if (contextFrame is null) RefreshContextMenu();
        var before = (contextMenu.Hovered, contextMenu.Focused, contextMenu.FocusVisible, contextMenu.HasPointerCapture);
        var result = contextMenu.Handle(inputEvent, clock.Elapsed);
        widgetInput.SetPointerCapture(result.CapturePointer);
        contextDirty |= before != (contextMenu.Hovered, contextMenu.Focused, contextMenu.FocusVisible, contextMenu.HasPointerCapture)
            || inputEvent is UiPointerWheel && result.Handled;
        string? choice = result.Changes.OfType<WidgetSelectionChanged>().FirstOrDefault()?.Keys.SingleOrDefault();
        if (result.Changes.OfType<WidgetOpenChanged>().Any(change => !change.Open)) CloseContextMenu();
        switch (choice)
        {
            case "seek":
                PauseTransport();
                ExitSource();
                Seek(contextTime);
                break;
            case "marker":
                if (Editor.Commit(new SetSequenceMarker(Editor.SequenceId, new SequenceMarker(Guid.NewGuid(), contextTime, "Marker"))).Success)
                    Editor.Status = $"Marker added at {Timecode.Format(contextTime, Editor.Sequence.Video.FrameRate)}.";
                break;
            case { } preset when preset.StartsWith("export-preset/", StringComparison.Ordinal):
                QueueDesktopExport(preset["export-preset/".Length..]);
                break;
            case not null:
                if (!RunContextEffect(choice)) Action(choice);
                break;
        }
    }

    /// <summary>The "+N" tab of a bar: the items that did not fit, as a menu under the pointer.</summary>
    private void OpenBarMenu(IReadOnlyList<(string Action, string Label)> overflow, string title)
    {
        if (overflow.Count == 0 || pointerPosition is not { } at) return;
        CloseContextMenu();
        if (Settings.IsOpen || IsFullscreen || view.IsCompact || job is not null || Gesture.Active || scrubbing) return;
        ShowMenu((at.X, at.Y), title, [.. overflow.Select(item => new WidgetOption(item.Action, item.Label))]);
    }

    private void CloseContextMenu()
    {
        if (contextMenu is null) return;
        widgetInput.SetPointerCapture(false);
        contextMenu.Dispose();
        contextMenu = null;
        contextFrame = null;
        observedNativePointer = null;
        contextDirty = true;
    }

    internal double[] VerifyContextMenu(string directory)
    {
        var original = Editor.Session.Snapshot;
        var viewport = Editor.Viewport;
        var clip = Editor.Sequence.Tracks.First(track => track.Kind == TrackKind.Video).Clips[0];
        var clipPoint = (viewport.X(clip.Start) + 30, viewport.TrackTop + 25);
        OpenContextMenu(clipPoint);
        var preparationMs = new List<double>();
        double PanelY(int milliseconds)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            RefreshContextMenu(TimeSpan.FromMilliseconds(milliseconds));
            preparationMs.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return contextFrame!.Resolved.Nodes[TimelineMenu.Child("popup")].Rect.Y;
        }
        double entered = PanelY(0);
        double midway = PanelY(80);
        double settled = PanelY(160);
        if (!(entered > midway && midway > settled)) throw new InvalidOperationException("Context menu entrance did not ease to its anchor.");

        var delete = contextFrame!.Resolved.Nodes[TimelineMenu.Child("option").Child("Delete")].Rect;
        var center = new PointerPoint(delete.X + delete.Width / 2, delete.Y + delete.Height / 2);
        HandleContextMenu(new UiPointerMoved(center, null, UiModifiers.None));
        RefreshContextMenu();
        if (PointerCursor() != UiCursor.Hand) throw new InvalidOperationException("Hovered menu item did not use the hand cursor.");
        Capture(Path.Combine(directory, "timeline-context-menu.png"));
        HandleContextMenu(new UiKeyChanged(UiKey.Escape, true, false, UiModifiers.None));
        if (contextMenu is not null || !ReferenceEquals(original, Editor.Session.Snapshot))
            throw new InvalidOperationException("Escape did not dismiss the menu without editing.");

        OpenContextMenu((clipPoint.Item1, view.Layout.RulerY + 3));
        RefreshContextMenu(WidgetBuilder.ContextMenuEntranceDuration);
        HandleContextMenu(new UiKeyChanged(UiKey.ArrowDown, true, false, UiModifiers.None));
        HandleContextMenu(new UiKeyChanged(UiKey.Enter, true, false, UiModifiers.None));
        if (contextMenu is not null || Editor.Sequence.Markers.Length != 1 || !Editor.History(false).Success
            || !DocumentSemantics.Equal(original.Document, Editor.Session.Snapshot.Document))
            throw new InvalidOperationException("Context menu did not commit one undoable timeline marker.");
        Editor.SelectedClip = null;
        return [.. preparationMs];
    }
}
