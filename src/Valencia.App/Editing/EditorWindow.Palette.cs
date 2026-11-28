using Machina.Core.Authoring;
using Machina.Core.Nodes;
using Machina.Layout.Rows;
using Machina.Core.Semantics;
using Machina.Core.Styling;
using Machina.Pipeline;
using Machina.Runtime.Input;
using Machina.Standard.Authoring;
using Machina.Standard.Widgets;
using Valencia.Media;
using Valencia.Rendering;
using Ui = Valencia.App.Editing.EditorView.Ui;

namespace Valencia.App.Editing;

/// <summary>
/// Command palette (Ctrl+Shift+P): Machina's combobox with the ranked filter, a shortcut detail column and Enter
/// accepting the top match. Every entry runs through <see cref="Action"/>, the same path as buttons and shortcuts,
/// so the palette can never do something the editor cannot.
/// </summary>
internal sealed partial class EditorWindow
{
    internal static readonly NodeId PaletteId = new("editor.palette");
    private const double PaletteWidth = 860, PaletteRows = 10;
    private readonly MachinaWidgetPipeline palettePipeline = new();
    private WidgetSession? palette;
    private string paletteQuery = "";
    private IReadOnlyList<WidgetOption> paletteCommands = [];
    private MachinaPreparedPresentation? paletteFrame;

    internal bool PaletteOpen => palette is not null;
    internal string PaletteQuery => paletteQuery;
    internal IReadOnlyList<WidgetOption> PaletteCommands => paletteCommands;
    /// <summary>The prepared palette overlay, for tests.</summary>
    internal MachinaPreparedPresentation? PaletteFrame => paletteFrame;

    internal void OpenPalette()
    {
        if (palette is not null || Settings.IsOpen || IsFullscreen || view.IsCompact) return;
        CloseContextMenu();
        contextDirty = false; // the palette takes the overlay slot; do not let the menu's close clear it
        CancelGesture();
        keys.Reset();
        paletteQuery = "";
        paletteCommands = BuildPaletteCommands(keys.Profile, Editor, MixTarget());
        palette = new();
        RefreshPalette();
        palette.Focus(PaletteId.Child("input"));
        RefreshPalette();
        Editor.Status = "Command palette: type to filter, Enter runs the top match, Esc closes.";
    }

    internal void ClosePalette()
    {
        if (palette is null) return;
        widgetInput.SetPointerCapture(false);
        palette.Dispose();
        palette = null;
        paletteFrame = null;
        Graphics.UpdateOverlay(null);
        keys.Reset();
        lastUi = -1;
    }

    internal void HandlePalette(UiInputEvent inputEvent)
    {
        if (palette is null) return;
        if (inputEvent is UiFocusChanged { Focused: false } or UiPointerCaptureLost)
        {
            ClosePalette();
            return;
        }
        var before = (palette.Hovered, palette.Focused, palette.FocusVisible);
        var result = palette.Handle(inputEvent, clock.Elapsed);
        widgetInput.SetPointerCapture(result.CapturePointer);
        string? chosen = null;
        bool close = false, dirty = before != (palette.Hovered, palette.Focused, palette.FocusVisible) || inputEvent is UiSurfaceResized
            || inputEvent is UiPointerWheel && result.Handled;
        foreach (var change in result.Changes)
            switch (change)
            {
                case WidgetTextChanged text: paletteQuery = text.Text; dirty = true; break;
                case WidgetSelectionChanged { Keys.Length: > 0 } selection: chosen = selection.Keys[0]; break;
                case WidgetOpenChanged { Open: false }: close = true; break;
            }
        if (chosen is not null || close)
        {
            ClosePalette();
            if (chosen is not null) RunPaletteCommand(chosen);
            return;
        }
        if (dirty) RefreshPalette();
    }

    private void RunPaletteCommand(string key)
    {
        if (key.StartsWith("export-preset/", StringComparison.Ordinal)) QueueDesktopExport(key["export-preset/".Length..]);
        else Action(key);
    }

    private void RefreshPalette()
    {
        if (palette is null) return;
        var size = Graphics.Window.Size;
        if (size.X <= 0 || size.Y <= 0) return;
        double top = view.Layout.Header.Y + view.Layout.Header.Height + 24;
        var context = new PaletteContext(Path.GetFileName(Editor.ProjectPath ?? "Untitled.valencia"), Editor.Session.Snapshot.Revision, PaletteTarget(palette));
        paletteFrame = palettePipeline.PrepareOverlayContent(palette, w => PaletteDeclaration(w, paletteCommands, paletteQuery, size.X, size.Y, top, context),
            size.X, size.Y, new(NativeUiText.Instance), style: PaletteStyle);
        Graphics.UpdateOverlay(paletteFrame.PresentationFrame);
    }

