using System.Globalization;
using Machina.Core.Actions;
using Machina.Core.Authoring;
using Machina.Core.Nodes;
using Machina.Core.Styling;
using Machina.Layout.Geometry;
using Machina.Layout.Rows;
using Machina.Pipeline;
using Machina.Standard.Authoring;
using Machina.Standard.Theme;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Rendering;
using Aurelian.Machina;
using Machina.Standard.Windowing;

namespace Valencia.App.Editing;

/// <summary>
/// Native editor declaration in the Edge Code style. Layout slots come from <see cref="EditorLayout"/>; this class only
/// fills them. Everything is rects, 1 px rules and MSDF text: no gradients, blur, shadows or rotation. All strings are ASCII.
/// </summary>
internal sealed partial class EditorView
{
    private readonly MachinaPresentationPipeline pipeline = new();
    public AurelianWindowChrome? Chrome { get; set; }
    public MachinaPreparedPresentation? Prepared { get; private set; }
    public IReadOnlyList<CanvasRectangle> Canvas { get; private set; } = [];
    public int VisibleClips { get; private set; }
    public EditorLayout Layout { get; private set; } = EditorLayout.For(1280, 720);
    /// <summary>True when the monitor shows a title card or the empty state instead of the picture.</summary>
    public bool HideVideo { get; private set; }
    public bool IsCompact { get; private set; }

    /// <summary>Edge Code palette and button styles.</summary>
    internal static class Ui
    {
        // Surfaces
        public const uint Ink = 0x0B0C0EFF, Panel = 0x121418FF, Raise = 0x1A1D22FF, Rule = 0x2A2E35FF, RowRule = 0x1F2329FF;
        public const uint Track = 0x15171BFF, Monitor = 0x000000FF, Tick = 0x4A505BFF;
        // Text
        public const uint Bone = 0xEDE7DAFF, Mute = 0x8A909BFF, Edge = 0x7A808BFF, Paper = 0xE6E0D2FF, Card = 0xF5F1E8FF, Wave = 0x2B2F36FF;
        // Signals: orange is "now" (playhead, selection, active, record); ultramarine is picture and In/Out;
        // cyan is snapping and drag ghosts; alert is destructive, lock and removals.
        public const uint Orange = 0xFF5A1FFF, CardOrange = 0xE8440AFF, Ultramarine = 0x2E4BFFFF, Cyan = 0x39E1FFFF, Alert = 0xFF3B4AFF;
        public const uint Reverse = 0xC3A3FFFF;
        public const uint Removal = 0xFF3B4A59, Ghost = 0x39E1FFB0, GhostInvalid = 0xFF3B4AB0, Backing = 0x0B0C0EC8;

        private static StandardButtonStyle Style(uint? background, uint foreground, uint? border = null, TextSize size = TextSize.Md) =>
            new(background is { } b ? ColorToken.Hex(b) : null, ColorToken.Hex(foreground), border is { } c ? ColorToken.Hex(c) : null, border is null ? 0 : 1,
                new TextStyle(ColorToken.Hex(foreground), size, TextAlignX.Center, TextAlignY.Center), 112, 32);
        public static readonly StandardButtonStyle Plain = Style(Raise, Bone), Dim = Style(Raise, Mute), Outline = Style(null, Bone, Rule),
            Primary = Style(Bone, Ink), Active = Style(Orange, Ink), Marked = Style(Ultramarine, Bone), Danger = Style(Alert, Ink),
            Tab = Style(null, Mute), TabOn = Style(null, Bone), Row = Style(null, Bone), RowOn = Style(Raise, Bone),
            Word = Style(null, Bone, size: TextSize.Sm), WordSelected = Style(Orange, Ink, size: TextSize.Sm), WordSpeaking = Style(Ultramarine, Bone, size: TextSize.Sm),
            TrackVideo = Style(Ultramarine, Bone, size: TextSize.Xl), TrackAudio = Style(Raise, Bone, size: TextSize.Xl), TrackTarget = Style(Orange, Ink, size: TextSize.Xl),
            CardApply = Style(Ink, Card), CardCancel = Style(null, Ink, Ink);
    }

    private enum PanelTab { Media, Transcript, Record, Controls }
    private static readonly string[] TabNames = ["Media", "Transcript", "Record", "Controls"];

    public Result<MachinaPreparedPresentation, EditorFailure> TryPrepare(EditorDocument editor,
        TimelineGesture gesture, bool playing, bool razor, int width, int height, bool jobRunning = false)
    {
        if (width <= 0 || height <= 0)
        {
            return Result<MachinaPreparedPresentation, EditorFailure>.Failure(
                new("layout", "Window has no drawable client area.", false, null));
        }
        int firstTrack = editor.Viewport.FirstTrack;
        int visibleClips = VisibleClips;
        var result = EditorOperation.Try("layout", () => Prepare(editor, gesture, playing, razor, width, height, jobRunning));
        if (result.Case is Err<EditorFailure>)
        {
            editor.Viewport.Configure(Layout, editor.Sequence.Tracks.Length);
            editor.Viewport.FirstTrack = firstTrack;
            VisibleClips = visibleClips;
        }
        return result;
    }

    public MachinaPreparedPresentation Prepare(EditorDocument editor, TimelineGesture gesture, bool playing, bool razor,
        int width = 1280, int height = 720, bool jobRunning = false)
    {
        var layout = EditorLayout.For(width, height, Chrome is null ? 0 : (int)Chrome.Layout.Height);
        // GLFW may report a tiny client area during minimize/live resize. Such a
        // window cannot contain the editing controls. Do not construct negative
        // button widths or change the timeline viewport while it is unusable.
        if (width < 320 || height < 480)
        {
            var compact = pipeline.Prepare(UI.Surface(color: ColorToken.Hex(Ui.Ink), children:
                [Text("Enlarge window to edit", 0, 0, Math.Max(1, width), 24)]),
                width, height, new Machina.Core.Lowering.UiLoweringOptions(NativeUiText.Instance));
            Layout = layout;
            IsCompact = true;
            HideVideo = true;
            VisibleClips = 0;
            Canvas = [];
            Prepared = compact;
            return compact;
        }
        var sequence = editor.Sequence;
        var viewport = editor.Viewport;
        viewport.Configure(layout, sequence.Tracks.Length);
        viewport.FirstTrack = Math.Clamp(viewport.FirstTrack, 0, Math.Max(0, sequence.Tracks.Length - viewport.VisibleTracks));
        var children = new List<UiNode>();
        var quads = new List<CanvasRectangle>();
        if (Chrome is { } chrome)
            children.Add(StandardUI.Place(WindowTitleBar.Create("window.titlebar", chrome.Layout,
                "Valencia  /  " + (editor.Dirty ? "* " : "") + Path.GetFileName(editor.ProjectPath ?? "Untitled.valencia"),
                chrome.IsMaximized, chrome.Hovered, chrome.Pressed,
                ColorToken.Hex(Ui.Panel), ColorToken.Hex(Ui.Bone)), 0, 0, width, chrome.Layout.Height));

        var headerChildren = new List<UiNode>();
        Header(headerChildren, editor, layout, jobRunning);
        if (layout.Header.Y == 0) children.AddRange(headerChildren);
        else children.Add(StandardUI.Frame(0, layout.Header.Y, width, layout.Header.Height, headerChildren));
        // Fills that sit under widgets must be widgets too: canvas quads draw above widget geometry (and above video).
        Fill(children, layout.Panel, Ui.Panel);
        children.Add(StandardUI.Frame(layout.Panel.X, layout.Panel.Y, layout.Panel.Width, layout.Panel.Height,
            Panel(editor, layout.Panel, layout.Profile == EditorLayoutProfile.VerticalPhone, !layout.HasInspector)));
        Fill(children, layout.Monitor, Ui.Monitor);
        bool hideVideo = Monitor(children, quads, editor, layout);
        if (!hideVideo && !playing) DrawTransformHandles(quads, editor, layout.Monitor);
        Transport(children, editor, layout, playing);
        if (layout.HasInspector)
        {
            Fill(children, layout.Inspector, Ui.Panel);
            children.Add(StandardUI.Frame(layout.Inspector.X, layout.Inspector.Y, layout.Inspector.Width, layout.Inspector.Height, Inspector(editor, layout.Inspector)));
        }
        Toolbar(children, editor, layout, razor);
        Timeline(children, quads, editor, gesture, layout);
        Status(children, editor, layout);

        var prepared = pipeline.Prepare(UI.Surface(color: ColorToken.Hex(Ui.Ink), children: children), width, height,
            new Machina.Core.Lowering.UiLoweringOptions(NativeUiText.Instance));
        Layout = layout;
        IsCompact = false;
        HideVideo = hideVideo;
        Canvas = quads;
        Prepared = prepared;
        return prepared;
    }

    // ---------------------------------------------------------------- header

