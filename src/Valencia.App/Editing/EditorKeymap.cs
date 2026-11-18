using InputMan.Core;
using Silk.NET.Input;

namespace Valencia.App.Editing;

internal enum EditorAction { TogglePlay, Pause, Play, ReverseUnsupported, Split, Delete, RippleDelete, Undo, Redo, Save, SaveAs, Open, Import, New, Export, SelectTool, RazorTool, Cancel, PreviousFrame, NextFrame, ObsMarker,
    MarkIn, MarkOut, ClearMarks, PreviousEdit, NextEdit, GoToStart, GoToEnd, RippleTrimPrevious, RippleTrimNext, InsertEdit, OverwriteEdit, Lift, Extract,
    ToggleSnap, ZoomIn, ZoomOut, ZoomFit }

/// <summary>
/// Silk keyboard adapter for the editor's InputMan profile. All shortcuts come from one data-driven profile
/// (<see cref="EditorShortcuts"/>); exact modifier matching is InputMan's, not hand-switched maps.
/// </summary>
internal sealed class EditorKeymap
{
    private readonly InputManEngine engine;
    private readonly Queue<(Key Key, bool Down)> transitions = [];
    private readonly HashSet<Key> held = [];
    private double time;
    private static readonly Dictionary<Key, KeyboardKey> Keys = BuildKeys();

    public EditorKeymap() : this(EditorShortcuts.Default()) { }
    public EditorKeymap(InputProfile profile, string? loadWarning = null)
    {
        Profile = profile; LoadWarning = loadWarning;
        engine = new(profile); engine.SetMaps(EditorShortcuts.Map);
    }
    public static EditorKeymap ForUser(string? path = null)
    {
        var (profile, warning) = EditorShortcuts.Load(path ?? EditorShortcuts.DefaultUserPath);
        return new(profile, warning);
    }

    public InputProfile Profile { get; }
    /// <summary>Set when the user's shortcuts file was rejected and defaults are in use.</summary>
    public string? LoadWarning { get; }
    /// <summary>The live engine, for an InputMan rebinding session in a settings screen.</summary>
    public IInputMan Engine => engine;
    public bool Control => held.Contains(Key.ControlLeft) || held.Contains(Key.ControlRight);
    public bool Shift => held.Contains(Key.ShiftLeft) || held.Contains(Key.ShiftRight);

    public void Record(Key key, bool down) => transitions.Enqueue((key, down));

    public IReadOnlyList<EditorAction> Read(double elapsed)
    {
        time += elapsed; var result = new List<EditorAction>();
        while (transitions.TryDequeue(out var transition))
        {
            // One InputMan tick per key transition, so fast chords inside one frame keep their order.
            if (transition.Down) held.Add(transition.Key); else held.Remove(transition.Key);
            engine.Tick(Snapshot(), 0, (float)time);
            foreach (var action in Enum.GetValues<EditorAction>())
                if (engine.CurrentFrame.WasPressed(new(action.ToString()))) result.Add(action);
        }
        return result;
    }

    /// <summary>Left and right modifiers fold onto InputMan's left control, so either Ctrl satisfies Ctrl+K.</summary>
    private InputSnapshot Snapshot()
    {
        var state = new Dictionary<ControlKey, bool>();
        foreach (var key in held)
            if (Keys.TryGetValue(key, out var mapped)) state[Controls.Key(mapped)] = true;
        return new InputSnapshot(state);
    }

    public void Reset()
    {
        transitions.Clear(); held.Clear();
        engine.ResetOnFocusLoss();
        engine.Tick(InputSnapshot.Empty, 0, (float)time);
    }

    private static Dictionary<Key, KeyboardKey> BuildKeys()
    {
        var keys = new Dictionary<Key, KeyboardKey>();
        // Letters, digits and F-keys share names between Silk and InputMan.
        foreach (var key in Enum.GetValues<Key>())
            if (Enum.TryParse<KeyboardKey>(key.ToString(), out var same) && same != KeyboardKey.Unknown) keys[key] = same;
        foreach (var (silk, portable) in new (Key, KeyboardKey)[]
        {
            (Key.Up, KeyboardKey.ArrowUp), (Key.Down, KeyboardKey.ArrowDown), (Key.Left, KeyboardKey.ArrowLeft), (Key.Right, KeyboardKey.ArrowRight),
            (Key.BackSlash, KeyboardKey.Backslash), (Key.GraveAccent, KeyboardKey.Grave), (Key.KeypadEnter, KeyboardKey.Enter),
            (Key.ControlLeft, KeyboardKey.LeftControl), (Key.ControlRight, KeyboardKey.LeftControl),
            (Key.ShiftLeft, KeyboardKey.LeftShift), (Key.ShiftRight, KeyboardKey.LeftShift),
            (Key.AltLeft, KeyboardKey.LeftAlt), (Key.AltRight, KeyboardKey.LeftAlt),
            (Key.SuperLeft, KeyboardKey.LeftMeta), (Key.SuperRight, KeyboardKey.LeftMeta),
        }) keys[silk] = portable;
        return keys;
    }
}