    internal static readonly WidgetStyle PaletteStyle = new(ColorToken.Hex(Ui.Panel), ColorToken.Hex(Ui.Bone),
        ColorToken.Hex(Ui.Rule), ColorToken.Hex(Ui.Orange), ColorToken.Hex(Ui.Ink), ColorToken.Hex(Ui.Mute),
        ColorToken.Hex(Ui.Cyan), Height: 30, Gap: 2, Padding: 8, TextSize: TextSize.Sm);

    /// <summary>What the palette frame shows besides the list: the project, its revision and the command under focus or hover.</summary>
    internal sealed record PaletteContext(string File, long Revision, string? TargetKey);

    /// <summary>The option the keyboard or pointer is on; null means "the top match" (what Enter runs).</summary>
    private static string? PaletteTarget(WidgetSession session)
    {
        string prefix = PaletteId.Child("option").Value + ".";
        foreach (var node in new[] { session.Focused, session.Hovered })
            if (node is { } id && id.Value.StartsWith(prefix, StringComparison.Ordinal)) return id.Value[prefix.Length..];
        return null;
    }

    /// <summary>
    /// A MAGI-style slate in Edge Code terms: a title block with double end bars, a real data line, the ranked list and a
    /// target rail reading out the highlighted command. Orange is the one loud colour (title, prompt, what Enter runs);
    /// everything else is ink, rule and bone. Corner brackets and a tick strip frame it like an instrument panel.
    /// </summary>
    internal static UiNode PaletteDeclaration(WidgetBuilder w, IReadOnlyList<WidgetOption> paletteCommands, string paletteQuery,
        int width, int height, double top, PaletteContext? context = null)
    {
        context ??= new("Untitled.valencia", 0, null);
        const double pad = 10, header = 44, row = 30, list = PaletteRows * 30 + (PaletteRows - 1) * 2 + 16, strip = 12;
        double panelWidth = Math.Min(PaletteWidth, width - 32), x = Math.Round((width - panelWidth) / 2);
        double rail = panelWidth >= 640 ? 236 : 0, prompt = 22;
        double comboWidth = panelWidth - 2 * pad - prompt - (rail > 0 ? rail + pad : 0);
        double panelHeight = header + row + 4 + list + pad + strip;
        var ranked = WidgetOptionFilters.Ranked(paletteCommands, paletteQuery);
        var target = context.TargetKey is { } key ? paletteCommands.FirstOrDefault(c => c.Key == key) : ranked.FirstOrDefault(c => !c.Disabled);
        var children = new List<UiNode>();
        void Add(UiNode node, double nx, double ny, double nw, double nh) => children.Add(StandardUI.Place(node, nx, ny, Math.Max(1, nw), Math.Max(1, nh)));
        void Fill(uint color, double nx, double ny, double nw, double nh) => Add(UI.Rect(color: ColorToken.Hex(color), width: Math.Max(1, nw), height: Math.Max(1, nh)), nx, ny, nw, nh);
        void Frame(uint color, double nx, double ny, double nw, double nh) =>
            Add(UI.Rect(width: nw, height: nh, style: new UiStyle(BorderColor: ColorToken.Hex(color), BorderThickness: 1)), nx, ny, nw, nh);
        double LineHeight(string text, TextSize size) => NativeUiText.Instance.MeasureText(text, new TextStyle(Size: size)).Height;
        void Text(string text, double nx, double ny, double nw, double nh, uint color, TextSize size, TextAlignX align = TextAlignX.Left)
        {
            if (nw <= 0 || nh <= 0) return;
            Add(w.TextLine(text, nw, nh, ColorToken.Hex(color), alignX: align, size: size), nx, ny, nw, nh);
        }

        // Dim the editor, then the slate.
        Fill(0x0B0C0EB0, 0, 0, width, height);
        Fill(Ui.Ink, x, top, panelWidth, panelHeight);
        Frame(Ui.Rule, x, top, panelWidth, panelHeight);
        Fill(Ui.Orange, x, top, 3, panelHeight);
        // Corner brackets: 14 px L-marks just outside the slate.
        foreach (var (cx, cy, sx, sy) in new[] { (x - 4, top - 4, 1, 1), (x + panelWidth + 4, top - 4, -1, 1),
                     (x - 4, top + panelHeight + 4, 1, -1), (x + panelWidth + 4, top + panelHeight + 4, -1, -1) })
        {
            Fill(Ui.Mute, sx > 0 ? cx : cx - 14, sy > 0 ? cy : cy - 2, 14, 2);
            Fill(Ui.Mute, sx > 0 ? cx : cx - 2, sy > 0 ? cy : cy - 14, 2, 14);
        }

        // Title block: double end bars like a deliberation panel, the title in the display face.
        const string title = "COMMAND";
        const TextSize titleSize = TextSize.Lg;
        double titleWidth = NativeUiText.Width(title, titleSize) + 44, titleHeight = 30;
        Frame(Ui.Orange, x + pad, top + 7, titleWidth, titleHeight);
        foreach (double bx in new[] { x + pad + 4, x + pad + 9, x + pad + titleWidth - 7, x + pad + titleWidth - 12 })
            Fill(Ui.Orange, bx, top + 11, 3, titleHeight - 8);
        Text(title, x + pad + 14, top + 7, titleWidth - 28, titleHeight, Ui.Orange, titleSize, TextAlignX.Center);
        // Data line: real counts and the project, right-aligned like a readout.
        string data = $"MATCH {ranked.Count:000}/{paletteCommands.Count:000}  /  {context.File.ToUpperInvariant()}  /  REV {context.Revision:0000}";
        double dataX = x + pad + titleWidth + 16;
        Text(data, dataX, top + 7, x + panelWidth - pad - dataX, 30, Ui.Mute, TextSize.Xs, TextAlignX.Right);

        // Prompt and query.
        double rowY = top + header;
        Text(">", x + pad, rowY, prompt, row, Ui.Orange, TextSize.Lg);
        var combo = w.Combobox(PaletteId, paletteCommands, null, paletteQuery, open: true, "Command",
            filter: WidgetOptionFilters.Ranked, acceptFirst: true, maximumRows: (int)PaletteRows, width: comboWidth,
            placeholder: "Type a command or a group: export, edit, audio, panel...", showTrigger: false);
        Add(combo, x + pad + prompt, rowY, comboWidth, row);

        if (rail > 0) Rail(x + panelWidth - pad - rail, rowY, rail, row + 4 + list);

        // Tick strip along the bottom edge: every 8 px, every fifth tall.
        double stripY = top + panelHeight - strip;
        for (int i = 0; x + 8 + i * 8 < x + panelWidth - 8; i++)
            Fill(i % 5 == 0 ? Ui.Tick : Ui.Rule, x + 8 + i * 8, stripY + (i % 5 == 0 ? 2 : 6), 1, i % 5 == 0 ? 8 : 4);
        return UI.Layer(children: children);

        void Rail(double rx, double ry, double rw, double rh)
        {
            Fill(Ui.Panel, rx, ry, rw, rh);
            Frame(Ui.Rule, rx, ry, rw, rh);
            double ix = rx + 10, iw = rw - 20, y = ry + 8;
            int rank = target is null ? 0 : ranked.ToList().FindIndex(c => c.Key == target.Key) + 1;
            Text(context.TargetKey is null ? "TARGET / ENTER" : "TARGET / SELECTED", ix, y, iw, 16, Ui.Orange, TextSize.Xs);
            Text(rank > 0 ? $"{rank:00}/{ranked.Count:00}" : "--/--", ix, y, iw, 16, Ui.Mute, TextSize.Xs, TextAlignX.Right);
            y += 22;
            string group = target?.Category?.ToUpperInvariant() ?? (target is null ? "NO MATCH" : "COMMAND");
            double groupHeight = LineHeight(group, TextSize.H1);
            Text(group, ix, y, iw, groupHeight, target is null ? Ui.Mute : Ui.Card, TextSize.H1);
            y += groupHeight + 4;
            Text(target?.Label ?? "Fewer letters, or a group name.", ix, y, iw, 20, target is null ? Ui.Mute : Ui.Bone, TextSize.Sm);
            y += 30;
            Fill(Ui.Rule, ix, y, iw, 1);
            y += 10;
            (string Key, string Value)[] facts =
            [
                ("KEY", target?.Detail ?? "-"),
                ("ID", target is null ? "-" : target.Key.Replace("export-preset/", "preset/", StringComparison.Ordinal)),
                ("STATE", target is null ? "-" : target.Disabled ? "UNAVAILABLE" : "READY"),
            ];
            foreach (var (name, value) in facts)
            {
                Text(name, ix, y, 52, 18, Ui.Mute, TextSize.Xs);
                Text(value, ix + 56, y, iw - 56, 18, name == "STATE" && value == "READY" ? Ui.Bone : Ui.Edge, TextSize.Xs);
                y += 20;
            }
            // Key legend pinned to the rail's foot.
            double foot = ry + rh - 3 * 18 - 8;
            Fill(Ui.Rule, ix, foot - 8, iw, 1);
            foreach (var (keyName, meaning, loud) in new[] { ("ENTER", "RUN TARGET", true), ("UP / DOWN", "CHOOSE", false), ("ESC", "CLOSE", false) })
            {
                Text(keyName, ix, foot, 80, 18, loud ? Ui.Orange : Ui.Bone, TextSize.Xs);
                Text(meaning, ix + 84, foot, iw - 84, 18, Ui.Mute, TextSize.Xs);
                foot += 18;
            }
        }
    }

