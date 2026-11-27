using Machina.Core.Semantics;
using Machina.Core.Styling;
using Machina.Pipeline;
using Machina.Runtime.Input;

namespace Valencia.App.Editing;

internal sealed partial class EditorWindow
{
    private PointerPoint? pointerPosition;
    internal UiCursor CurrentPointerCursor => PointerCursor();
    internal Machina.Layout.Rows.NodeId? HoveredControl => feedback.Hovered;
    internal List<double> PointerFeedbackMilliseconds { get; } = [];

    internal void VerifyPointerFeedback(string directory)
    {
        if (view.Prepared is not { } prepared || !prepared.TryGetActionCenter(Machina.Core.Actions.UiAction.Named("Open"), out var open))
            throw new InvalidOperationException("Editor Open control is unavailable for native pointer qualification.");
        long revision = Editor.Session.Snapshot.Revision;
        for (int index = 0; index < 4; index++)
        {
            var point = index % 2 == 0 ? open : new PointerPoint(2, (int)chrome.Layout.Height + 2);
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            PointerMove((point.X, point.Y));
            cursor.Set(PointerCursor());
            if (index % 2 == 0 && cursor.NativeShape != Silk.NET.Input.StandardCursor.Hand)
                throw new InvalidOperationException("Editor did not apply the native hand cursor.");
            if (feedbackDirty) RefreshFeedback();
            Graphics.Draw();
            PointerFeedbackMilliseconds.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        PointerMove((open.X, open.Y));
        RefreshFeedback();
        Capture(Path.Combine(directory, "pointer-hover.png"));
        PointerDown((open.X, open.Y));
        PointerUp((2, (int)chrome.Layout.Height + 2));
        if (Editor.Session.Snapshot.Revision != revision)
            throw new InvalidOperationException("Pointer feedback changed project state.");
        if (Editor.Sequence.Tracks.FirstOrDefault(track => !track.Clips.IsEmpty) is { } track)
        {
            var clip = track.Clips[0];
            PointerMove((Editor.Viewport.X(clip.Start) + 1, Editor.Viewport.TrackTop + 20));
            if (PointerCursor() != UiCursor.ResizeHorizontal)
                throw new InvalidOperationException("Native timeline trim cursor is missing.");
        }
    }

    private void RefreshFeedback()
    {
        if (view.Prepared is not { } prepared) return;
        Graphics.UpdateUi(MachinaPointerPresentation.Apply(prepared, feedback,
            ColorToken.Hex(EditorView.Ui.Raise), ColorToken.Hex(EditorView.Ui.Bone)));
        feedbackDirty = false;
    }

    private UiCursor PointerCursor()
    {
        if (palette is not null) return palette.Cursor;
        if (contextMenu is not null) return contextMenu.Cursor;
        if (Gesture.Active) return Gesture.Edge == 0 ? UiCursor.Move : UiCursor.ResizeHorizontal;
        if (Editor.EffectDrag is { } effect) return effect.Operation is Valencia.Core.Editing.SetTrackTransition
            ? UiCursor.ResizeHorizontal : UiCursor.Move;
        if (scrubbing) return UiCursor.Hand;
        if (IsFullscreen)
            return pointerPosition is { } fullscreenPoint && Contains(view.FullscreenSeekBar, fullscreenPoint.X, fullscreenPoint.Y)
                ? UiCursor.Hand : feedback.Cursor;
        if (pointerPosition is { } point && !view.IsCompact)
        {
            if (EffectCursor(point.X, point.Y) is { } effectIntent) return effectIntent;
            if (Editor.Viewport.Cursor(Editor.Sequence, point.X, point.Y, view.Layout.RulerY, razor) is { } intent)
                return intent;
        }
        return feedback.Cursor;
    }
}
