using InputMan.Core;
using Silk.NET.Input;
using Valencia.App.Editing;
using Xunit;

namespace Valencia.Tests;

public sealed class EditorShortcutTests
{
    private static IReadOnlyList<EditorAction> Press(EditorKeymap keys, params Key[] chord)
    {
        foreach (var key in chord) keys.Record(key, true);
        foreach (var key in chord.Reverse()) keys.Record(key, false);
        return keys.Read(.016);
    }

    public static readonly TheoryData<Key[], string[]> PremiereChords = new()
    {
        { [Key.K], ["Pause"] },
        { [Key.ControlLeft, Key.K], ["Split"] },
        { [Key.Z], [] },
        { [Key.ControlLeft, Key.Z], ["Undo"] },
        { [Key.ControlRight, Key.Z], ["Undo"] },
        { [Key.ControlLeft, Key.ShiftRight, Key.Z], ["Redo"] },
        { [Key.Delete], ["Delete"] },
        { [Key.Backspace], ["Delete"] },
        { [Key.ShiftLeft, Key.Delete], ["RippleDelete"] },
        { [Key.AltLeft, Key.Backspace], ["RippleDelete"] },
        { [Key.ControlLeft, Key.M], ["Export"] },
        { [Key.ControlLeft, Key.E], [] },
        { [Key.ControlLeft, Key.AltLeft, Key.N], ["New"] },
        { [Key.ControlLeft, Key.N], ["NewSequence"] },
        { [Key.ControlLeft, Key.Tab], ["NextSequence"] },
        { [Key.ControlLeft, Key.I], ["Import"] },
        { [Key.I], ["MarkIn"] },
        { [Key.Comma], ["InsertEdit"] },
        { [Key.Period], ["OverwriteEdit"] },
        { [Key.Semicolon], ["Lift"] },
        { [Key.Apostrophe], ["Extract"] },
        { [Key.S], ["ToggleSnap"] },
        { [Key.ControlLeft, Key.S], ["Save"] },
        { [Key.ControlLeft, Key.ShiftLeft, Key.S], ["SaveAs"] },
        { [Key.Equal], ["ZoomIn"] },
        { [Key.Minus], ["ZoomOut"] },
        { [Key.BackSlash], ["ZoomFit"] },
        { [Key.Q], ["RippleTrimPrevious"] },
        { [Key.Home], ["GoToStart"] },
        { [Key.Up], ["PreviousEdit"] },
        { [Key.ControlLeft, Key.ShiftLeft, Key.X], ["ClearMarks"] },
        { [Key.M], ["AddSequenceMarker"] },
        { [Key.ShiftLeft, Key.M], ["NextSequenceMarker"] },
        { [Key.ControlLeft, Key.ShiftLeft, Key.M], ["PreviousSequenceMarker"] },
        { [Key.AltLeft, Key.ShiftLeft, Key.M], ["ObsMarker"] },
        { [Key.SuperLeft, Key.S], [] },
        { [Key.AltLeft, Key.K], [] },
    };

    [Theory]
    [MemberData(nameof(PremiereChords))]
    public void PremiereDefaultsFireExactlyOneCommand(Key[] chord, string[] expected) =>
        Assert.Equal(expected, Press(new EditorKeymap(), chord).Select(a => a.ToString()));

    [Fact]
    public void DefaultTableCoversEveryCommandWithoutConflicts()
    {
        Assert.Empty(EditorShortcuts.Conflicts(EditorShortcuts.PremiereDefaults));
        Assert.Equal(Enum.GetValues<EditorAction>().Order(), EditorShortcuts.PremiereDefaults.Select(e => e.Action).Distinct().Order());
        string Line((EditorAction Action, Shortcut Shortcut) e) => $"{e.Action}={e.Shortcut}";
        Assert.Equal(EditorShortcuts.PremiereDefaults.Select(Line), EditorShortcuts.Table(EditorShortcuts.Default()).Select(Line));
    }

    [Fact]
    public void HoldingAKeyWhileReleasingItsModifierDoesNotFireThePlainCommand()
    {
        var keys = new EditorKeymap();
        keys.Record(Key.ControlLeft, true); keys.Record(Key.K, true); keys.Record(Key.ControlLeft, false); keys.Record(Key.K, false);
        Assert.Equal([EditorAction.Split], keys.Read(.016));
    }