    private static void Header(List<UiNode> children, EditorDocument editor, EditorLayout layout, bool jobRunning)
    {
        int width = layout.Width; double rowH = EditorLayout.HeaderRowHeight;
        bool two = layout.HeaderRows == 2;
        double mark = NativeUiText.Width("VALENCIA", TextSize.Xl);
        children.Add(TextIn("VALENCIA", 14, 0, mark + 4, rowH, TextSize.Xl));
        if (!two && 14 + mark + 10 + NativeUiText.Width("NLE", TextSize.Xs) < EditorLayout.HeaderCommandsLeft - 8)
            children.Add(TextIn("NLE", 14 + mark + 10, 2, 40, rowH, TextSize.Xs, Ui.Mute));
        var commands = new[] { ("New", "New"), ("Open", "Open"), ("Save", "Save"), ("Save as", "SaveAs"), ("Import", "Import"), ("Export", "Export"), ("Settings", "Settings") };
        double gap = EditorLayout.HeaderCommandGap;
        double x = two ? Gap : EditorLayout.HeaderCommandsLeft, y = two ? rowH + 4 : 6;
        double cell = two ? (width - 2 * Gap - (commands.Length - 1) * gap) / commands.Length : EditorLayout.HeaderCommandWidth;
        foreach (var (label, action) in commands)
        {
            children.Add(Button(label, action, x, y, cell, 28, action == "Export" ? Ui.Primary : Ui.Outline)); x += cell + gap;
        }
        children.Add(Box(0, layout.Header.Height - 1, width, 1, Ui.Rule));

        double right = width - 14;
        if (jobRunning) { children.Add(Button("Cancel job", "Cancel", right - 112, 6, 112, 28, Ui.Danger)); right -= 122; }
        double left = two ? 14 + mark + 20 : x + 10;
        var sequence = editor.Sequence;
        if (editor.Session.Snapshot.Document.Sequences.Length > 1 && right - left >= 86)
        {
            double switchWidth = Math.Min(140, right - left);
            string name = NativeUiText.Ellipsize(sequence.Name, switchWidth - 18, TextSize.Md);
            children.Add(Button(name, "NextSequence", right - switchWidth, 6, switchWidth, 28, Ui.Outline, caps: false));
            right -= switchWidth + Gap;
        }
        string meta = $"REV {editor.Session.Snapshot.Revision:0000} / {sequence.Video.Width}x{sequence.Video.Height} / {Fps(sequence.Video.FrameRate)}P";
        double metaWidth = NativeUiText.Width(meta, TextSize.Xs);
        if (right - metaWidth - 180 >= left) { children.Add(TextIn(meta, right - metaWidth, 0, metaWidth, rowH, TextSize.Xs, Ui.Mute)); right -= metaWidth + 18; }
        string project = Path.GetFileName(editor.ProjectPath ?? "Untitled.valencia");
        double nameWidth = Math.Min(NativeUiText.Width(project) + 2, right - left - 14);
        if (nameWidth > 48)
        {
            children.Add(TextIn(NativeUiText.Ellipsize(project, nameWidth), right - nameWidth, 0, nameWidth, rowH, TextSize.Sm, Ui.Bone, TextAlignX.Right));
            // Unsaved changes: a solid orange pip before the project name.
            if (editor.Dirty) children.Add(Box(right - nameWidth - 13, rowH / 2 - 4, 7, 7, Ui.Orange));
        }
    }

    // ---------------------------------------------------------------- monitor

    /// <summary>Monitor overlays. Returns true when the picture is replaced by a title card or the empty state.</summary>
    private static bool Monitor(List<UiNode> children, List<CanvasRectangle> quads, EditorDocument editor, EditorLayout layout)
    {
        var m = layout.Monitor; var sequence = editor.Sequence;
        double pad = Math.Clamp(Math.Round(m.Width * .05), 12, 44);
        if (editor.Card is { } card) { TitleCard(children, editor, card, m, pad); return true; }
        if (editor.Source is null && sequence.Tracks.All(t => t.Clips.IsEmpty)) { NoSignal(children, editor, m, pad); return true; }
        if (m.Width < 120 || m.Height < 60) return false;

        // Canvas quads draw above the video, text above both.
        bool source = editor.Source is not null;
        string badge = source ? "SRC" : "PGM";
        double badgeWidth = NativeUiText.Width(badge, TextSize.Md) + 14, bx = m.X + 10, by = m.Y + 10;
        quads.Add(new((float)bx, (float)by, (float)badgeWidth, 18, source ? Ui.Ultramarine : Ui.Orange));
        children.Add(TextIn(badge, bx, by, badgeWidth, 18, TextSize.Md, source ? Ui.Bone : Ui.Ink, TextAlignX.Center));
        if (editor.Recording)
        {
            double rx = bx + badgeWidth + 4, rw = NativeUiText.Width("REC", TextSize.Md) + 14;
            quads.Add(new((float)rx, (float)by, (float)rw, 18, Ui.Alert));
            children.Add(TextIn("REC", rx, by, rw, 18, TextSize.Md, Ui.Ink, TextAlignX.Center));
        }
        string label = source
            ? "SOURCE / " + Timecode.Format(editor.Source!.Duration, sequence.Video.FrameRate)
            : Aspect(sequence.Video.Width, sequence.Video.Height) + " / " + (ProfileName(editor) ?? "SEQUENCE").ToUpperInvariant();
        double labelWidth = Math.Min(NativeUiText.Width(label, TextSize.Xs) + 12, m.Width - badgeWidth - 80);
        if (labelWidth > 60)
        {
            double lx = m.X + m.Width - 10 - labelWidth;
            quads.Add(new((float)lx, (float)by, (float)labelWidth, 18, Ui.Backing));
            children.Add(TextIn(NativeUiText.Ellipsize(label, labelWidth - 12, TextSize.Xs), lx, by, labelWidth, 18, TextSize.Xs, Ui.Mute, TextAlignX.Center));
        }
        // Registration marks in the lower corners.
        double plus = NativeUiText.Width("+");
        children.Add(TextIn("+", m.X + 6, m.Y + m.Height - 22, plus + 2, 18, TextSize.Sm, Ui.Edge));
        children.Add(TextIn("+", m.X + m.Width - 8 - plus, m.Y + m.Height - 22, plus + 2, 18, TextSize.Sm, Ui.Edge));
        return false;
    }

    private static void NoSignal(List<UiNode> children, EditorDocument editor, Rect m, double pad)
    {
        var sequence = editor.Sequence; double inner = m.Width - 2 * pad, x = m.X + pad;
        const string head = "NO SIGNAL.", body = "Drop a recording here, press Ctrl+I, or start OBS from the Record tab.";
        var size = Fit(head, TextSize.H2, inner, m.Height * .45);
        double headHeight = Line(head, size), bodyHeight = Line(body, TextSize.Lg);
        bool showBody = m.Height >= headHeight + bodyHeight + 60;
        double y = m.Y + (m.Height - headHeight - (showBody ? bodyHeight + 8 : 0)) / 2;
        children.Add(Text(head, x, y, inner, headHeight, size, Ui.Card));
        if (showBody) children.Add(Text(NativeUiText.Ellipsize(body, inner, TextSize.Lg), x, y + headHeight + 8, inner, bodyHeight, TextSize.Lg, Ui.Card));
        if (m.Height >= headHeight + 70)
        {
            string code = $"VLN / {sequence.Name.ToUpperInvariant()} / {sequence.Video.Width}x{sequence.Video.Height} / {Fps(sequence.Video.FrameRate)}P / {Timecode.Format(default, sequence.Video.FrameRate)}";
            double top = m.Y + Math.Min(pad * .7, 30);
            children.Add(Text(NativeUiText.Ellipsize(code, inner - 80, TextSize.Xs), x, top, inner - 80, 14, TextSize.Xs, Ui.Edge));
            if (inner > 160) children.Add(Box(m.X + m.Width - pad - 60, top + 3, 60, 8, Ui.Orange));
        }
    }

    /// <summary>Full-monitor title cards: export complete, recording started, proposal ready (inverted).</summary>
    private static void TitleCard(List<UiNode> children, EditorDocument editor, TitleCard card, Rect m, double pad)
    {
        bool paper = card.Kind == TitleCardKind.Proposal;
        uint fg = paper ? Ui.Ink : Ui.Card;
        if (paper) children.Add(Box(m.X, m.Y, m.Width, m.Height, Ui.Card));
        double inner = m.Width - 2 * pad, x = m.X + pad, y = m.Y + Math.Min(pad * .7, 30);
        double bottom = m.Y + m.Height - Math.Min(pad * .7, 28);
        bool recording = card.Kind == TitleCardKind.Recording;
        var headSize = Fit(card.Head, TextSize.H2, recording ? inner * .4 : inner, m.Height * .42);
        var subSize = Smaller(headSize);
        double headHeight = Line(card.Head, headSize);

        // Bottom strip first so the body knows how much room is left.
        double dataHeight = Line("A", TextSize.Xs) + 2, stripTop = bottom - Math.Max(paper ? 30 : 0, card.Data.Count * dataHeight);
        if (paper)
        {
            double apply = NativeUiText.Width("APPLY", TextSize.Md) + 28, cancel = NativeUiText.Width("CANCEL  ESC", TextSize.Md) + 28;
            string applyAction = editor.HasAgentProposal ? "ApplyAgentPlan" : editor.ProposedCut is not null ? "ApplyCut" : "ApplyTextCut";
            string cancelAction = editor.HasAgentProposal ? "RejectAgentPlan" : editor.ProposedCut is not null ? "CancelCut" : "CancelTextCut";
            children.Add(Button("Apply", applyAction, x, bottom - 30, apply, 30, Ui.CardApply, key: "card"));
            children.Add(Button("Cancel  Esc", cancelAction, x + apply + 8, bottom - 30, cancel, 30, Ui.CardCancel, key: "card"));
            double dataLeft = x + apply + cancel + 24;
            for (int i = 0; i < card.Data.Count && dataLeft + 60 < x + inner; i++)
                children.Add(TextIn(NativeUiText.Ellipsize(card.Data[i], x + inner - dataLeft, TextSize.Xs), dataLeft, bottom - 30 + i * dataHeight, x + inner - dataLeft,
                    card.Data.Count == 1 ? 30 : dataHeight, TextSize.Xs, Ui.Ink, TextAlignX.Right));
        }
        else
        {
            for (int i = 0; i < card.Data.Count; i++)
                children.Add(Text(NativeUiText.Ellipsize(card.Data[i], inner, TextSize.Xs), x, stripTop + i * dataHeight, inner, dataHeight, TextSize.Xs, Ui.Mute, TextAlignX.Right));
        }

        if (recording)
        {
            // REC block with the take facts stacked beside it.
            double block = NativeUiText.Width(card.Head, headSize) + 24, blockHeight = Math.Round(headHeight * .86);
            children.Add(Box(x, y, block, blockHeight, Ui.Orange));
            children.Add(Text(card.Head, x + 12, y + blockHeight - headHeight * .95, block - 24, headHeight, headSize, Ui.Ink));
            double cx = x + block + 18, cy = y;
            foreach (string line in card.Tail.Split('\n'))
            {
                var size = Fit(line, subSize, x + inner - cx, blockHeight / 2);
                double h = Line(line, size); children.Add(Text(line, cx, cy, x + inner - cx, h, size, fg)); cy += h * .85;
            }
            y += blockHeight + 10;
        }
        else
        {
            children.Add(Text(card.Head, x, y, inner, headHeight, headSize, fg));
            y += headHeight * .88;
            if (card.Stats is { Count: > 0 } stats)
            {
                double column = inner / stats.Count, valueHeight = 0;
                for (int i = 0; i < stats.Count; i++)
                {
                    var (value, label, accent) = stats[i];
                    var size = Fit(value, subSize, column - 12, m.Height * .3);
                    double h = Line(value, size); valueHeight = Math.Max(valueHeight, h);
                    children.Add(Text(value, x + i * column, y, column - 12, h, size, accent ? Ui.CardOrange : fg));
                    children.Add(Text(NativeUiText.Ellipsize(label, column - 12, TextSize.Xs), x + i * column, y + h * .9, column - 12, 14, TextSize.Xs, fg));
                }
                y += valueHeight * .9 + 18;
            }
            else if (card.Tail.Length > 0)
            {
                var size = Fit(card.Tail, subSize, inner - 40, m.Height * .25);
                double h = Line(card.Tail, size), square = Math.Round(TypeScale.Pixels(size) * .6);
                children.Add(Box(x, y + h * .78 - square, square, square, Ui.Orange));
                children.Add(Text(card.Tail, x + square + 14, y, inner - square - 14, h, size, fg));
                y += h * .9;
            }
        }
        double bodyHeight = Line(card.Body, TextSize.Lg);
        if (card.Body.Length > 0 && y + 10 + bodyHeight <= stripTop - 6)
            children.Add(Text(NativeUiText.Ellipsize(card.Body, inner, TextSize.Lg), x, y + 10, inner, bodyHeight, TextSize.Lg, fg));
    }

