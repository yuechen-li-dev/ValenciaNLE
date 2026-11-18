using System.Collections.Immutable;
using InputMan.Core;
using InputMan.Core.Serialization;
using Binding = InputMan.Core.Binding;

namespace Valencia.App.Editing;

[Flags]
internal enum ShortcutModifiers { None = 0, Ctrl = 1, Shift = 2, Alt = 4 }

/// <summary>One key plus an exact modifier set. Ctrl+K and K are different shortcuts (InputMan exclusive modifiers).</summary>
internal readonly record struct Shortcut(KeyboardKey Key, ShortcutModifiers Modifiers = ShortcutModifiers.None)
{
    private static readonly (ShortcutModifiers Flag, string Name, KeyboardKey Control)[] Order =
        [(ShortcutModifiers.Ctrl, "Ctrl", KeyboardKey.LeftControl), (ShortcutModifiers.Shift, "Shift", KeyboardKey.LeftShift), (ShortcutModifiers.Alt, "Alt", KeyboardKey.LeftAlt)];
    private static readonly Dictionary<KeyboardKey, string> Names = new()
    {
        [KeyboardKey.Comma] = ",", [KeyboardKey.Period] = ".", [KeyboardKey.Semicolon] = ";", [KeyboardKey.Apostrophe] = "'",
        [KeyboardKey.Equal] = "=", [KeyboardKey.Minus] = "-", [KeyboardKey.Backslash] = "\\", [KeyboardKey.Slash] = "/",
        [KeyboardKey.LeftBracket] = "[", [KeyboardKey.RightBracket] = "]", [KeyboardKey.Grave] = "`",
        [KeyboardKey.ArrowUp] = "Up", [KeyboardKey.ArrowDown] = "Down", [KeyboardKey.ArrowLeft] = "Left", [KeyboardKey.ArrowRight] = "Right",
        [KeyboardKey.Escape] = "Esc",
    };

    public ControlKey[] ModifierControls { get { var m = Modifiers; return [.. Order.Where(o => m.HasFlag(o.Flag)).Select(o => Controls.Key(o.Control))]; } }

    public override string ToString()
    {
        var m = Modifiers;
        return string.Join("+", Order.Where(o => m.HasFlag(o.Flag)).Select(o => o.Name).Append(Names.GetValueOrDefault(Key) ?? Key.ToString().Replace("Number", "")));
    }

    public static bool TryFrom(BindingTrigger trigger, out Shortcut shortcut)
    {
        shortcut = default;
        if (trigger.Type != TriggerType.Button || trigger.Control.Device != DeviceKind.Keyboard || !Enum.IsDefined((KeyboardKey)trigger.Control.Code)) return false;
        var modifiers = ShortcutModifiers.None;
        foreach (var control in trigger.Modifiers)
        {
            var match = Order.FirstOrDefault(o => Controls.Key(o.Control) == control);
            if (match.Flag == ShortcutModifiers.None) return false;
            modifiers |= match.Flag;
        }
        shortcut = new((KeyboardKey)trigger.Control.Code, modifiers);
        return true;
    }
}

/// <summary>
/// The editor's command-to-shortcut table. Premiere Pro's Windows defaults are the default profile for adoption;
/// users override per action through an InputMan profile (the format InputMan's rebinding UI saves).
/// </summary>
internal static class EditorShortcuts
{
    public const string MapName = "Editor";
    public static readonly ActionMapId Map = new(MapName);
    private const ShortcutModifiers Ctrl = ShortcutModifiers.Ctrl, Shift = ShortcutModifiers.Shift, Alt = ShortcutModifiers.Alt;

    /// <summary>Exact-match modifiers. Meta is listed so Win+key never triggers an editor command.</summary>
    public static readonly ImmutableArray<ControlKey> Modifiers =
        [Controls.Key(KeyboardKey.LeftControl), Controls.Key(KeyboardKey.LeftShift), Controls.Key(KeyboardKey.LeftAlt), Controls.Key(KeyboardKey.LeftMeta)];