    [Fact]
    public void ShortcutsDisplayInPremiereNotation()
    {
        Assert.Equal("Ctrl+Shift+Z", new Shortcut(KeyboardKey.Z, ShortcutModifiers.Ctrl | ShortcutModifiers.Shift).ToString());
        Assert.Equal("Alt+Backspace", new Shortcut(KeyboardKey.Backspace, ShortcutModifiers.Alt).ToString());
        Assert.Equal(",", new Shortcut(KeyboardKey.Comma).ToString());
        Assert.Equal("Ctrl+1", new Shortcut(KeyboardKey.Number1, ShortcutModifiers.Ctrl).ToString());
    }

    [Fact]
    public void UserFileOverridesPerCommandAndKeepsOtherDefaults()
    {
        string path = Temp();
        EditorShortcuts.Save(EditorShortcuts.Profile([(EditorAction.Export, new(KeyboardKey.E, ShortcutModifiers.Ctrl))]), path);
        var keys = EditorKeymap.ForUser(path);
        Assert.Null(keys.LoadWarning);
        Assert.Equal([EditorAction.Export], Press(keys, Key.ControlLeft, Key.E));
        Assert.Empty(Press(keys, Key.ControlLeft, Key.M));
        Assert.Equal([EditorAction.Split], Press(keys, Key.ControlLeft, Key.K));
    }

    [Fact]
    public void LegacyObsBindingMigratesWhileUserBindingsCanKeepNewlyClaimedChords()
    {
        string path = Temp();
        try
        {
            EditorShortcuts.Save(EditorShortcuts.Profile([
                (EditorAction.ObsMarker, new(KeyboardKey.M, ShortcutModifiers.Ctrl | ShortcutModifiers.Shift)),
                (EditorAction.Export, new(KeyboardKey.N, ShortcutModifiers.Ctrl))
            ]), path);
            var keys = EditorKeymap.ForUser(path);
            Assert.Null(keys.LoadWarning);
            Assert.Equal([EditorAction.PreviousSequenceMarker], Press(keys, Key.ControlLeft, Key.ShiftLeft, Key.M));
            Assert.Equal([EditorAction.ObsMarker], Press(keys, Key.AltLeft, Key.ShiftLeft, Key.M));
            Assert.Equal([EditorAction.Export], Press(keys, Key.ControlLeft, Key.N));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ConflictingUserFileFallsBackToDefaultsWithAWarning()
    {
        string path = Temp();
        EditorShortcuts.Save(EditorShortcuts.Profile([(EditorAction.Export, new(KeyboardKey.K))]), path);
        var keys = EditorKeymap.ForUser(path);
        Assert.Contains("K is bound to", keys.LoadWarning);
        Assert.Equal([EditorAction.Pause], Press(keys, Key.K));
        Assert.Equal([EditorAction.Export], Press(keys, Key.ControlLeft, Key.M));
    }

    [Fact]
    public void UnreadableOrForeignUserFilesFallBackToDefaults()
    {
        string garbage = Temp(); File.WriteAllText(garbage, "{ not json");
        Assert.NotNull(EditorKeymap.ForUser(garbage).LoadWarning);
        string foreign = Temp();
        var profile = EditorShortcuts.Profile([]);
        profile.Maps[EditorShortcuts.MapName].Bindings.Add(Bind.Action(Controls.Mouse(InputMan.Core.MouseButton.Primary), new ActionId("Export"), name: "Export"));
        profile.Maps[EditorShortcuts.MapName].Bindings.Add(Bind.Action(Controls.Key(KeyboardKey.P), new ActionId("Teleport"), name: "Teleport"));
        EditorShortcuts.Save(profile, foreign);
        var keys = EditorKeymap.ForUser(foreign);
        Assert.Contains("only keyboard keys", keys.LoadWarning);
        Assert.Contains("unknown command 'Teleport'", keys.LoadWarning);
        Assert.Equal([EditorAction.Export], Press(keys, Key.ControlLeft, Key.M));
        Assert.Null(EditorKeymap.ForUser(Temp()).LoadWarning);
    }

    private static string Temp() => Path.Combine(Path.GetTempPath(), "valencia-shortcuts-" + Guid.NewGuid().ToString("N") + ".json");
}
