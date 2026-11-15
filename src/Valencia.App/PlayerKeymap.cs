using InputMan.Core;
using Silk.NET.Input;
using Valencia.Core;
using Input = InputMan.Core.Input;

namespace Valencia.App;

internal sealed class PlayerKeymap
{
    private readonly InputManEngine engine;
    private double time;
    private readonly Queue<(Key Key, bool Pressed)> transitions = new();
    private readonly Dictionary<ControlKey, bool> current = new();
    private readonly Dictionary<Key, KeyboardKey> keys = new()
    {
        [Key.Space] = KeyboardKey.Space,
        [Key.Left] = KeyboardKey.ArrowLeft,
        [Key.Right] = KeyboardKey.ArrowRight,
        [Key.O] = KeyboardKey.O,
        [Key.K] = KeyboardKey.K,
        [Key.J] = KeyboardKey.J,
        [Key.L] = KeyboardKey.L
    };

    public PlayerKeymap()
    {
        ActionMapId map = new("Player");
        var profile = Input.Profile([Input.Map(map, 0,
        [
            Bind.Action(Controls.Key(KeyboardKey.Space), new ActionId(nameof(TransportAction.TogglePlayback))),
            Bind.Action(Controls.Key(KeyboardKey.K), new ActionId(nameof(TransportAction.TogglePlayback))),
            Bind.Action(Controls.Key(KeyboardKey.O), new ActionId(nameof(TransportAction.Open))),
            Bind.Action(Controls.Key(KeyboardKey.ArrowLeft), new ActionId(nameof(TransportAction.SeekBackward))),
            Bind.Action(Controls.Key(KeyboardKey.ArrowRight), new ActionId(nameof(TransportAction.SeekForward))),
            Bind.Action(Controls.Key(KeyboardKey.J), new ActionId(nameof(TransportAction.SeekBackward))),
            Bind.Action(Controls.Key(KeyboardKey.L), new ActionId(nameof(TransportAction.SeekForward)))
        ])]);
        engine = new InputManEngine(profile);
        engine.SetMaps(map);
    }

    public void Record(Key key, bool pressed)
    {
        if (keys.ContainsKey(key))
        {
            transitions.Enqueue((key, pressed));
        }
    }

    public IReadOnlyList<TransportAction> Read(double elapsed)
    {
        time += elapsed;
        var actions = new List<TransportAction>();
        while (transitions.TryDequeue(out var transition))
        {
            current[Controls.Key(keys[transition.Key])] = transition.Pressed;
            engine.Tick(new InputSnapshot(current), 0, (float)time);
            foreach (TransportAction action in Enum.GetValues<TransportAction>())
            {
                if (engine.CurrentFrame.WasPressed(new ActionId(action.ToString())))
                {
                    actions.Add(action);
                }
            }
        }
        engine.Tick(new InputSnapshot(current), (float)elapsed, (float)time);
        return actions;
    }

    public void Reset()
    {
        transitions.Clear();
        current.Clear();
        engine.ResetOnFocusLoss();
    }
}