    // ---------------------------------------------------------------- transport

    private static void Transport(List<UiNode> children, EditorDocument editor, EditorLayout layout, bool playing)
    {
        var t = layout.Transport; var rate = editor.MonitorFrameRate;
        Fill(children, t, Ui.Panel);
        bool twoRows = layout.TransportRows == 2, source = editor.Source is not null;
        double rowH = EditorLayout.TransportRowHeight, rowY = t.Y + 5, buttonsY = twoRows ? t.Y + rowH + 5 : rowY, left0 = t.X + 6, right0 = t.X + t.Width - 6;
        double fullscreenWidth = t.Width < 450 ? 42 : 98;
        children.Add(Button(t.Width < 450 ? "F11" : "Full screen", nameof(EditorAction.Fullscreen), right0 - fullscreenWidth, rowY, fullscreenWidth, 26, Ui.Outline));
        double controlsRight = right0;
        right0 -= fullscreenWidth + 8;
        // Monitor mode switch (Premiere's source/program monitors share one picture here).
        double program = Command("Program"), sourceWidth = Command("Source");
        children.Add(Button("Program", "MonitorProgram", left0, rowY, program, 26, source ? Ui.Outline : Ui.Active));
        children.Add(Button("Source", "MonitorSource", left0 + program, rowY, sourceWidth, 26, source ? Ui.Marked : Ui.Outline));
        double x = left0 + program + sourceWidth + 10;
        string timecode = Timecode.Format(editor.ScrubTarget ?? editor.Playhead, rate), duration = "/ " + Timecode.Format(editor.MonitorDuration, rate);
        var tcSize = Fit(timecode, TextSize.Xl, Math.Max(0, right0 - x - NativeUiText.Width(duration) - 8), 48);
        double tcWidth = NativeUiText.Width(timecode, tcSize);
        children.Add(TextIn(timecode, x, t.Y, tcWidth + 2, rowH, tcSize));
        x += tcWidth + 8;
        if (x + NativeUiText.Width(duration) < right0) { children.Add(TextIn(duration, x, t.Y + 2, NativeUiText.Width(duration) + 2, rowH, TextSize.Sm, Ui.Mute)); x += NativeUiText.Width(duration) + 10; }
        double timecodeRight = x;

        string label = source ? editor.Source!.Name : "View / " + (ProfileName(editor) ?? "Sequence");
        double labelWidth = Math.Min(source ? 220 : 190, (source ? NativeUiText.Width(label, TextSize.Xs) : Command(label)) + 8);
        bool labelFits = right0 - labelWidth >= timecodeRight + 8;
        if (!labelFits && !source) { labelWidth = Math.Max(0, right0 - timecodeRight - 8); labelFits = labelWidth >= 90; }
        if (!labelFits) labelWidth = 0;
        else if (source) children.Add(TextIn(NativeUiText.Ellipsize(label, labelWidth, TextSize.Xs), right0 - labelWidth, t.Y, labelWidth, rowH, TextSize.Xs, Ui.Mute, TextAlignX.Right));
        else children.Add(Button(NativeUiText.Ellipsize(label.ToUpperInvariant(), labelWidth - 12, TextSize.Md), "OutputProfile", right0 - labelWidth, rowY, labelWidth, 26, Ui.Outline));

        (SequenceTime? In, SequenceTime? Out) marks = source ? editor.SourceMarks.GetValueOrDefault(editor.Source!.AssetId) : (editor.MarkIn, editor.MarkOut);
        var buttons = new List<(string Label, string Action, double Width, StandardButtonStyle Style, bool GroupStart)>
        {
            ("|<", "GoToStart", 30, Ui.Plain, false), ("<<", "PreviousEdit", 30, Ui.Plain, false), ("<", "PreviousFrame", 30, Ui.Plain, false),
            (editor.Reversing ? "Rev" : playing ? "Pause" : "Play", "TogglePlay", 64, editor.Reversing ? Ui.Marked : Ui.Primary, false),
            (">", "NextFrame", 30, Ui.Plain, false), (">>", "NextEdit", 30, Ui.Plain, false),
            (">|", "GoToEnd", 30, Ui.Plain, false),
            ("In", "MarkIn", 38, marks.In is null ? Ui.Plain : Ui.Marked, true), ("Out", "MarkOut", 44, marks.Out is null ? Ui.Plain : Ui.Marked, false),
        };
        if (source) buttons.AddRange([("Insert ,", "InsertEdit", Command("Insert ,"), Ui.Plain, true), ("Overwrite .", "OverwriteEdit", Command("Overwrite ."), Ui.Plain, false)]);
        double total = buttons.Sum(b => b.Width + (b.GroupStart ? 8 : 0)) + (buttons.Count - 1) * 2;
        double right = twoRows ? controlsRight : right0 - (labelWidth > 0 ? labelWidth + 10 : 0);
        double left = twoRows ? left0 + Math.Max(0, (t.Width - 12 - total) / 2) : Math.Max(timecodeRight + 4, right - total);
        foreach (var b in buttons)
        {
            if (b.GroupStart) left += 8;
            if (left + b.Width > right) break;
            children.Add(Button(b.Label, b.Action, left, buttonsY, b.Width, 26, b.Style, key: "transport")); left += b.Width + 2;
        }
    }

    // ---------------------------------------------------------------- toolbar

    private static void Toolbar(List<UiNode> children, EditorDocument editor, EditorLayout layout, bool razor)
    {
        var bar = layout.Toolbar;
        double addWidth = (layout.ClipLeft - Gap - 2) / 2;
        bool narrow = addWidth < 70;
        children.Add(Button(narrow ? "+V" : "+ Video", "AddVideo", bar.X, bar.Y, addWidth, 28));
        children.Add(Button(narrow ? "+A" : "+ Audio", "AddAudio", bar.X + addWidth + 2, bar.Y, addWidth, 28));
        bool phone = layout.Profile == EditorLayoutProfile.VerticalPhone, keys = layout.Width >= 1360 && !phone;
        // Labels carry their Premiere shortcut where there is room; the phone root keeps the core tools only.
        string K(string label, string key) => keys ? label + " " + key : label;
        var tools = new List<(string Label, string Action, StandardButtonStyle Style, bool GroupStart, (uint Color, bool On)? Pip)>
        {
            (K("Select", "V"), "SelectTool", razor ? Ui.Plain : Ui.Primary, false, null), (K("Razor", "C"), "RazorTool", razor ? Ui.Primary : Ui.Plain, false, null),
            ("Split", "Split", Ui.Plain, true, null), (phone ? "Del" : "Delete", "Delete", Ui.Plain, false, null), ("Ripple", "RippleDelete", Ui.Plain, false, null),
            (phone ? "T" : "Title", "NewTitle", Ui.Plain, true, null),
        };
        if (!phone) tools.AddRange([(K("Insert", ","), "InsertEdit", Ui.Plain, true, null), (K("Overwrite", "."), "OverwriteEdit", Ui.Plain, false, null),
            (K("Lift", ";"), "Lift", Ui.Plain, false, null), (K("Extract", "'"), "Extract", Ui.Plain, false, null)]);
        tools.AddRange([("Undo", "Undo", Ui.Plain, true, null), ("Redo", "Redo", Ui.Plain, false, null),
            ("Snap", "ToggleSnap", editor.Snapping ? Ui.Plain : Ui.Dim, true, (Ui.Cyan, editor.Snapping)),
            ("Link", "ToggleLink", editor.LinkedSelection ? Ui.Plain : Ui.Dim, false, (Ui.Orange, editor.LinkedSelection)),
            ("Magnet", "ToggleMagnet", editor.Magnetic ? Ui.Plain : Ui.Dim, false, (Ui.Bone, editor.Magnetic))]);
        double zoomWidth = 30 + 44 + 30 + 4;
        double x = layout.ClipLeft, y = bar.Y, right = bar.X + bar.Width - (layout.ToolbarRows == 2 ? 0 : zoomWidth + 8);
        // Reduce padding first. If the row is still tight, give the whole group proportionally smaller labels.
        const double pipSpace = 18, gap = 2, groupGap = 8;
        var widths = tools.Select(t => NativeUiText.Width(t.Label.ToUpperInvariant(), TextSize.Md)).ToArray();
        double ink = widths.Sum();
        double spacing = (tools.Count - 1) * gap + tools.Count(t => t.GroupStart) * groupGap + tools.Count(t => t.Pip is not null) * pipSpace;
        double padding = layout.ToolbarRows == 2 ? 20 : Math.Clamp((right - x - spacing - ink) / tools.Count, 8, 20);
        double scale = layout.ToolbarRows == 2 ? 1 : Math.Min(1, (right - x - spacing - padding * tools.Count) / ink);
        for (int i = 0; i < tools.Count; i++)
        {
            var tool = tools[i];
            double w = widths[i] * scale + padding + (tool.Pip is null ? 0 : pipSpace);
            if (tool.GroupStart) x += groupGap;
            if (x + w > right && layout.ToolbarRows == 2 && y == bar.Y) { x = bar.X; y += EditorLayout.ToolbarRowHeight + 4; right = bar.X + bar.Width - zoomWidth - 8; }
            if (x + w > right + .001) break;
            var style = tool.Pip is null ? tool.Style : tool.Style with { HorizontalPadding = pipSpace / 2 };
            children.Add(Button(tool.Label, tool.Action, x, y, w, 28, style, key: "tool"));
            if (tool.Pip is { } pip) children.Add(pip.On ? Box(x + 6, y + 11, 6, 6, pip.Color) : Frame(x + 6, y + 11, 6, 6, Ui.Edge));
            x += w + gap;
        }
        double zx = bar.X + bar.Width - zoomWidth, zy = layout.ToolbarRows == 2 ? bar.Y + EditorLayout.ToolbarRowHeight + 4 : bar.Y;
        foreach (var (label, action) in new[] { ("-", "ZoomOut"), ("Fit", "ZoomFit"), ("+", "ZoomIn") })
        {
            double w = label == "Fit" ? 44 : 30; children.Add(Button(label, action, zx, zy, w, 28)); zx += w + 2;
        }
    }