    public static readonly ImmutableArray<(EditorAction Action, Shortcut Shortcut)> PremiereDefaults =
    [
        // Transport
        (EditorAction.TogglePlay, new(KeyboardKey.Space)),
        (EditorAction.ReverseUnsupported, new(KeyboardKey.J)),
        (EditorAction.Pause, new(KeyboardKey.K)),
        (EditorAction.Play, new(KeyboardKey.L)),
        (EditorAction.PreviousFrame, new(KeyboardKey.ArrowLeft)),
        (EditorAction.NextFrame, new(KeyboardKey.ArrowRight)),
        (EditorAction.PreviousEdit, new(KeyboardKey.ArrowUp)),
        (EditorAction.NextEdit, new(KeyboardKey.ArrowDown)),
        (EditorAction.GoToStart, new(KeyboardKey.Home)),
        (EditorAction.GoToEnd, new(KeyboardKey.End)),
        // Marking and three-point editing
        (EditorAction.MarkIn, new(KeyboardKey.I)),
        (EditorAction.MarkOut, new(KeyboardKey.O)),
        (EditorAction.ClearMarks, new(KeyboardKey.X, Ctrl | Shift)),
        (EditorAction.InsertEdit, new(KeyboardKey.Comma)),
        (EditorAction.OverwriteEdit, new(KeyboardKey.Period)),
        (EditorAction.Lift, new(KeyboardKey.Semicolon)),
        (EditorAction.Extract, new(KeyboardKey.Apostrophe)),
        // Timeline editing
        (EditorAction.SelectTool, new(KeyboardKey.V)),
        (EditorAction.RazorTool, new(KeyboardKey.C)),
        (EditorAction.Split, new(KeyboardKey.K, Ctrl)),
        (EditorAction.Delete, new(KeyboardKey.Delete)),
        (EditorAction.Delete, new(KeyboardKey.Backspace)),
        (EditorAction.RippleDelete, new(KeyboardKey.Delete, Shift)),
        (EditorAction.RippleDelete, new(KeyboardKey.Backspace, Alt)),
        (EditorAction.RippleTrimPrevious, new(KeyboardKey.Q)),
        (EditorAction.RippleTrimNext, new(KeyboardKey.W)),
        (EditorAction.ToggleSnap, new(KeyboardKey.S)),
        (EditorAction.ZoomIn, new(KeyboardKey.Equal)),
        (EditorAction.ZoomOut, new(KeyboardKey.Minus)),
        (EditorAction.ZoomFit, new(KeyboardKey.Backslash)),
        (EditorAction.Undo, new(KeyboardKey.Z, Ctrl)),
        (EditorAction.Redo, new(KeyboardKey.Z, Ctrl | Shift)),
        (EditorAction.Cancel, new(KeyboardKey.Escape)),
        // Project
        (EditorAction.New, new(KeyboardKey.N, Ctrl | Alt)),
        (EditorAction.Open, new(KeyboardKey.O, Ctrl)),
        (EditorAction.Save, new(KeyboardKey.S, Ctrl)),
        (EditorAction.SaveAs, new(KeyboardKey.S, Ctrl | Shift)),
        (EditorAction.Import, new(KeyboardKey.I, Ctrl)),
        (EditorAction.Export, new(KeyboardKey.M, Ctrl)),
        // Valencia-only (no Premiere equivalent)
        (EditorAction.ObsMarker, new(KeyboardKey.M, Ctrl | Shift)),
    ];

    public static string DefaultUserPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Valencia", "keybindings.json");

    public static InputProfile Profile(IEnumerable<(EditorAction Action, Shortcut Shortcut)> table)
    {
        var bindings = new List<Binding>();
        foreach (var group in table.GroupBy(e => e.Action))
        {
            int slot = 0;
            foreach (var (action, shortcut) in group)
            {
                // Stable slot names ("RippleDelete", "RippleDelete.2") are what a rebind UI addresses.
                string name = slot++ == 0 ? action.ToString() : $"{action}.{slot}";
                bindings.Add(Bind.ActionChord(Controls.Key(shortcut.Key), new ActionId(action.ToString()), ButtonEdge.Pressed, ConsumeMode.None, name, shortcut.ModifierControls));
            }
        }
        return new InputProfile
        {
            Options = new InputOptions { ExclusiveModifiers = [.. Modifiers] },
            Maps = new() { [MapName] = new ActionMapDefinition { Id = Map, CanConsume = false, Bindings = bindings } },
        };
    }

    public static InputProfile Default() => Profile(PremiereDefaults);

    /// <summary>Reads a profile back into the table. Bindings the editor cannot use are reported, not guessed at.</summary>
    public static ImmutableArray<(EditorAction Action, Shortcut Shortcut)> Table(InputProfile profile, List<string>? problems = null)
    {
        var table = ImmutableArray.CreateBuilder<(EditorAction, Shortcut)>();
        foreach (var binding in profile.Maps.GetValueOrDefault(MapName)?.Bindings ?? [])
        {
            if (binding.Output is not ActionOutput { Action.Name: var name } || !Enum.TryParse<EditorAction>(name, out var action))
                problems?.Add($"unknown command '{(binding.Output as ActionOutput)?.Action.Name ?? binding.Name}'");
            else if (!Shortcut.TryFrom(binding.Trigger, out var shortcut))
                problems?.Add($"{action}: only keyboard keys with Ctrl/Shift/Alt can be bound");
            else table.Add((action, shortcut));
        }
        return table.ToImmutable();
    }

    /// <summary>Each shortcut bound to more than one command.</summary>
    public static IEnumerable<string> Conflicts(IEnumerable<(EditorAction Action, Shortcut Shortcut)> table) =>
        table.GroupBy(e => e.Shortcut).Where(g => g.Select(e => e.Action).Distinct().Count() > 1)
            .Select(g => $"{g.Key} is bound to {string.Join(" and ", g.Select(e => e.Action).Distinct())}");

    /// <summary>
    /// Defaults with the user's file layered per command: a command present in the file takes exactly the file's
    /// shortcuts; commands the file doesn't mention (including ones added in later versions) keep their defaults.
    /// A malformed or conflicting file is ignored as a whole and reported, so the editor always starts usable.
    /// </summary>
    public static (InputProfile Profile, string? Warning) Load(string path)
    {
        if (!File.Exists(path)) return (Default(), null);
        try
        {
            var problems = new List<string>();
            var user = Table(InputProfileJson.LoadFromFile(path), problems);
            var overridden = user.Select(e => e.Action).ToHashSet();
            var merged = PremiereDefaults.Where(e => !overridden.Contains(e.Action)).Concat(user).ToArray();
            problems.AddRange(Conflicts(merged));
            if (problems.Count > 0) return (Default(), $"Keyboard shortcuts file ignored ({Path.GetFileName(path)}): {string.Join("; ", problems)}.");
            return (Profile(merged), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return (Default(), $"Keyboard shortcuts file ignored ({Path.GetFileName(path)}): {e.Message}");
        }
    }

    public static void Save(InputProfile profile, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        InputProfileJson.SaveToFile(profile, path);
    }
}