    /// <summary>
    /// Every editor command with its live shortcut (rebinds included), plus the commands that only live on buttons and menus.
    /// Labels follow the Settings screen; group names and action ids are search keywords.
    /// </summary>
    /// <summary>Settings-screen groups as short palette categories (Premiere's menu names where they fit).</summary>
    private static readonly Dictionary<string, string> PaletteGroups = new()
    {
        ["Transport"] = "Play", ["Marking"] = "Mark", ["Editing"] = "Edit", ["View"] = "View", ["Project"] = "File", ["Recording"] = "OBS",
    };

    internal static IReadOnlyList<WidgetOption> BuildPaletteCommands(InputMan.Core.InputProfile profile, EditorDocument Editor,
        (double Lufs, string Destination) target)
    {
        var shortcuts = EditorShortcuts.Slots(profile).GroupBy(slot => slot.Action)
            .ToDictionary(group => group.Key, group => string.Join(" / ", group.Select(slot => slot.Shortcut.ToString()).Distinct()));
        var commands = new List<WidgetOption>();
        foreach (var action in Enum.GetValues<EditorAction>())
        {
            if (action is EditorAction.CommandPalette or EditorAction.Cancel) continue;
            var (group, label) = EditorShortcuts.Describe(action);
            string category = action switch
            {
                EditorAction.Panel1 or EditorAction.Panel2 or EditorAction.Panel3 or EditorAction.Panel4 or EditorAction.Panel5 => "Panel",
                EditorAction.Export => "Export",
                EditorAction.KeyboardShortcuts => "App",
                _ => PaletteGroups.GetValueOrDefault(group, group),
            };
            if (category == "Panel") label = label.Replace("Panel tab ", "", StringComparison.Ordinal); // "PANEL: 04 (Mix)"
            if (action == EditorAction.Export) label = "Choose format and file";
            commands.Add(new(action.ToString(), label, Detail: shortcuts.GetValueOrDefault(action), Keywords: group + " " + action, Category: category));
        }
        string On(bool on) => on ? "off" : "on";
        var (lufs, destination) = target;
        (string Category, string Key, string Label, string Keywords)[] extra =
        [
            ("Track", "AddVideo", "Add video track", "timeline"), ("Track", "AddAudio", "Add audio track", "timeline"),
            ("Track", "TrackDeleteTarget", "Delete targeted track", "remove"), ("Track", "TrackDeleteEmpty", "Delete all empty tracks", "remove clean up"),
            ("Timeline", "ToggleLink", $"Turn linked selection {On(Editor.LinkedSelection)}", "link audio video"),
            ("Timeline", "ToggleMagnet", $"Turn magnetic timeline {On(Editor.Magnetic)}", "ripple magnet"),
            ("App", "Settings", "Settings", "preferences about"),
            ("Media", "Relink", "Relink missing media", "offline"), ("Media", "Analyze", "Analyze scenes and audio", ""),
            ("Media", "Transcribe", "Transcribe speech", "speech whisper captions"),
            ("Agent", "RoughCut", "Propose a rough cut", "silence"), ("Agent", "RunScript", "Run edit script", "copeland typescript"),
            ("Audio", "NormalizeMix", $"Normalize mix to {lufs:0} LUFS ({destination})", "loudness mix"),
            ("Audio", "ListenMix", Editor.ListenBefore ? "Listen after clip processing" : "Listen before clip processing", "eq cleanup bypass"),
            ("Audio", "ClearOver", "Clear over lamps", "meter clip"),
            ("Agent", "CreateAgentCheckpoint", "Checkpoint this revision", "snapshot"), ("Agent", "ListAgentCheckpoints", "Show project checkpoints", ""),
            ("Render", "RenderQueueStatus", "Show render queue", "export jobs"), ("Render", "RetryRenderJob", "Retry interrupted render", "export"),
            ("Render", "CancelRenderJob", "Cancel active render", "export"), ("App", "ProductDiagnostics", "System diagnostics", "gpu ffmpeg"),
            ("App", "ManageSpeechModel", "Manage speech model", "whisper download"),
        ];
        commands.AddRange(extra.Select(e => new WidgetOption(e.Key, e.Label, Keywords: e.Keywords, Category: e.Category)));
        commands.AddRange(ExportCatalog.Presets.Select(preset =>
            new WidgetOption("export-preset/" + preset.Id, preset.Name.Replace('\u00D7', 'x'), Keywords: "render preset " + preset.Encoder, Category: "Export")));
        return commands;
    }
}
