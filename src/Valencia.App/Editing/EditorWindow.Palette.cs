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
    private const double PaletteWidth = 640, PaletteRows = 10;
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
        paletteFrame = palettePipeline.PrepareOverlayContent(palette, w => PaletteDeclaration(w, paletteCommands, paletteQuery, size.X, size.Y, top),
            size.X, size.Y, new(NativeUiText.Instance), style: PaletteStyle);
        Graphics.UpdateOverlay(paletteFrame.PresentationFrame);
    }

    internal static readonly WidgetStyle PaletteStyle = new(ColorToken.Hex(Ui.Panel), ColorToken.Hex(Ui.Bone),
        ColorToken.Hex(Ui.Rule), ColorToken.Hex(Ui.Orange), ColorToken.Hex(Ui.Ink), ColorToken.Hex(Ui.Mute),
        ColorToken.Hex(Ui.Cyan), Height: 30, Gap: 2, Padding: 8, TextSize: TextSize.Sm);

    /// <summary>An Edge Code slate: dimmed editor, a numbered caption strip, the query field and its ranked list.</summary>
    internal static UiNode PaletteDeclaration(WidgetBuilder w, IReadOnlyList<WidgetOption> paletteCommands, string paletteQuery,
        int width, int height, double top)
    {
        double panelWidth = Math.Min(PaletteWidth, width - 32), x = Math.Round((width - panelWidth) / 2), inner = panelWidth - 16;
        string caption = $"CMD / {paletteCommands.Count} COMMANDS / ENTER RUNS TOP MATCH / ESC CLOSES";
        var combo = w.Combobox(PaletteId, paletteCommands, null, paletteQuery, open: true, "Command",
            filter: WidgetOptionFilters.Ranked, acceptFirst: true, maximumRows: (int)PaletteRows, width: inner,
            placeholder: "Type a command: split, export, mix, transcript...", showTrigger: false);
        UiNode Rect(uint color, double w2, double h2) => UI.Rect(color: ColorToken.Hex(color), width: w2, height: h2);
        return UI.Layer(children:
        [
            StandardUI.Place(Rect(0x0B0C0E99, width, height), 0, 0, width, height),
            StandardUI.Place(Rect(Ui.Ink, panelWidth, 64), x, top, panelWidth, 64),
            StandardUI.Place(Rect(Ui.Orange, 4, 64), x, top, 4, 64),
            StandardUI.Place(w.TextLine(caption, inner, 18, ColorToken.Hex(Ui.Mute), size: TextSize.Xs), x + 8, top + 4, inner, 18),
            StandardUI.Place(combo, x + 8, top + 26, inner, 30),
        ]);
    }

    /// <summary>
    /// Every editor command with its live shortcut (rebinds included), plus the commands that only live on buttons and menus.
    /// Labels follow the Settings screen; group names and action ids are search keywords.
    /// </summary>
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
            commands.Add(new(action.ToString(), label, Detail: shortcuts.GetValueOrDefault(action), Keywords: group + " " + action));
        }
        string On(bool on) => on ? "off" : "on";
        var (lufs, destination) = target;
        (string Key, string Label, string Keywords)[] extra =
        [
            ("AddVideo", "Add video track", "timeline track"), ("AddAudio", "Add audio track", "timeline track"),
            ("ToggleLink", $"Turn linked selection {On(Editor.LinkedSelection)}", "link audio video"),
            ("ToggleMagnet", $"Turn magnetic timeline {On(Editor.Magnetic)}", "ripple magnet"),
            ("Settings", "Settings", "preferences about"),
            ("Relink", "Relink missing media", "media offline"), ("Analyze", "Analyze media (scenes, audio)", "media"),
            ("Transcribe", "Transcribe speech", "speech whisper captions"), ("RoughCut", "Propose a rough cut", "silence agent"),
            ("RunScript", "Run edit script", "copeland typescript agent"),
            ("NormalizeMix", $"Normalize mix to {lufs:0} LUFS ({destination})", "audio loudness mix"),
            ("ListenMix", Editor.ListenBefore ? "Listen after clip processing" : "Listen before clip processing", "audio eq cleanup bypass"),
            ("ClearOver", "Clear over lamps", "audio meter clip"),
            ("CreateAgentCheckpoint", "Checkpoint this revision", "agent snapshot"), ("ListAgentCheckpoints", "Show project checkpoints", "agent"),
            ("RenderQueueStatus", "Show render queue", "export jobs"), ("RetryRenderJob", "Retry interrupted render", "export"),
            ("CancelRenderJob", "Cancel active render", "export"), ("ProductDiagnostics", "System diagnostics", "gpu ffmpeg"),
            ("ManageSpeechModel", "Manage speech model", "whisper download"),
        ];
        commands.AddRange(extra.Select(e => new WidgetOption(e.Key, e.Label, Keywords: e.Keywords)));
        commands.AddRange(ExportCatalog.Presets.Select(preset =>
            new WidgetOption("export-preset/" + preset.Id, "Export " + preset.Name, Keywords: "render preset " + preset.Encoder)));
        return commands;
    }
}