    // ---------------------------------------------------------------- timeline

    private void Timeline(List<UiNode> children, List<CanvasRectangle> quads, EditorDocument editor, TimelineGesture gesture, EditorLayout layout)
    {
        var sequence = editor.Sequence; var viewport = editor.Viewport;
        double rulerY = layout.RulerY, rulerLeft = viewport.ClipLeft, rulerWidth = viewport.ClipRight - viewport.ClipLeft;
        // Subdivisions use timeline time so they move with scrolling and zooming.
        children.Add(Box(rulerLeft, rulerY, rulerWidth, EditorLayout.RulerHeight, Ui.Panel));
        children.Add(Box(rulerLeft, rulerY + EditorLayout.RulerHeight - 1, rulerWidth, 1, Ui.Rule));
        // A "nice" step that keeps labels at least ~90 px apart.
        double[] steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];
        double step = steps.FirstOrDefault(s => s * viewport.PixelsPerSecond >= 90, steps[^1]);
        double origin = viewport.Origin.TotalSeconds;
        double first = Math.Floor(origin / step) * step;
        double end = viewport.Time(viewport.ClipRight).TotalSeconds;
        int labels = 0;
        for (double second = first; second < end && labels < 200; second += step, labels++)
        {
            for (int division = 1; division < 4; division++)
            {
                double minor = second + step * division / 4;
                if (minor < origin || minor >= end) continue;
                float minorX = (float)viewport.X(new((long)(minor * SequenceTime.TicksPerSecond)));
                quads.Add(new(minorX, (float)(rulerY + EditorLayout.RulerHeight - 6), 1, 5, Ui.Rule));
            }
            if (second < origin) continue;
            float x = (float)viewport.X(new((long)(second * SequenceTime.TicksPerSecond)));
            quads.Add(new(x, (float)rulerY + 9, 1, (float)EditorLayout.RulerHeight - 10, Ui.Tick));
            children.Add(Text(Timecode.Ruler(second), x + 4, rulerY + 8, 80, 14, TextSize.Xs, Ui.Mute));
        }
        uint playheadColor = editor.ScrubTarget is not null ? Ui.Cyan : editor.Reversing ? Ui.Reverse : Ui.Orange;
        children.Add(TextIn(Timecode.Format(editor.TimelinePlayhead, sequence.Video.FrameRate), Gap + 2, rulerY, layout.ClipLeft - Gap - 6, EditorLayout.RulerHeight, TextSize.Lg, playheadColor));
        foreach (var marker in sequence.Markers)
        {
            double x = viewport.X(marker.At);
            if (x < viewport.ClipLeft || x + 5 > viewport.ClipRight) continue;
            quads.Add(new((float)x, (float)rulerY + 1, 5, 7, Ui.Cyan));
        }
        double row = viewport.Row, clipHeight = row - 8;

        HashSet<Guid> selected = editor.Selection is { } chosen ? [.. EditorEdits.Group(editor, chosen.Id).Select(g => g.Clip.Id)] : [];
        var labelsByTrack = TrackLabels(sequence);
        var queries = new TimelineQueries(editor.Session.Snapshot);
        VisibleClips = 0; int clipLabels = 0;
        for (int i = viewport.FirstTrack; i < Math.Min(sequence.Tracks.Length, viewport.FirstTrack + viewport.VisibleTracks); i++)
        {
            var track = sequence.Tracks[i]; float y = (float)(viewport.TrackTop + (i - viewport.FirstTrack) * row);
            string trackLabel = labelsByTrack[track.Id];
            TrackHeader(children, editor, layout, track, trackLabel, y, row);
            quads.Add(new((float)viewport.ClipLeft, y, (float)(viewport.ClipRight - viewport.ClipLeft), (float)row - 2, Ui.Track));
            bool video = track.Kind == TrackKind.Video;
            bool dim = track.State.Locked || (video ? !track.State.Visible : track.State.Muted);
            foreach (var clip in viewport.Visible(track))
            {
                VisibleClips++;
                bool isSelected = clip.Id == editor.SelectedClip, linked = !isSelected && selected.Contains(clip.Id);
                bool title = clip.IsTitle();
                // Title clips read as title cards: an ink slab with an orange tab, not footage blue.
                uint color = isSelected ? Ui.Orange : title ? Ui.Ink : video ? Ui.Ultramarine : Ui.Paper;
                uint ink = isSelected || !video ? Ui.Ink : Ui.Bone;
                if (dim) color = (color & 0xFFFFFF00) | 0x66;
                ClipRect(clip, y, color, linked);
                if (title)
                {
                    double tabX = viewport.X(clip.Start);
                    if (tabX >= viewport.ClipLeft && tabX + 4 <= viewport.ClipRight) quads.Add(new((float)tabX, y + 1, 4, (float)row - 4, isSelected ? Ui.Ink : Ui.Orange));
                    if (!isSelected) quads.Add(new((float)Math.Max(viewport.ClipLeft, viewport.X(clip.Start)), y + (float)row - 4,
                        (float)(Math.Min(viewport.ClipRight, viewport.X(clip.End)) - Math.Max(viewport.ClipLeft, viewport.X(clip.Start))), 1, Ui.Rule));
                }
                Waveform(editor, viewport, quads, track, clip, y, row, isSelected ? Ui.Ink : Ui.Wave);
                double left = Math.Max(viewport.ClipLeft, viewport.X(clip.Start)) + 7, right = Math.Min(viewport.ClipRight, viewport.X(clip.End)) - 7;
                if (right - left >= 40 && clipLabels++ < 64)
                {
                    string code = $"{trackLabel}-{track.Clips.IndexOf(clip) + 1:0000}";
                    double codeWidth = NativeUiText.Width(code, TextSize.Xs) + 10;
                    bool withCode = right - left >= codeWidth + 60;
                    double nameWidth = right - left - (withCode ? codeWidth : 0);
                    children.Add(Text(NativeUiText.Ellipsize(ClipName(editor, clip), nameWidth, TextSize.Xs), left, y + 7, nameWidth, 14, TextSize.Xs, ink));
                    if (withCode) children.Add(Text(code, right - codeWidth, y + 7, codeWidth, 14, TextSize.Xs, ink, TextAlignX.Right));
                }
            }
            DrawTimelineEffects(quads, editor, sequence, queries, track, y);
            if (gesture.Active)
                foreach (var (trackId, ghost) in gesture.Ghosts)
                    if (trackId == track.Id) ClipRect(ghost, y, gesture.Valid ? Ui.Ghost : Ui.GhostInvalid, false);
        }
        if (sequence.Tracks.Length > viewport.VisibleTracks)
            children.Add(Text($"Tracks {viewport.FirstTrack + 1}-{Math.Min(sequence.Tracks.Length, viewport.FirstTrack + viewport.VisibleTracks)} of {sequence.Tracks.Length} (Shift+wheel)",
                Gap, viewport.TrackTop + viewport.VisibleTracks * row + 2, layout.Width - 2 * Gap, 14, TextSize.Xs, Ui.Mute));
        if (sequence.Tracks.All(t => t.Clips.IsEmpty))
            children.Add(Text("Empty sequence. Import media (Ctrl+I), select it, then Insert (,) at the playhead.",
                viewport.ClipLeft + 12, viewport.TrackTop + 14, viewport.ClipRight - viewport.ClipLeft - 24, 18, color: Ui.Mute));

