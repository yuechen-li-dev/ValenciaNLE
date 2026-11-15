using InputMan.Core;
using Silk.NET.Input;
using Input = InputMan.Core.Input;

namespace Valencia.App.Editing;

internal enum EditorAction { TogglePlay, Pause, Play, ReverseUnsupported, Split, Delete, RippleDelete, Undo, Redo, Save, SaveAs, Open, Import, New, Export, SelectTool, RazorTool, Cancel, PreviousFrame, NextFrame, ObsMarker,
    MarkIn, MarkOut, ClearMarks, PreviousEdit, NextEdit, GoToStart, GoToEnd, RippleTrimPrevious, RippleTrimNext, InsertEdit, OverwriteEdit, Lift, Extract }
internal sealed class EditorKeymap
{
    private readonly InputManEngine engine;
    private readonly Queue<(Key Key, bool Down)> transitions = [];
    private readonly Dictionary<Key, bool> physical = [];
    private double time;
    private static readonly Dictionary<Key, KeyboardKey> Keys = new()
    {
        [Key.Space] = KeyboardKey.Space, [Key.J] = KeyboardKey.J, [Key.K] = KeyboardKey.K, [Key.L] = KeyboardKey.L,
        [Key.C] = KeyboardKey.C, [Key.V] = KeyboardKey.V, [Key.Z] = KeyboardKey.Z, [Key.S] = KeyboardKey.S,
        [Key.O] = KeyboardKey.O, [Key.N] = KeyboardKey.N, [Key.E] = KeyboardKey.E, [Key.I] = KeyboardKey.I, [Key.M] = KeyboardKey.M,
        [Key.Delete] = (KeyboardKey)76, [Key.Escape] = KeyboardKey.Escape, [Key.Left] = KeyboardKey.ArrowLeft, [Key.Right] = KeyboardKey.ArrowRight
    };
    public bool Control => Down(Key.ControlLeft) || Down(Key.ControlRight);
    public bool Shift => Down(Key.ShiftLeft) || Down(Key.ShiftRight);
    public EditorKeymap()
    {
        var normal = Map("Normal", (Key.Space, EditorAction.TogglePlay), (Key.J, EditorAction.ReverseUnsupported), (Key.K, EditorAction.Pause),
            (Key.L, EditorAction.Play), (Key.C, EditorAction.RazorTool), (Key.V, EditorAction.SelectTool), (Key.Delete, EditorAction.Delete),
            (Key.Escape, EditorAction.Cancel), (Key.Left, EditorAction.PreviousFrame), (Key.Right, EditorAction.NextFrame));
        var control = Map("Control", (Key.K, EditorAction.Split), (Key.Z, EditorAction.Undo), (Key.S, EditorAction.Save), (Key.O, EditorAction.Open),
            (Key.N, EditorAction.New), (Key.E, EditorAction.Export), (Key.I, EditorAction.Import));
        var both = Map("Both", (Key.Z, EditorAction.Redo), (Key.S, EditorAction.SaveAs), (Key.M, EditorAction.ObsMarker));
        var shift = Map("Shift", (Key.Delete, EditorAction.RippleDelete));
        engine = new(Input.Profile([normal, control, both, shift])); engine.SetMaps(new ActionMapId("Normal"));
    }
    private static ActionMapDefinition Map(string name, params (Key Key, EditorAction Action)[] items) => Input.Map(new ActionMapId(name), 0,
        [.. items.Select(i => Bind.Action(Controls.Key(Keys[i.Key]), new ActionId(i.Action.ToString())))]);
    public void Record(Key key, bool down) => transitions.Enqueue((key, down));
    private bool Down(Key key) => physical.GetValueOrDefault(key);
    public IReadOnlyList<EditorAction> Read(double elapsed)
    {
        time += elapsed; var result = new List<EditorAction>();
        while (transitions.TryDequeue(out var transition))
        {
            bool wasDown = Down(transition.Key);
            physical[transition.Key] = transition.Down;
            // Premiere editing keys outside InputMan's key enum are matched directly on the key-down edge.
            if (transition.Down && !wasDown && Direct(transition.Key) is { } direct) result.Add(direct);
            engine.SetMaps(new ActionMapId(Control ? Shift ? "Both" : "Control" : Shift ? "Shift" : "Normal"));
            var state = Keys.ToDictionary(k => Controls.Key(k.Value), k => Down(k.Key));
            engine.Tick(new InputSnapshot(state), 0, (float)time);
            foreach (var action in Enum.GetValues<EditorAction>()) if (engine.CurrentFrame.WasPressed(new(action.ToString()))) result.Add(action);
        }
        return result;
    }
    private EditorAction? Direct(Key key) => (key, Control, Shift) switch
    {
        (Key.I, false, false) => EditorAction.MarkIn,
        (Key.O, false, false) => EditorAction.MarkOut,
        (Key.X, true, true) => EditorAction.ClearMarks,
        (Key.Q, false, false) => EditorAction.RippleTrimPrevious,
        (Key.W, false, false) => EditorAction.RippleTrimNext,
        (Key.Up, false, false) => EditorAction.PreviousEdit,
        (Key.Down, false, false) => EditorAction.NextEdit,
        (Key.Home, false, false) => EditorAction.GoToStart,
        (Key.End, false, false) => EditorAction.GoToEnd,
        (Key.Comma, false, false) => EditorAction.InsertEdit,
        (Key.Period, false, false) => EditorAction.OverwriteEdit,
        (Key.Semicolon, false, false) => EditorAction.Lift,
        (Key.Apostrophe, false, false) => EditorAction.Extract,
        _ => null
    };
    public void Reset() { transitions.Clear(); physical.Clear(); engine.ResetOnFocusLoss(); }
}
