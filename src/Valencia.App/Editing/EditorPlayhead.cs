using Valencia.Rendering;

namespace Valencia.App.Editing;

internal sealed partial class EditorWindow
{
    // Rendered above the timeline geometry without rebuilding its Machina tree.
    private void UpdatePlayheadOverlay()
    {
        var overlay = playheadOverlay;
        overlay.Clear();
        Graphics.CanvasOverlay = overlay;
        if (Settings.IsOpen || view.IsCompact || view.Prepared is null) return;

        var viewport = Editor.Viewport;
        float left = (float)viewport.ClipLeft;
        float right = (float)viewport.ClipRight;
        float top = (float)view.Layout.RulerY;
        float bottom = Math.Max(top, (float)viewport.TrackBottom);
        if (right <= left) return;

        void Add(float x, float y, float width, float height, uint color)
        {
            float from = Math.Max(x, left);
            float to = Math.Min(x + width, right);
            if (to > from && height > 0) overlay.Add(new(from, y, to - from, height, color));
        }

        if (!scrubbing && pointerPosition is { } pointer && pointer.X >= left && pointer.X < right
            && pointer.Y >= top && pointer.Y < viewport.TrackTop)
        {
            float guide = (float)Math.Round(pointer.X);
            Add(guide - 2, top, 5, 3, 0x8A909BA0);
            Add(guide, top + 3, 1, bottom - top - 3, 0x8A909B44);
        }

        float head = (float)Math.Round(viewport.X(Editor.TimelinePlayhead));
        if (head < left || head > right) return;
        uint accent = scrubbing ? EditorView.Ui.Cyan : Editor.Reversing ? EditorView.Ui.Reverse : EditorView.Ui.Orange;
        uint halo = (accent & 0xFFFFFF00) | (scrubbing ? 0x55u : 0x28u);
        Add(left, top + (float)EditorLayout.RulerHeight - 2, head - left, 2, halo);
        Add(head - 3, top + 8, 7, bottom - top - 8, halo);
        Add(head, top + 8, 1, bottom - top - 8, accent);
        Add(head - 7, top, 15, 8, EditorView.Ui.Ink);
        Add(head - 6, top + 1, 13, 6, accent);
        for (int step = 0; step < 5; step++)
            Add(head - 4 + step, top + 7 + step, 9 - 2 * step, 1, accent);
        Add(head - 3, top + 2, 7, 1, 0xFFFFFF9C);
    }
}
