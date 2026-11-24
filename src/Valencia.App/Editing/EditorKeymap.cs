using InputMan.Core;
using InputMan.Core.Rebind;
using Silk.NET.Input;

namespace Valencia.App.Editing;

internal enum EditorAction { TogglePlay, Pause, Play, PlayReverse, Split, Delete, RippleDelete, Undo, Redo, Save, SaveAs, Open, Import, New, Export, SelectTool, RazorTool, Cancel, PreviousFrame, NextFrame, ObsMarker,
    MarkIn, MarkOut, ClearMarks, PreviousEdit, NextEdit, GoToStart, GoToEnd, RippleTrimPrevious, RippleTrimNext, InsertEdit, OverwriteEdit, Lift, Extract,
    NewSequence, NextSequence, AddSequenceMarker, NextSequenceMarker, PreviousSequenceMarker,
    ToggleSnap, ZoomIn, ZoomOut, ZoomFit, KeyboardShortcuts }

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
        LoadWarning = loadWarning;
        engine = new(profile); engine.SetMaps(EditorShortcuts.Map);
    }
    public static EditorKeymap ForUser(string? path = null)
    {
        var (profile, warning) = EditorShortcuts.Load(path ?? EditorShortcuts.DefaultUserPath);
        return new(profile, warning);
    }

    /// <summary>The live profile, including completed rebinds.</summary>
    public InputProfile Profile => engine.ExportProfile();
    public bool Rebinding => engine.IsRebinding;
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

    /// <summary>
    /// Captures the next key (with Ctrl/Shift/Alt) for one binding slot through InputMan's rebind session.
    /// Escape is reserved for cancelling; a shortcut another command already owns is rejected and named in the result.
    /// </summary>
    public IRebindSession StartRebind(string slot, Action<RebindResult> completed)
    {
        var modifiers = EditorShortcuts.Modifiers.Take(3).ToHashSet();
        var candidates = Enum.GetValues<KeyboardKey>()
            .Where(k => k is not (KeyboardKey.Unknown or KeyboardKey.Escape or KeyboardKey.LeftMeta or KeyboardKey.RightMeta
                or KeyboardKey.RightControl or KeyboardKey.RightShift or KeyboardKey.RightAlt))
            .Select(Controls.Key).ToArray();
        var session = engine.StartRebind(new RebindRequest
        {
            Map = EditorShortcuts.Map, BindingNameOrSlot = slot,
            CandidateButtons = candidates, AllowChord = true, ModifierControls = modifiers, MaxModifiers = 3,
            AllowedDevices = new HashSet<DeviceKind> { DeviceKind.Keyboard },
            ForbiddenControls = new HashSet<ControlKey> { Controls.Key(KeyboardKey.Escape) },
            ConflictPolicy = RebindConflictPolicy.Reject, Timeout = TimeSpan.FromHours(1),
        });
        session.OnCompleted += completed;
        // Seed immediately: InputMan treats keys held on the session's first tick as "already down", and this
        // keymap only ticks on key transitions, so the first real press must not be the seeding tick.
        engine.Tick(Snapshot(), 0, (float)time);
        return session;
    }

    public void Replace(InputProfile profile)
    {
        engine.ImportProfile(profile);
        engine.SetMaps(EditorShortcuts.Map);
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
