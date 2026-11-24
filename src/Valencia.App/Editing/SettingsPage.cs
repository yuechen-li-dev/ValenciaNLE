using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Aurelian.Machina;
using InputMan.Core.Rebind;
using Machina.Core.Authoring;
using Machina.Core.Nodes;
using Machina.Core.Semantics;
using Machina.Core.Styling;
using Machina.Layout.Rows;
using Machina.Pipeline;
using Machina.Runtime.Input;
using Machina.Standard.Authoring;
using Machina.Standard.Widgets;
using Machina.Standard.Windowing;
using Valencia.Rendering;
using Ui = Valencia.App.Editing.EditorView.Ui;

namespace Valencia.App.Editing;

internal enum SettingsSection { Shortcuts, About }

/// <summary>
/// Full-window settings page, built entirely from Machina.Standard widgets on the editor's own input context.
/// The editor stops drawing (and hides video) while it is open; Esc or Close returns to the timeline.
/// Shortcut changes go through InputMan rebinding on the live keymap and are saved at once.
/// </summary>
internal sealed class SettingsPage
{
    private const double Rail = 280, RowHeight = 36, GroupHeight = 40, SlotWidth = 168, LabelWidth = 340;
    private readonly WidgetSession session;
    private readonly MachinaWidgetPipeline pipeline = new();
    private readonly EditorKeymap keys;
    private readonly string bindingsPath;
    private readonly Func<string> gpu;
    private IRebindSession? rebind;
    private string? capturing;
    private string filter = "";
    private bool dirty = true;
    private static readonly Lazy<IReadOnlyList<(string Package, string Version, string License)>> Dependencies = new(LoadDependencies);

    public SettingsPage(EditorKeymap keys, Func<string> gpu, IWidgetClipboard? clipboard = null, string? bindingsPath = null)
    {
        this.keys = keys; this.gpu = gpu;
        this.bindingsPath = bindingsPath ?? EditorShortcuts.DefaultUserPath;
        session = new() { Clipboard = clipboard };
    }

    public bool IsOpen { get; private set; }
    public SettingsSection Section { get; set; } = SettingsSection.Shortcuts;
    public string Status { get; private set; } = "";
    public bool StatusIsError { get; private set; }
    public string? Capturing => capturing;
    public UiCursor Cursor => session.Cursor;
    public MachinaPreparedPresentation? Frame { get; private set; }
    internal WidgetSession Session => session;

    /// <summary>True once after anything changed that needs a new frame.</summary>
    public bool TakeDirty() { bool value = dirty; dirty = false; return value; }

    public void Open(SettingsSection section = SettingsSection.Shortcuts)
    {
        IsOpen = true; Section = section; dirty = true;
        Report(keys.LoadWarning ?? "", keys.LoadWarning is not null);
    }

    public void Close()
    {
        CancelCapture();
        IsOpen = false; dirty = true;
    }

    /// <summary>Routes one input event. Returns whether the pointer should be captured.</summary>
    public bool Handle(UiInputEvent input, TimeSpan timestamp = default)
    {
        if (!IsOpen) return false;
        dirty = true;
        if (input is UiKeyChanged { Key: UiKey.Escape, IsPressed: true, IsRepeat: false })
        {
            if (capturing is not null) { CancelCapture(); Report("Unchanged."); }
            else Close();
            return false;
        }
        // While capturing, keys belong to InputMan's rebind session (fed by the keymap), not to focused widgets.
        if (capturing is not null && input is UiKeyChanged or UiTextEntered) return false;
        if (input is UiCloseRequested) return false;
        var result = session.Handle(input, timestamp);
        session.Apply(result.Changes);
        return result.CapturePointer;
    }

    public MachinaPreparedPresentation Prepare(int width, int height, AurelianWindowChrome? chrome = null)
    {
        Frame = pipeline.Prepare(session, w => View(w, width, height, chrome), width, height, new(NativeUiText.Instance), style: Style);
        var deferred = session.DrainChanges();
        if (deferred.Length > 0) { session.Apply(deferred); dirty = true; }
        return Frame;
    }

    // ---------------------------------------------------------------- shortcuts model

    public void BeginCapture(string slot)
    {
        CancelCapture();
        capturing = slot;
        Report("Press the new shortcut for " + Label(slot) + ". Esc cancels.");
        rebind = keys.StartRebind(slot, Completed);
    }

    private void CancelCapture()
    {
        var active = rebind; rebind = null; capturing = null;
        active?.Cancel();
    }

