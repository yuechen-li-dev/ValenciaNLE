using Machina.Core.Authoring;
using Machina.Core.Nodes;
using Machina.Core.Styling;
using Machina.Layout.Geometry;
using Machina.Pipeline;
using Valencia.Rendering;

namespace Valencia.App.Editing;

internal sealed partial class EditorView
{
    public Rect FullscreenSeekBar { get; private set; }
    public Rect FullscreenControls { get; private set; }
    public void InvalidatePrepared() => Prepared = null;

    public MachinaPreparedPresentation PrepareFullscreen(EditorDocument editor, int width, int height,
        bool controlsVisible, bool playing, double volume, bool muted)
    {
        var children = new List<UiNode>();
        FullscreenSeekBar = FullscreenControls = default;
        var length = editor.MonitorDuration;
        bool hasMedia = length.Ticks > 0;
        if (!hasMedia)
            children.Add(TextIn("Open a video to start watching", 0, 0, width, height, TextSize.Lg, Ui.Bone, TextAlignX.Center));

        if (controlsVisible && width >= 320 && height >= 140)
        {
            double barWidth = Math.Min(width - 32, 1040), left = (width - barWidth) / 2;
            double top = height - 120;
            FullscreenControls = new(left, top, barWidth, 96);
            Fill(children, FullscreenControls, Ui.Ink);
            Fill(children, new(left, top, barWidth, 1), Ui.Rule);
            double x = left + 14, inner = barWidth - 28;
            var time = editor.ScrubTarget ?? editor.Playhead;
            string title = editor.Source?.Name ?? editor.Sequence.Name;
            string duration = $"{Timecode.Duration(time.TotalSeconds)} / {Timecode.Duration(length.TotalSeconds)}";
            double clockWidth = NativeUiText.Width(duration, TextSize.Sm) + 4;
            children.Add(TextIn(NativeUiText.Ellipsize(title, inner - clockWidth - 8), x, top + 6,
                inner - clockWidth - 8, 24, TextSize.Md));
            children.Add(TextIn(duration, x + inner - clockWidth, top + 6, clockWidth,
                24, TextSize.Sm, Ui.Mute, TextAlignX.Right));

            if (hasMedia)
            {
                FullscreenSeekBar = new(x, top + 30, inner, 24);
                double fraction = Math.Clamp(time.Ticks / (double)length.Ticks, 0, 1);
                uint accent = editor.ScrubTarget is not null ? Ui.Cyan : editor.Reversing ? Ui.Reverse : Ui.Orange;
                double filled = (inner - 10) * fraction;
                Fill(children, new(x + 5, top + 41, inner - 10, 4), Ui.Tick);
                if (filled >= 1) Fill(children, new(x + 5, top + 41, filled, 4), accent);
                Fill(children, new(x + filled, top + 38, 10, 10), accent);
            }

            double y = top + 56, right = x + inner;
            children.Add(Button("Exit", "Fullscreen", right - 58, y, 58, 28, Ui.Outline));
            double openWidth = width < 560 ? 64 : 108;
            children.Add(Button(width < 560 ? "Open" : "Open video", "WatchVideo", right - 64 - openWidth, y, openWidth, 28, Ui.Outline));
            if (hasMedia)
            {
                children.Add(Button(playing ? "Pause" : "Play", "TogglePlay", x, y, 68, 28, Ui.Primary));
                if (barWidth >= 470)
                {
                    children.Add(Button("-5s", "PlayerBack", x + 74, y, 46, 28));
                    children.Add(Button("+5s", "PlayerForward", x + 124, y, 46, 28));
                    children.Add(Button("-", "VolumeDown", x + 182, y, 26, 28));
                    children.Add(Button(muted ? "Muted" : $"{volume:P0}", "ToggleMute", x + 212, y, 64, 28, muted ? Ui.Marked : Ui.Plain));
                    children.Add(Button("+", "VolumeUp", x + 280, y, 26, 28));
                }
                else children.Add(Button(muted ? "Unmute" : "Mute", "ToggleMute", x + 72, y, 60, 28));
            }
        }
        if (editor.Status.StartsWith("Preview error", StringComparison.Ordinal) || editor.Status.StartsWith("job error", StringComparison.Ordinal)
            || editor.Status.StartsWith("ERROR:", StringComparison.Ordinal))
        {
            Fill(children, new(0, 0, width, 34), Ui.Ink);
            children.Add(TextIn(NativeUiText.Ellipsize(editor.Status, Math.Max(1, width - 24)), 12, 0,
                Math.Max(1, width - 24), 34, TextSize.Sm, Ui.Bone));
        }

        var prepared = pipeline.Prepare(UI.Surface(children: children), width, height,
            new Machina.Core.Lowering.UiLoweringOptions(NativeUiText.Instance));
        Prepared = prepared;
        Canvas = [];
        return prepared;
    }
}