        foreach (var removal in (editor.ProposedCut?.Removed ?? []).Concat(editor.ProposedTextCut?.Removed ?? []))
        {
            float left = (float)Math.Clamp(viewport.X(removal.Start), viewport.ClipLeft, viewport.ClipRight);
            float right = (float)Math.Clamp(viewport.X(removal.End), viewport.ClipLeft, viewport.ClipRight);
            if (right > left) quads.Add(new(left, (float)viewport.TrackTop, right - left, (float)(viewport.TrackBottom - viewport.TrackTop), Ui.Removal));
        }
        if (editor.MarkIn is not null || editor.MarkOut is not null)
        {
            var markEnd = editor.MarkOut ?? sequence.Duration;
            float markLeft = (float)Math.Clamp(viewport.X(editor.MarkIn ?? default), viewport.ClipLeft, viewport.ClipRight);
            float markRight = (float)Math.Clamp(viewport.X(markEnd), viewport.ClipLeft, viewport.ClipRight);
            if (markRight > markLeft) quads.Add(new(markLeft, (float)rulerY, markRight - markLeft, 8, Ui.Ultramarine));
        }
        if (gesture.Active && gesture.SnapLine is { } snap)
        {
            float snapX = (float)viewport.X(snap);
            if (snapX >= viewport.ClipLeft && snapX <= viewport.ClipRight) quads.Add(new(snapX - 1, (float)rulerY, 2, (float)(viewport.TrackBottom - rulerY), Ui.Cyan));
        }
        void ClipRect(TimelineClip c, float y, uint color, bool ring)
        {
            float left = (float)Math.Clamp(viewport.X(c.Start), viewport.ClipLeft, viewport.ClipRight), right = (float)Math.Clamp(viewport.X(c.End), viewport.ClipLeft, viewport.ClipRight);
            if (right <= left) return;
            float w = Math.Max(1, right - left), h = (float)clipHeight, top = y + 3;
            quads.Add(new(left, top, w, h, color));
            quads.Add(new(left, top, Math.Min(1, w), h, Ui.Ink)); // a 1 px cut line between adjacent clips
            if (ring && w > 4)
            {
                quads.Add(new(left, top, w, 2, Ui.Orange)); quads.Add(new(left, top + h - 2, w, 2, Ui.Orange));
                quads.Add(new(left, top, 2, h, Ui.Orange)); quads.Add(new(right - 2, top, 2, h, Ui.Orange));
            }
        }
    }

    private static Dictionary<Guid, string> TrackLabels(SequenceDocument sequence)
    {
        int video = 0, audio = 0; var labels = new Dictionary<Guid, string>();
        foreach (var track in sequence.Tracks) labels[track.Id] = track.Kind == TrackKind.Video ? "V" + ++video : "A" + ++audio;
        return labels;
    }

    private static void TrackHeader(List<UiNode> children, EditorDocument editor, EditorLayout layout, TimelineTrack track, string label, float y, double row)
    {
        double w = layout.ClipLeft - Gap - 2, height = row - 2;
        children.Add(Box(Gap, y, w, height, Ui.Panel));
        bool targeted = editor.TargetTrack == track.Id, video = track.Kind == TrackKind.Video;
        double block = Math.Min(54, Math.Round(w / 3));
        children.Add(Button(label, "Target:" + track.Id, Gap, y, block, height, targeted ? Ui.TrackTarget : video ? Ui.TrackVideo : Ui.TrackAudio));
        double x = Gap + block + 8, inner = w - block - 12;
        double toggle = Math.Min(26, (inner - 2) / 2), toggleHeight = Math.Min(20, (height - 6) / 2);
        bool off = video ? !track.State.Visible : track.State.Muted;
        double nameHeight = Line("A", TextSize.Xs);
        bool showName = height >= toggleHeight + nameHeight + 10 && inner > 30;
        double toggleY = showName ? y + (height - toggleHeight - nameHeight - 4) / 2 + nameHeight + 4 : y + (height - toggleHeight) / 2;
        if (showName) children.Add(Text(NativeUiText.Ellipsize((track.Role is { } role ? role + " · " : "") + track.Name, inner, TextSize.Xs), x, toggleY - nameHeight - 4, inner, nameHeight, TextSize.Xs, Ui.Mute));
        children.Add(Button("L", "Lock:" + track.Id, x, toggleY, toggle, toggleHeight, track.State.Locked ? Ui.Danger : Ui.Plain));
        children.Add(Button(video ? "H" : "M", "State:" + track.Id, x + toggle + 2, toggleY, toggle, toggleHeight, off ? Ui.Danger : Ui.Plain));
    }

    private static void Waveform(EditorDocument editor, TimelineViewport viewport, List<CanvasRectangle> quads, TimelineTrack track, TimelineClip clip, float y, double row, uint color)
    {
        if (track.Kind != TrackKind.Audio || !editor.Waveforms.TryGetValue((clip.AssetId, clip.StreamIndex), out var waveform)) return;
        var visibleStart = new SequenceTime(Math.Max(clip.Start.Ticks, viewport.Origin.Ticks));
        var visibleEnd = new SequenceTime(Math.Min(clip.End.Ticks, viewport.Time(viewport.ClipRight).Ticks));
        if (visibleEnd.Ticks <= visibleStart.Ticks) return;
        var sourceStart = clip.SourceIn.Shift(new(visibleStart.Ticks - clip.Start.Ticks));
        var sourceEnd = clip.SourceIn.Shift(new(visibleEnd.Ticks - clip.Start.Ticks));
        int pixels = Math.Max(1, (int)(viewport.X(visibleEnd) - viewport.X(visibleStart)));
        foreach (var bin in waveform.Visible(sourceStart, sourceEnd, pixels))
        {
            long from = Math.Max(visibleStart.Ticks, clip.Start.Ticks + bin.Start.Since(clip.SourceIn).Ticks);
            long to = Math.Min(visibleEnd.Ticks, clip.Start.Ticks + bin.End.Since(clip.SourceIn).Ticks);
            float x = (float)viewport.X(new(from));
            float binWidth = Math.Max(1, (float)(viewport.X(new(to)) - x));
            for (int channel = 0; channel < bin.Channels.Length; channel++)
            {
                var peak = bin.Channels[channel];
                float amplitude = Math.Clamp(Math.Max(Math.Abs(peak.Minimum), Math.Abs(peak.Maximum)), 0, 1);
                // Below the clip label: the waveform band takes the rest of the clip height.
                float band = (float)row - 34, center = y + 26 + (channel + .5f) * band / bin.Channels.Length;
                float binHeight = Math.Max(1, amplitude * (band - 1) / bin.Channels.Length);
                quads.Add(new(x, center - binHeight / 2, binWidth, binHeight, color));
            }
        }
    }

    // ---------------------------------------------------------------- status

    private static void Status(List<UiNode> children, EditorDocument editor, EditorLayout layout)
    {
        double y = layout.StatusY, width = layout.Width - 2 * 12, h = EditorLayout.StatusHeight;
        children.Add(Box(0, y, layout.Width, 1, Ui.Rule));
        if (editor.EffectDrag?.Preview is { Success: false, Message: { } failure, Error: not Valencia.Core.Editing.EditError.NoChange })
        {
            children.Add(TextIn(NativeUiText.Ellipsize(failure, width, TextSize.Sm), 12, y + 1, width, h - 1, TextSize.Sm, Ui.Alert));
            return;
        }
        var rate = editor.Sequence.Video.FrameRate;
        string selection = editor.Selection is { } chosen
            ? $"{Short(chosen.Properties.Name, 24)} {Timecode.Format(chosen.Start, rate)}-{Timecode.Format(chosen.End, rate)}  |  " : "";
        string marks = editor.MarkIn is not null || editor.MarkOut is not null
            ? $"In {Timecode.Format(editor.MarkIn ?? default, rate)} Out {Timecode.Format(editor.MarkOut ?? editor.Sequence.Duration, rate)}  |  " : "";
        string hints = layout.Portrait ? "Space play | Ctrl+K split" : "Space play | I/O mark | Q/W trim | , insert | . overwrite | ; lift | ' extract | Ctrl+K split | Shift+wheel tracks";
        double hintWidth = Math.Min(NativeUiText.Width(hints, TextSize.Xs) + 4, width * .5);
        string message = "> " + selection + marks + editor.Status;
        children.Add(TextIn(NativeUiText.Ellipsize(message, width - hintWidth - 16), 12, y + 1, width - hintWidth - 16, h - 1, TextSize.Sm, Ui.Bone));
        children.Add(TextIn(NativeUiText.Ellipsize(hints, hintWidth, TextSize.Xs), layout.Width - 12 - hintWidth, y + 1, hintWidth, h - 1, TextSize.Xs, Ui.Edge, TextAlignX.Right));
    }

    // ---------------------------------------------------------------- panel

    private static IReadOnlyList<UiNode> Panel(EditorDocument editor, Rect panel, bool compact, bool inlineInspector)
    {
        var items = new List<UiNode>();
        double width = panel.Width - 2 * Pad, height = panel.Height;
        var tab = editor.ControlsMode && inlineInspector ? PanelTab.Controls
            : editor.ObsMode ? PanelTab.Record : editor.TranscriptMode ? PanelTab.Transcript : PanelTab.Media;
        bool card = tab != PanelTab.Controls && !compact && height >= 300;
        double y = Pad;
        if (card)
        {
            // Title card header: the open tab's name, large, with one fact about it on the right.
            string head = TabNames[(int)tab].ToUpperInvariant();
            var size = Fit(head, TextSize.H1, width - 12, 72);
            double h = Line(head, size);
            items.Add(Text(head, Pad + 6, y - 2, width - 6, h, size));
            string info = PanelInfo(editor, tab);
            double infoWidth = Math.Min(NativeUiText.Width(info, TextSize.Xs) + 2, width - 12 - NativeUiText.Width(head, size) - 12);
            if (infoWidth > 40) items.Add(Text(NativeUiText.Ellipsize(info, infoWidth, TextSize.Xs), Pad + width - 6 - infoWidth, y - 2 + h * .62, infoWidth, 14, TextSize.Xs, Ui.Mute, TextAlignX.Right));
            y += h - 4;
            items.Add(Box(0, y, panel.Width, 1, Ui.Rule));
            y += 6;
        }
        int tabCount = inlineInspector ? TabNames.Length : TabNames.Length - 1;
        double tabWidth = width / tabCount;
        for (int i = 0; i < tabCount; i++)
        {
            bool on = (int)tab == i;
            string numbered = $"{i + 1:00} {TabNames[i]}".ToUpperInvariant();
            string label = NativeUiText.Width(numbered, TextSize.Md) <= tabWidth - 8 ? numbered : TabNames[i].ToUpperInvariant();
            items.Add(Button(label, "Tab:" + TabNames[i], Pad + i * tabWidth, y, tabWidth, 24, on ? Ui.TabOn : Ui.Tab));
            if (on)
            {
                double lw = Math.Min(tabWidth - 8, NativeUiText.Width(label, Fit(label, TextSize.Md, tabWidth - 6, 24)));
                items.Add(Box(Pad + i * tabWidth + (tabWidth - lw) / 2, y + 22, lw, 2, Ui.Orange));
            }
        }
        double top = y + 24 + (tab == PanelTab.Controls ? 4 : 10), bottom = height - Pad;
        if (card)
        {
            // Edge-code footer: real data only.
            double footerY = height - 22;
            items.Add(Box(0, footerY, panel.Width, 1, Ui.Rule));
            items.Add(TextIn(NativeUiText.Ellipsize(PanelFooter(editor, tab), width - 12, TextSize.Xs), Pad + 6, footerY + 1, width - 12, 21, TextSize.Xs, Ui.Edge));
            bottom = footerY - Pad;
        }
        // A pending proposal is always shown, whichever tab is open, because it gates further edits.
        if (editor.ProposedCut is { } cut)
            bottom = Proposal(items, "Rough cut", cut.Removed, "ApplyCut", "CancelCut", width, bottom);
        else if (editor.ProposedTextCut is { } text)
            bottom = Proposal(items, "Text edit", text.Removed, "ApplyTextCut", "CancelTextCut", width, bottom);
        switch (tab)
        {
            case PanelTab.Media: MediaTab(items, editor, width, top, bottom, compact); break;
            case PanelTab.Transcript: TranscriptTab(items, editor, width, top, bottom); break;
            case PanelTab.Controls:
                if (bottom - top >= 32)
                {
                    var rect = new Rect(0, top - 6, panel.Width, bottom - top + 6);
                    items.Add(StandardUI.Frame(rect.X, rect.Y, rect.Width, rect.Height, Inspector(editor, rect, compact: true)));
                }
                break;
            default: RecordTab(items, editor, width, top, bottom); break;
        }
        return items;
    }

    private static string PanelInfo(EditorDocument editor, PanelTab tab) => tab switch
    {
        PanelTab.Media => $"{editor.Session.Snapshot.Document.Assets.Length} ITEMS",
        PanelTab.Transcript => editor.Transcript?.Transcript is { } t ? $"{t.Words.Length} WORDS" : "NO TRANSCRIPT",
        _ => "OBS " + editor.ObsConnection.ToUpperInvariant(),
    };

    private static string PanelFooter(EditorDocument editor, PanelTab tab)
    {
        var assets = editor.Session.Snapshot.Document.Assets;
        switch (tab)
        {
            case PanelTab.Media:
                double total = assets.Sum(AssetSeconds);
                string chosen = editor.SelectedAsset is { } id ? " > SEL " + IdCode(id) : "";
                return $"VLN-BIN-{assets.Length:000} > {Timecode.Duration(total)} TOTAL" + chosen;
            case PanelTab.Transcript:
                if (editor.Transcript?.Transcript is not { } transcript) return "TXT NONE > SELECT MEDIA, THEN SPEECH";
                string first = editor.TranscriptFirstWord + 1 > transcript.Words.Length ? "-" : (editor.TranscriptFirstWord + 1).ToString(CultureInfo.InvariantCulture);
                return $"TXT {IdCode(editor.Transcript.AssetId)} > {transcript.Words.Length} WORDS > FROM {first}";
            default:
                return "OBS " + editor.ObsConnection.ToUpperInvariant() + " > " + editor.ObsRecording.ToUpperInvariant();
        }
    }

    private const double Pad = 8;

    private static double Proposal(List<UiNode> items, string title, IReadOnlyList<Valencia.Engine.TimelineRemoval> removed, string apply, string cancel, double width, double bottom)
    {
        double seconds = removed.Sum(r => (r.End.Ticks - r.Start.Ticks) / (double)SequenceTime.TicksPerSecond);
        double y = bottom - 54;
        items.Add(Text(NativeUiText.Ellipsize($"{title}: {removed.Count} removals, -{Timecode.Duration(seconds)} (red on timeline)", width), Pad, y, width, 18, color: Ui.Orange));
        double half = (width - 2) / 2;
        items.Add(Button("Apply", apply, Pad, y + 22, half, 28, Ui.Primary));
        items.Add(Button("Cancel", cancel, Pad + half + 2, y + 22, half, 28));
        return y - 8;
    }

    private static void MediaTab(List<UiNode> items, EditorDocument editor, double width, double top, double bottom, bool compact)
    {
        var actions = new (string Label, string Short, string Action)[] { ("Import", "Import", "Import"), ("Insert ,", "Insert", "InsertEdit"),
            ("Overwrite .", "Overwrite", "OverwriteEdit"), ("Relink", "Relink", "Relink"), ("Analyze", "Analyze", "Analyze"), ("Speech", "Speech", "Transcribe"),
            ("Rough cut", "Rough cut", "RoughCut"), ("Script", "Script", "RunScript") };
        const int columns = 4;
        const double gap = 2;
        var labelWidths = Enumerable.Range(0, columns).Select(i => Math.Max(
            NativeUiText.Width(actions[i].Short.ToUpperInvariant(), TextSize.Md),
            NativeUiText.Width(actions[i + columns].Short.ToUpperInvariant(), TextSize.Md))).ToArray();
        double room = width - (columns - 1) * gap;
        double total = labelWidths.Sum();
        double padding = Math.Max(6, (room - total) / columns);
        double scale = Math.Min(1, (room - padding * columns) / total);
        double actionsTop = bottom - 2 * 28 - 2;
        double x = Pad;
        for (int i = 0; i < actions.Length; i++)
        {
            int column = i % columns;
            double cell = labelWidths[column] * scale + padding;
            string label = NativeUiText.Width(actions[i].Label.ToUpperInvariant(), TextSize.Md) <= cell - 6 ? actions[i].Label : actions[i].Short;
            items.Add(Button(label, actions[i].Action, x, actionsTop + i / columns * 30, cell, 28, key: "media"));
            x = column == columns - 1 ? Pad : x + cell + gap;
        }
        var assets = editor.Session.Snapshot.Document.Assets;
        double rowHeight = compact ? 30 : 40, listBottom = actionsTop - 8;
        int capacity = Math.Max(1, (int)((listBottom - top) / (rowHeight + 2)));
        editor.MediaPageSize = capacity;
        if (assets.IsEmpty) items.Add(Text(NativeUiText.Ellipsize("No media. Import (Ctrl+I) or record (Record tab).", width), Pad, top, width, 18, color: Ui.Mute));
        double y = top;
        foreach (var asset in assets.Skip(editor.FirstAsset).Take(capacity))
        {
            bool chosen = editor.SelectedAsset == asset.Id;
            items.Add(Button("", "Asset:" + asset.Id, Pad, y, width, rowHeight, chosen ? Ui.RowOn : Ui.Row));
            if (chosen) items.Add(Box(Pad, y, 3, rowHeight, Ui.Orange));
            double seconds = AssetSeconds(asset);
            string duration = seconds > 0 ? Timecode.Duration(seconds) : "", streams = StreamCode(asset);
            double rightWidth = compact ? 0 : Math.Max(NativeUiText.Width(duration), NativeUiText.Width(streams, TextSize.Xs)) + 8;
            double nameWidth = width - 14 - rightWidth;
            if (compact)
            {
                items.Add(TextIn(NativeUiText.Ellipsize(asset.Role is { } role ? role + " · " + asset.Name : asset.Name, nameWidth), Pad + 10, y, nameWidth, rowHeight));
            }
            else
            {
                items.Add(Text(NativeUiText.Ellipsize(asset.Name, nameWidth), Pad + 10, y + 4, nameWidth, 16));
                string detail = chosen ? Valencia.Engine.AgentHandles.For("asset", asset.Id) : AssetMeta(asset);
                items.Add(Text(NativeUiText.Ellipsize((asset.Role is { } role ? role + " · " : "") + detail, nameWidth, TextSize.Xs), Pad + 10, y + 22, nameWidth, 14, TextSize.Xs, Ui.Mute));
                items.Add(Text(duration, Pad + width - rightWidth, y + 4, rightWidth - 6, 16, alignX: TextAlignX.Right));
                items.Add(Text(streams, Pad + width - rightWidth, y + 22, rightWidth - 6, 14, TextSize.Xs, Ui.Mute, TextAlignX.Right));
            }
            y += rowHeight + 2;
        }
        if (assets.Length > capacity)
            items.Add(Text($"{editor.FirstAsset + 1}-{Math.Min(assets.Length, editor.FirstAsset + capacity)} of {assets.Length} (wheel)", Pad, listBottom - 6, width, 14, TextSize.Xs, Ui.Mute));
    }

    private static double AssetSeconds(MediaAsset asset) => asset.Streams.Select(s => s.End is { } end && s.Start is { } start
        ? new SourcePosition(end).Since(new SourcePosition(start)).TotalSeconds : 0).DefaultIfEmpty(0).Max();

    private static string StreamCode(MediaAsset asset)
    {
        int video = asset.Streams.Count(s => s.Kind == TrackKind.Video), audio = asset.Streams.Count(s => s.Kind == TrackKind.Audio);
        return (video > 0 ? "V" : "") + (audio > 0 ? (video > 0 ? " + " : "") + (audio > 1 ? "A x" + audio : "A") : "");
    }

    private static string AssetMeta(MediaAsset asset)
    {
        var video = asset.Streams.FirstOrDefault(s => s.Kind == TrackKind.Video);
        var audio = asset.Streams.FirstOrDefault(s => s.Kind == TrackKind.Audio);
        string text = video is not null ? $"{video.Codec} {video.Width}x{video.Height}"
            : audio is not null ? $"{audio.Codec} {audio.SampleRate / 1000.0:0.#}K {audio.Channels}CH" : "NO STREAMS";
        return text.ToUpperInvariant();
    }

    private static void TranscriptTab(List<UiNode> items, EditorDocument editor, double width, double top, double bottom)
    {
        double third = (width - 4) / 3, half = (width - 2) / 2;
        double controls = bottom - (editor.ProposedTextCut is null ? 58 : 28);
        if (editor.Transcript?.Transcript is null)
        {
            items.Add(Text("No transcript yet.", Pad, top, width, 18));
            items.Add(Text(NativeUiText.Ellipsize("Select media in the Media tab, then Speech.", width), Pad, top + 22, width, 18, color: Ui.Mute));
            items.Add(Button("Speech", "Transcribe", Pad, top + 50, 100, 28, Ui.Active));
            editor.TranscriptPageSize = 1; return;
        }
        items.Add(Button("Find", "TranscriptFind", Pad, controls, third, 28));
        items.Add(Button("< Prev", "TranscriptPrev", Pad + third + 2, controls, third, 28));
        items.Add(Button("Next >", "TranscriptNext", Pad + 2 * (third + 2), controls, third, 28));
        if (editor.ProposedTextCut is null)
        {
            items.Add(Button("Keep selection", "TextRetain", Pad, controls + 30, half, 28));
            items.Add(Button("Remove selection", "TextRemove", Pad + half + 2, controls + 30, half, 28, Ui.Danger));
        }
        var transcript = editor.Transcript!.Transcript!;
        var active = new TimelineQueries(editor.Session.Snapshot).Evaluate(editor.SequenceId, editor.Playhead);
        double x = Pad, y = top, lineHeight = 24, limit = controls - 8;
        int index = editor.TranscriptFirstWord, shown = 0;
        items.Add(Text(NativeUiText.Ellipsize($"Words {index + 1}+ of {transcript.Words.Length}. Click to seek, Shift+click to select.", width, TextSize.Xs), Pad, y, width, 14, TextSize.Xs, Ui.Mute));
        y += 20;
        for (; index < transcript.Words.Length; index++)
        {
            var word = transcript.Words[index];
            string label = word.Text.Trim() + (word.TimingUsable ? "" : " [?]");
            double w = Math.Min(width, NativeUiText.Width(label) + 12);
            if (x + w > Pad + width) { x = Pad; y += lineHeight; }
            if (y + lineHeight > limit) break;
            bool selected = editor.TranscriptSelectionStart is { } a && editor.TranscriptSelectionEnd is { } b && index >= Math.Min(a, b) && index <= Math.Max(a, b);
            bool speaking = active.Any(c => c.Asset.Id == editor.Transcript!.AssetId && c.SourcePosition.CompareTo(word.Range.Start) >= 0 && c.SourcePosition.CompareTo(word.Range.End) < 0);
            items.Add(Button(NativeUiText.Ellipsize(label, w - 6), "TranscriptWord:" + index, x, y, w, lineHeight - 3,
                selected ? Ui.WordSelected : speaking ? Ui.WordSpeaking : Ui.Word, caps: false));
            x += w + 2; shown++;
        }
        editor.TranscriptPageSize = Math.Max(1, shown);
    }

    private static void RecordTab(List<UiNode> items, EditorDocument editor, double width, double top, double bottom)
    {
        double half = (width - 2) / 2;
        items.Add(Text("OBS: " + editor.ObsConnection, Pad, top, width, 18));
        items.Add(Text(NativeUiText.Ellipsize(editor.ObsRecording, width), Pad, top + 20, width, 18, color: Ui.Mute));
        double y = top + 48;
        items.Add(Button("Connect / disconnect", "ObsConnect", Pad, y, width, 28, Ui.Outline));
        items.Add(Button("Start recording", "ObsStart", Pad, y + 30, half, 28, Ui.Active));
        items.Add(Button("Stop", "ObsStop", Pad + half + 2, y + 30, half, 28));
        items.Add(Button("Pause / resume", "ObsPause", Pad, y + 60, half, 28));
        items.Add(Button("Mark highlight", "ObsMarker", Pad + half + 2, y + 60, half, 28));
        items.Add(Button("Import finished take", "ObsImport", Pad, y + 90, width, 28));
        if (y + 150 < bottom)
        {
            items.Add(Text(NativeUiText.Ellipsize("Valencia never changes OBS settings.", width), Pad, y + 128, width, 18, color: Ui.Mute));
            items.Add(Text(NativeUiText.Ellipsize("Closing Valencia does not stop OBS.", width), Pad, y + 148, width, 18, color: Ui.Mute));
        }
    }

    // ---------------------------------------------------------------- inspector

    private static IReadOnlyList<UiNode> Inspector(EditorDocument editor, Rect area, bool compact = false)
    {
        var items = new List<UiNode>(); double x = 14, width = area.Width - 28, y = Pad, labelWidth = Math.Min(112, width * .4);
        double footerY = compact ? area.Height : area.Height - 22;
        var rate = editor.Sequence.Video.FrameRate;
        void Title(string text, string info)
        {
            if (compact)
            {
                items.Add(TextIn(text + " / " + info, x, 0, width, 18, TextSize.Sm));
                y = 18;
                return;
            }
            var size = Fit(text, TextSize.H1, width * .62, 72); double h = Line(text, size);
            items.Add(Text(text, x, y - 2, width, h, size));
            double infoWidth = Math.Min(NativeUiText.Width(info, TextSize.Xs) + 2, width - NativeUiText.Width(text, size) - 12);
            if (infoWidth > 40) items.Add(Text(NativeUiText.Ellipsize(info, infoWidth, TextSize.Xs), x + width - infoWidth, y - 2 + h * .62, infoWidth, 14, TextSize.Xs, Ui.Mute, TextAlignX.Right));
            y += h - 4; items.Add(Box(0, y, area.Width, 1, Ui.Rule)); y += 4;
        }
        void Row(string name, string value, uint color = Ui.Bone)
        {
            double rowHeight = compact ? 19 : 28;
            if (y + rowHeight > footerY - 4) return;
            items.Add(TextIn(name.ToUpperInvariant(), x, y, labelWidth, rowHeight, TextSize.Sm, Ui.Mute));
            items.Add(TextIn(NativeUiText.Ellipsize(value, width - labelWidth), x + labelWidth, y, width - labelWidth, rowHeight, TextSize.Sm, color));
            y += rowHeight; items.Add(Box(x, y - 1, width, 1, Ui.RowRule));
        }
        void Control(string name, string value, string action, double startX, double cell, double label, bool advance = true)
        {
            double rowHeight = compact ? 22 : 28, side = compact ? 20 : 26;
            if (y + rowHeight > footerY) return;
            var size = compact ? TextSize.Xs : TextSize.Sm;
            items.Add(TextIn(name.ToUpperInvariant(), startX, y, label, rowHeight, size, Ui.Mute));
            double valueWidth = cell - label - 2 * side - 6;
            items.Add(TextIn(NativeUiText.Ellipsize(value, valueWidth, size), startX + label, y, valueWidth, rowHeight, size));
            items.Add(Button("-", "fx:" + action + "-", startX + cell - 2 * (side + 2), y + 1, side, rowHeight - 3));
            items.Add(Button("+", "fx:" + action + "+", startX + cell - side - 2, y + 1, side, rowHeight - 3));
            if (advance) y += rowHeight;
        }
        void Footer(string left, string right)
        {
            if (compact || footerY < y + 8) return;
            items.Add(Box(0, footerY, area.Width, 1, Ui.Rule));
            double rightWidth = Math.Min(NativeUiText.Width(right, TextSize.Xs) + 2, width * .62);
            items.Add(TextIn(NativeUiText.Ellipsize(left, width - rightWidth - 12, TextSize.Xs), x, footerY + 1, width - rightWidth - 12, 21, TextSize.Xs, Ui.Edge));
            items.Add(TextIn(NativeUiText.Ellipsize(right, rightWidth, TextSize.Xs), x + width - rightWidth, footerY + 1, rightWidth, 21, TextSize.Xs, Ui.Edge, TextAlignX.Right));
        }
        void SpeedControls(TimelineClip video)
        {
            double h = compact ? 22 : 26;
            if (y + h > footerY) return;
            var rate = video.Retime;
            double cell = (width - 6) / 4;
            var labels = new[] { "0.5x", "1x", "2x", "Freeze" };
            var actions = new[] { "half", "normal", "double", "freeze" };
            int selected = rate switch
            {
                null => 1, { Hold: not null } => 3,
                { Numerator: 1, Denominator: 2 } => 0, { Numerator: 2, Denominator: 1 } => 2, _ => -1
            };
            for (int i = 0; i < labels.Length; i++)
                items.Add(Button(labels[i], "fx:" + actions[i], x + i * (cell + 2), y + 1, cell, h - 2, i == selected ? Ui.Active : Ui.Plain));
            y += h;
        }
        if (editor.Source is { } source)
        {
            var asset = editor.Session.Snapshot.Document.Assets.FirstOrDefault(a => a.Id == source.AssetId);
            var marks = editor.SourceMarks.GetValueOrDefault(source.AssetId);
            Title("SOURCE", asset is null ? "" : $"{asset.Streams.Length} STREAMS");
            Row("Name", source.Name); Row("Duration", Timecode.Format(source.Duration, rate), Ui.Orange);
            Row("In", marks.In is { } i ? Timecode.Format(i, rate) : "-"); Row("Out", marks.Out is { } o ? Timecode.Format(o, rate) : "-");
            if (asset is not null)
            {
                Row("File", Path.GetFileName(asset.SourcePath));
                foreach (var stream in asset.Streams.Take(6))
                    Row(stream.Kind == TrackKind.Video ? $"Video #{stream.Index}" : $"Audio #{stream.Index}", stream.Kind == TrackKind.Video
                        ? $"{stream.Codec} {stream.Width}x{stream.Height}" : $"{stream.Codec} {stream.SampleRate / 1000.0:0.#} kHz {stream.Channels}ch");
            }
            Row("Edit", ", insert  . overwrite");
            Footer("SRC " + IdCode(source.AssetId), $"IN {(marks.In is { } fi ? Timecode.Format(fi, rate) : "-")} > OUT {(marks.Out is { } fo ? Timecode.Format(fo, rate) : "-")}");
            return items;
        }
        if (EditorEffects.SelectedTransform(editor) is { } target)
        {
            var pose = target.Pose;
            Title(target.Title is null ? "CLIP" : "TITLE", "TRANSFORM");
            if (!compact) Row("Name", target.Title?.Text ?? ClipName(editor, target.Clip));
            if (target.Clip.Title is { } content)
            {
                // Titles: edit the words and cycle the look (card, lower third, outline) in place.
                double rowHeight = compact ? 22 : 26, half = (width - 2) / 2;
                if (y + rowHeight <= footerY)
                {
                    items.Add(Button("Edit text", "TitleEdit", x, y + 1, half, rowHeight - 2));
                    items.Add(Button("Style " + TitleStyleName(content.Style), "TitleStyle", x + half + 2, y + 1, half, rowHeight - 2));
                    y += rowHeight;
                }
            }
            else SpeedControls(target.Clip);
            var controls = new (string Name, string Value, string Action)[]
            {
                (compact ? "X" : "Position X", $"{pose.X:P0}", "x"),
                (compact ? "Y" : "Position Y", $"{pose.Y:P0}", "y"),
                ("Scale", $"{pose.Scale:P0}", "scale"),
                (compact ? "Angle" : "Rotation", $"{pose.Rotation:0.#}°", "rotation"),
                (compact ? "Alpha" : "Opacity", $"{pose.Opacity:P0}", "opacity")
            };
            double cellWidth = compact ? (width - 6) / 2 : width;
            for (int i = 0; i < controls.Length; i++)
            {
                var (name, value, action) = controls[i];
                double start = compact && i % 2 != 0 ? x + cellWidth + 6 : x;
                Control(name, value, action, start, cellWidth, compact ? 43 : labelWidth,
                    !compact || i % 2 != 0 || i == controls.Length - 1);
            }
            double keyHeight = compact ? 22 : 26;
            if (y + keyHeight <= footerY)
            {
                double cell = (width - 4) / 3;
                items.Add(Button("Prev key", "fx:prev", x, y + 1, cell, keyHeight - 2));
                items.Add(Button("Set key", "fx:key+", x + cell + 2, y + 1, cell, keyHeight - 2, Ui.Active));
                items.Add(Button("Next key", "fx:next", x + 2 * (cell + 2), y + 1, cell, keyHeight - 2));
                y += keyHeight;
            }
            Footer("DRAG HANDLE IN MONITOR", $"{target.Curve?.Keys.Length ?? 0} KEYS");
            return items;
        }
        if (editor.Selection is { } clip)
        {
            var track = editor.Sequence.Tracks.First(t => t.Clips.Any(c => c.Id == clip.Id));
            var group = EditorEdits.Group(editor, clip.Id);
            string label = TrackLabels(editor.Sequence)[track.Id];
            Title("CLIP", label + " / " + (group.Length > 1 ? $"LINKED x{group.Length}" : "UNLINKED"));
            Row("Name", ClipName(editor, clip)); Row("Track", (track.Role is { } role ? role + " · " : "") + track.Name + (track.State.Locked ? " (locked)" : ""));
            if (track.Kind == TrackKind.Video && !track.State.Locked) SpeedControls(clip);
            if (track.Kind == TrackKind.Audio && !track.State.Locked)
                Control("Clip gain", clip.Properties.Gain == 0 ? "-inf dB" : $"{20 * Math.Log10((double)clip.Properties.Gain):+0.0;-0.0;0.0} dB",
                    "clipGain", x, width, labelWidth);
            Row("Start", Timecode.Format(clip.Start, rate)); Row("End", Timecode.Format(clip.End, rate)); Row("Duration", Timecode.Format(clip.Duration, rate), Ui.Orange);
            Row("Source in", $"{SourceTime(clip.SourceIn).TotalSeconds:0.000} s");
            Row("Linked", group.Length > 1 ? $"{group.Length - 1} other clip{(group.Length > 2 ? "s" : "")}" : "none");
            Row("Enabled", clip.Properties.Enabled ? "yes" : "no");
            if (track.Kind == TrackKind.Video) Row("Transform", "Place playhead inside clip");
            Footer(Valencia.Engine.AgentHandles.For("clip", clip.Id), Valencia.Engine.AgentHandles.For("track", track.Id));
            return items;
        }
        var sequence = editor.Sequence;
        int clips = sequence.Tracks.Sum(t => t.Clips.Length);
        Title("SEQUENCE", $"{clips} CLIPS / {Aspect(sequence.Video.Width, sequence.Video.Height)}");
        Row("Name", sequence.Name); Row("Format", $"{sequence.Video.Width}x{sequence.Video.Height}");
        Row("Frame rate", $"{Fps(rate)} fps"); Row("Audio", $"{sequence.AudioSampleRate / 1000.0:0.#} kHz");
        Row("Duration", Timecode.Format(sequence.Duration, rate), Ui.Orange);
        Row("Tracks", $"{sequence.Tracks.Count(t => t.Kind == TrackKind.Video)} video, {sequence.Tracks.Count(t => t.Kind == TrackKind.Audio)} audio");
        Row("Clips", clips.ToString(CultureInfo.InvariantCulture));
        Row("Outputs", sequence.Creator is { } creator && !creator.Profiles.IsDefaultOrEmpty ? string.Join(", ", creator.Profiles.Select(p => p.Name)) : "sequence only");
        Row("Timeline", string.Join("  ", new[] { editor.Snapping ? "snap" : "", editor.LinkedSelection ? "linked" : "unlinked", editor.Magnetic ? "magnetic" : "" }.Where(v => v.Length > 0)));
        Footer(Valencia.Engine.AgentHandles.For("sequence", editor.SequenceId), $"REV {editor.Session.Snapshot.Revision:0000}");
        return items;
    }

    // ---------------------------------------------------------------- helpers

    internal static string TitleStyleName(string style) => style switch
    {
        TitleStyles.Card => "Card", TitleStyles.LowerThird => "Lower third", _ => "Outline"
    };
    private static string ClipName(EditorDocument editor, TimelineClip clip) =>
        clip.Title is { } title ? "T  " + title.Text : string.IsNullOrWhiteSpace(clip.Properties.Name) ? editor.Session.Snapshot.Document.Assets.FirstOrDefault(a => a.Id == clip.AssetId)?.Name ?? "" : clip.Properties.Name;
    private static string? ProfileName(EditorDocument editor) => editor.Sequence.Creator?.Profiles.FirstOrDefault(p => p.Id == editor.SelectedProfile)?.Name;
    private static SequenceTime SourceTime(SourcePosition position) => new(position.Timestamp.ToSequenceTime().Ticks + position.Offset.Ticks);
    /// <summary>Short edge-code form of an id: first seven hex digits, split 4-3.</summary>
    private static string IdCode(Guid id) { string n = id.ToString("N").ToUpperInvariant(); return n[..4] + "-" + n[4..7]; }
    private static string Fps(FrameRate rate)
    {
        double fps = rate.Numerator / (double)rate.Denominator;
        return fps.ToString(Math.Abs(fps - Math.Round(fps)) < 1e-9 ? "0" : "0.##", CultureInfo.InvariantCulture);
    }
    private static string Aspect(int width, int height)
    {
        int a = Math.Max(1, width), b = Math.Max(1, height);
        while (b != 0) (a, b) = (b, a % b);
        return $"{width / a}:{height / a}";
    }
    private const double Gap = EditorLayout.Gap;
    private static string Short(string value, int count) => value.Length <= count ? value : value[..(count - 3)] + "...";

    private static double Line(string text, TextSize size) => NativeUiText.Instance.MeasureText(text, new TextStyle(Size: size)).Height;
    /// <summary>Width a command button needs for its label in the command face.</summary>
    private static double Command(string label, TextSize size = TextSize.Md) => NativeUiText.Width(label.ToUpperInvariant(), size) + 20;

    private static TextSize[] Fallback(TextSize size) => size switch
    {
        TextSize.H2 => [TextSize.H2, TextSize.H1, TextSize.Xl, TextSize.Lg, TextSize.Sm, TextSize.Xs],
        TextSize.H1 => [TextSize.H1, TextSize.Xl, TextSize.Lg, TextSize.Sm, TextSize.Xs],
        TextSize.Xl => [TextSize.Xl, TextSize.Lg, TextSize.Sm, TextSize.Xs],
        TextSize.Lg => [TextSize.Lg, TextSize.Sm, TextSize.Xs],
        TextSize.Md => [TextSize.Md, TextSize.Sm, TextSize.Xs],
        TextSize.Sm => [TextSize.Sm, TextSize.Xs],
        _ => [TextSize.Xs],
    };
    /// <summary>The largest size at or below <paramref name="size"/> whose measured text fits the box.</summary>
    private static TextSize Fit(string text, TextSize size, double width, double height) =>
        Fallback(size).FirstOrDefault(s => NativeUiText.Width(text, s) <= width && Line(text, s) <= height, TextSize.Xs);
    private static TextSize Smaller(TextSize size) => size switch
    {
        TextSize.H2 => TextSize.H1, TextSize.H1 => TextSize.Xl, TextSize.Xl => TextSize.Lg, _ => TextSize.Sm,
    };
    private static StandardButtonStyle Sized(StandardButtonStyle style, TextSize size) =>
        style.TextStyle.Size == size ? style : style with { TextStyle = style.TextStyle with { Size = size } };

    private static void Fill(List<UiNode> children, Rect r, uint color) => children.Add(Box(r.X, r.Y, r.Width, r.Height, color));
    private static UiNode Box(double x, double y, double w, double h, uint color) =>
        StandardUI.Place(UI.Rect(color: ColorToken.Hex(color)), x, y, Math.Max(1, w), Math.Max(1, h));
    private static UiNode Frame(double x, double y, double w, double h, uint color) =>
        StandardUI.Place(UI.Rect(borderColor: ColorToken.Hex(color), borderThickness: 1), x, y, Math.Max(1, w), Math.Max(1, h));

    /// <summary>Text box at least as tall as the measured line, so glyphs are never clipped.</summary>
    private static UiNode Text(string text, double x, double y, double w, double h, TextSize size = TextSize.Sm, uint color = Ui.Bone, TextAlignX alignX = TextAlignX.Left) =>
        StandardUI.Place(UI.Text(text, color: ColorToken.Hex(color), size: size, alignX: alignX), x, y, Math.Max(1, w), Math.Max(h, Line(text, size)));
    /// <summary>Text vertically centred on a row.</summary>
    private static UiNode TextIn(string text, double x, double rowY, double w, double rowHeight, TextSize size = TextSize.Sm, uint color = Ui.Bone, TextAlignX alignX = TextAlignX.Left)
    {
        double h = Line(text, size);
        return Text(text, x, rowY + (rowHeight - h) / 2, w, h, size, color, alignX);
    }
    /// <summary>
    /// Command labels keep their face when space is tight. Native presentation fits the ink in the button bounds.
    /// Node identity defaults to the action; pass a key when the same action appears in two places in one frame.
    /// </summary>
    private static UiNode Button(string text, string action, double x, double y, double w, double h, StandardButtonStyle? style = null, string? key = null, bool caps = true)
    {
        style ??= Ui.Plain;
        string label = caps ? text.ToUpperInvariant() : text;
        if (style.TextStyle.Size != TextSize.Md)
            style = Sized(style, Fit(label, style.TextStyle.Size, w - 6, h));
        return key is null ? StandardUI.Button(label, UiAction.Named(action), x, y, w, h, style: style)
            : StandardUI.Button(label, UiAction.Named(action), x, y, w, h, id: new NodeId(key).Child(action), style: style);
    }
}

/// <summary>Non-drop-frame timecode for display only; authoritative time stays in exact ticks.</summary>
internal static class Timecode
{
    public static string Format(SequenceTime time, FrameRate rate)
    {
        long fps = Math.Max(1, (long)Math.Round(rate.Numerator / (double)rate.Denominator));
        long frames = TimelineTime.FrameIndex(time, rate, TimeRounding.Floor);
        long seconds = frames / fps, frame = frames % fps;
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}:{frame:00}";
    }
    public static string Ruler(double seconds)
    {
        long s = (long)Math.Round(seconds);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }
    public static string Duration(double seconds)
    {
        long s = (long)Math.Round(seconds);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : s >= 60 ? $"{s / 60}:{s % 60:00}" : $"{seconds:0.0}s";
    }
}