    private void Completed(RebindResult result)
    {
        if (rebind is null) return; // cancelled
        string slot = capturing ?? "";
        rebind = null; capturing = null; dirty = true;
        string attempted = result.AttemptedControl is { } control ? EditorShortcuts.FromCapture(control, result.AttemptedModifiers).ToString() : "";
        if (!result.Succeeded)
        {
            if (result.ConflictingBindings.Count > 0)
                Report($"{attempted} is already {string.Join(", ", result.ConflictingBindings.Select(Label))}. Change that one first.", true);
            else Report(result.Error ?? "Shortcut unchanged.", result.Error is not null and not "Canceled.");
            return;
        }
        Save($"{Label(slot)} is now {attempted}.");
    }

    public void ResetToDefaults()
    {
        CancelCapture();
        keys.Replace(EditorShortcuts.Default());
        try { if (File.Exists(bindingsPath)) File.Delete(bindingsPath); Report("All shortcuts restored to Premiere Pro defaults."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Report("Defaults in use, but the saved file could not be removed: " + e.Message, true); }
    }

    private void Save(string message)
    {
        try { EditorShortcuts.Save(keys.Profile, bindingsPath); Report(message + " Saved."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Report(message + " Not saved: " + e.Message, true); }
    }

    private void Report(string message, bool error = false) { Status = message; StatusIsError = error; dirty = true; }

    private string Label(string slot)
    {
        var match = EditorShortcuts.Slots(keys.Profile).FirstOrDefault(s => s.Slot == slot);
        return match.Slot is null ? slot : EditorShortcuts.Describe(match.Action).Label;
    }

    // ---------------------------------------------------------------- view

    private static readonly WidgetStyle Style = new(ColorToken.Hex(Ui.Raise), ColorToken.Hex(Ui.Bone), ColorToken.Hex(Ui.Rule),
        ColorToken.Hex(Ui.Orange), ColorToken.Hex(Ui.Ink), ColorToken.Hex(Ui.Mute), ColorToken.Hex(Ui.Cyan), Height: 32, TextSize: TextSize.Sm);

    private UiNode View(WidgetBuilder w, int width, int height, AurelianWindowChrome? chrome)
    {
        double top = chrome?.Layout.Height ?? 0;
        var children = new List<UiNode> { Box(0, 0, width, height, Ui.Ink) };
        if (chrome is not null)
            children.Add(StandardUI.Place(WindowTitleBar.Create("window.titlebar", chrome.Layout, "Valencia  /  Settings",
                chrome.IsMaximized, chrome.Hovered, chrome.Pressed, ColorToken.Hex(Ui.Panel), ColorToken.Hex(Ui.Bone)), 0, 0, width, chrome.Layout.Height));

        // Rail: the page title set like a title card, then the sections.
        children.Add(Box(0, top, Rail, height - top, Ui.Panel));
        children.Add(Box(Rail - 1, top, 1, height - top, Ui.Rule));
        // The heading takes the largest display size that fits the rail; it never runs into the page.
        var headSize = new[] { TextSize.H2, TextSize.Xl, TextSize.Lg }.FirstOrDefault(size => NativeUiText.Width("SETTINGS", size) <= Rail - 48, TextSize.Lg);
        double headHeight = Line("SETTINGS", headSize);
        children.Add(Text("SETTINGS", 24, top + 22, Rail - 48, headSize, Ui.Bone));
        children.Add(Box(24, top + 22 + headHeight * .92, 56, 4, Ui.Orange));
        children.Add(Text("VALENCIA / PREFERENCES", 24, top + 22 + headHeight + 6, Rail - 48, TextSize.Xs, Ui.Mute));
        double navY = top + 150;
        foreach (var (section, label) in new[] { (SettingsSection.Shortcuts, "Keyboard shortcuts"), (SettingsSection.About, "About Valencia...") })
        {
            if (Section == section) children.Add(Box(12, navY + 4, 4, 24, Ui.Orange));
            children.Add(StandardUI.Place(w.Button("settings.nav." + section, label, () => { CancelCapture(); Section = section; dirty = true; }, width: Rail - 48), 24, navY, Rail - 48, 32));
            navY += 40;
        }
        children.Add(StandardUI.Place(w.Button("settings.close", "Close  Esc", Close, width: Rail - 48), 24, height - 56, Rail - 48, 32));

        double x = Rail + 40, contentWidth = Math.Max(320, Math.Min(width - x - 40, 980));
        if (Section == SettingsSection.Shortcuts) Shortcuts(w, children, x, top, contentWidth, height);
        else About(children, x, top, contentWidth, height);
        return UI.Rect(UI.Layer(children: children), width: width, height: height, style: new(ClipToBounds: true));
    }

    private void Shortcuts(WidgetBuilder w, List<UiNode> children, double x, double top, double width, int height)
    {
        children.Add(Text("KEYBOARD SHORTCUTS", x, top + 24, width, TextSize.Xl, Ui.Bone));
        children.Add(Text("Premiere Pro defaults. Click a shortcut, then press the new keys. Ctrl, Shift and Alt combine; Esc cancels.",
            x, top + 64, width, TextSize.Sm, Ui.Mute));
        children.Add(StandardUI.Place(w.Input("settings.filter", Bind.Value(() => filter, v => { filter = v; dirty = true; }), "Filter commands",
            "Filter by command or key", width: 300), x, top + 96, 300, 32));
        double reset = Math.Min(280, width - 316);
        children.Add(StandardUI.Place(w.Button("settings.reset", "Reset to Premiere defaults", ResetToDefaults, width: reset), x + width - reset, top + 96, reset, 32));
        if (Status.Length > 0)
        {
            if (StatusIsError) children.Add(Box(x, top + 142, 6, 16, Ui.Alert));
            children.Add(Text(NativeUiText.Ellipsize(Status, width - 16, TextSize.Sm), x + (StatusIsError ? 14 : 0), top + 140, width - 14, TextSize.Sm, StatusIsError ? Ui.Alert : Ui.Cyan));
        }

        double listTop = top + 176, listHeight = Math.Max(120, height - listTop - 24);
        double inner = width - WidgetBuilder.ScrollbarGutter;
        var rows = new List<UiNode>(); double y = 0;
        var slots = EditorShortcuts.Slots(keys.Profile);
        foreach (string group in EditorShortcuts.Groups)
        {
            var actions = slots.Select(s => s.Action).Distinct()
                .Where(a => EditorShortcuts.Describe(a).Group == group && Matches(a, slots)).ToArray();
            if (actions.Length == 0) continue;
            rows.Add(Text(group.ToUpperInvariant(), 0, y + 14, inner, TextSize.Xs, Ui.Orange));
            rows.Add(Box(0, y + GroupHeight - 4, inner, 1, Ui.Rule));
            y += GroupHeight;
            foreach (var action in actions)
            {
                var mine = slots.Where(s => s.Action == action).ToArray();
                bool changed = mine.Any(s => !EditorShortcuts.IsDefault(action, s.Shortcut))
                    || mine.Length != EditorShortcuts.PremiereDefaults.Count(d => d.Action == action);
                if (changed) rows.Add(Box(0, y + RowHeight / 2 - 3, 6, 6, Ui.Orange));
                string label = NativeUiText.Ellipsize(EditorShortcuts.Describe(action).Label, LabelWidth, TextSize.Sm);
                double labelHeight = Line(label, TextSize.Sm);
                rows.Add(Text(label, 14, y + 2 + (32 - labelHeight) / 2, LabelWidth, TextSize.Sm, Ui.Bone));
                double sx = LabelWidth + 24;
                foreach (var slot in mine)
                {
                    string shown = capturing == slot.Slot ? "Press keys..." : slot.Shortcut.ToString();
                    string id = slot.Slot;
                    rows.Add(StandardUI.Place(w.Button("settings.slot." + id, shown, () => BeginCapture(id), width: SlotWidth), sx, y + 2, SlotWidth, 32));
                    if (capturing == slot.Slot) rows.Add(Outline(sx - 2, y, SlotWidth + 4, 36, Ui.Orange));
                    sx += SlotWidth + 8;
                }
                y += RowHeight;
            }
            y += 8;
        }
        if (rows.Count == 0) rows.Add(Text("No command matches \"" + filter + "\".", 0, 8, inner, TextSize.Sm, Ui.Mute));
        double contentHeight = Math.Max(y, 40);
        var list = UI.Rect(UI.Layer(children: rows), width: inner, height: contentHeight);
        children.Add(StandardUI.Place(w.ScrollArea("settings.shortcuts", list, contentHeight, width, listHeight, "Keyboard shortcuts"), x, listTop, width, listHeight));
    }

    private bool Matches(EditorAction action, IReadOnlyList<(string Slot, EditorAction Action, Shortcut Shortcut)> slots)
    {
        if (filter.Trim() is not { Length: > 0 } term) return true;
        return EditorShortcuts.Describe(action).Label.Contains(term, StringComparison.OrdinalIgnoreCase)
            || slots.Any(s => s.Action == action && s.Shortcut.ToString().Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private void About(List<UiNode> children, double x, double top, double width, int height)
    {
        // Set like the editor's title cards: a heavy wordmark, an orange block, then facts as a data strip.
        double y = top + 24, head = Line("VALENCIA", TextSize.H2), square = 22;
        children.Add(Box(x, y + head * .78 - square, square, square, Ui.Orange));
        children.Add(Text("VALENCIA", x + square + 14, y, width - square - 14, TextSize.H2, Ui.Bone));
        y += head * .92;
        children.Add(Text("NON-LINEAR EDITOR FOR STREAMERS AND CREATORS", x, y, width, TextSize.Sm, Ui.Mute));
        y += 40;
        var facts = new (string Value, string Label)[]
        {
            (Version, "VERSION"), (".NET " + Environment.Version.ToString(2), "RUNTIME"), ("GPL-3.0", "LICENSE"), (Arch, "ARCHITECTURE"),
        };
        double column = width / facts.Length;
        for (int i = 0; i < facts.Length; i++)
        {
            children.Add(Text(NativeUiText.Ellipsize(facts[i].Value, column - 16, TextSize.Lg), x + i * column, y, column - 16, TextSize.Lg, i == 2 ? Ui.CardOrange : Ui.Bone));
            children.Add(Text(facts[i].Label, x + i * column, y + Line("A", TextSize.Lg) + 2, column - 16, TextSize.Xs, Ui.Mute));
        }
        y += Line("A", TextSize.Lg) + 36;
        children.Add(Box(x, y, width, 1, Ui.Rule)); y += 16;
        foreach (var (label, value) in new[]
        {
            ("GPU", gpu()), ("OS", RuntimeInformation.OSDescription),
            ("Source", "github.com/yuechen-li-dev/ValenciaNLE"),
            ("License", "GNU General Public License v3. See LICENSE.txt beside Valencia.App.exe."),
            ("Notices", "THIRD_PARTY_NOTICES.md lists every bundled component and its license."),
        })
        {
            children.Add(Text(label.ToUpperInvariant(), x, y, 120, TextSize.Xs, Ui.Mute));
            children.Add(Text(NativeUiText.Ellipsize(value, width - 140, TextSize.Sm), x + 140, y - 2, width - 140, TextSize.Sm, Ui.Bone));
            y += 28;
        }
        y += 12;
        children.Add(Text("BUNDLED COMPONENTS", x, y, width, TextSize.Xs, Ui.Orange)); y += 24;
        double col = Math.Max(240, width / 2);
        int perColumn = Math.Max(1, (int)((height - y - 24) / 22));
        var deps = Dependencies.Value;
        for (int i = 0; i < deps.Count && i < perColumn * 2; i++)
        {
            var (package, version, license) = deps[i];
            double cx = x + (i / perColumn) * col, cy = y + (i % perColumn) * 22;
            if (cx + col > x + width + 1) break;
            children.Add(Text(NativeUiText.Ellipsize($"{package} {version}", col * .62, TextSize.Xs), cx, cy, col * .62, TextSize.Xs, Ui.Bone));
            children.Add(Text(NativeUiText.Ellipsize(license, col * .34, TextSize.Xs), cx + col * .62, cy, col * .34, TextSize.Xs, Ui.Mute));
        }
    }

    internal static string Version
    {
        get
        {
            string? informational = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(SettingsPage).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (informational is null) return "DEV";
            int plus = informational.IndexOf('+');
            // "1.0.0+<commit>": show the version and a short commit, which is what a bug report needs.
            return plus < 0 ? informational : informational[..plus] + " " + informational[(plus + 1)..Math.Min(informational.Length, plus + 8)];
        }
    }

    private static string Arch => RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();

    private static IReadOnlyList<(string, string, string)> LoadDependencies()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "docs", "dependencies.json");
            if (!File.Exists(path)) return [];
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return [.. json.RootElement.EnumerateArray()
                .Where(e => e.TryGetProperty("scopes", out var scopes) && scopes.EnumerateArray().Any(s => s.GetString() == "application"))
                .Select(e => (e.GetProperty("package").GetString() ?? "", e.GetProperty("version").GetString() ?? "",
                    e.GetProperty("license").GetString() is { Length: > 0 } license && !license.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? license : "see notices"))
                .OrderBy(d => d.Item1, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return []; }
    }

    private static double Line(string text, TextSize size) => NativeUiText.Instance.MeasureText(text, new TextStyle(Size: size)).Height;
    private static UiNode Box(double x, double y, double w, double h, uint color) =>
        StandardUI.Place(UI.Rect(color: ColorToken.Hex(color)), x, y, Math.Max(1, w), Math.Max(1, h));
    private static UiNode Outline(double x, double y, double w, double h, uint color) =>
        StandardUI.Place(UI.Rect(borderColor: ColorToken.Hex(color), borderThickness: 2), x, y, Math.Max(1, w), Math.Max(1, h));
    private static UiNode Text(string text, double x, double y, double w, TextSize size, uint color) =>
        StandardUI.Place(UI.Text(text, color: ColorToken.Hex(color), size: size), x, y, Math.Max(1, w), Line(text, size));
}
