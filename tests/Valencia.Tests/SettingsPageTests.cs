using Machina.Layout.Rows;
using Machina.Runtime.Input;
using Silk.NET.Input;
using Valencia.App.Editing;
using Xunit;

namespace Valencia.Tests;

public sealed class SettingsPageTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "valencia-settings-" + Guid.NewGuid().ToString("N") + ".json");

    private static (SettingsPage Page, EditorKeymap Keys, string Path) Open(SettingsSection section = SettingsSection.Shortcuts)
    {
        var keys = new EditorKeymap(); string path = Temp();
        var page = new SettingsPage(keys, () => "Test GPU", bindingsPath: path);
        page.Open(section);
        page.Prepare(1440, 900);
        return (page, keys, path);
    }

    private static void Click(SettingsPage page, string id)
    {
        var frame = page.Prepare(1440, 900);
        Assert.True(frame.Resolved.Nodes.TryGetValue(new NodeId(id), out var node), "missing " + id);
        var center = new PointerPoint(node.Rect.X + node.Rect.Width / 2, node.Rect.Y + node.Rect.Height / 2);
        page.Handle(new UiPointerMoved(center, null, default));
        page.Handle(new UiPointerButtonChanged(center, UiPointerButton.Primary, true, default));
        page.Handle(new UiPointerButtonChanged(center, UiPointerButton.Primary, false, default));
        page.Prepare(1440, 900);
    }

    private static IReadOnlyList<EditorAction> Press(EditorKeymap keys, params Key[] chord)
    {
        foreach (var key in chord) keys.Record(key, true);
        foreach (var key in chord.Reverse()) keys.Record(key, false);
        return keys.Read(.016);
    }

    [Fact]
    public void ClickingAShortcutCapturesTheNextChordAndSavesIt()
    {
        var (page, keys, path) = Open();
        // Rows below the fold are clipped by the scroll area and cannot be clicked; Play / stop is the first row.
        Click(page, "settings.slot.TogglePlay");
        Assert.Equal("TogglePlay", page.Capturing);
        Assert.Empty(Press(keys, Key.ControlLeft, Key.E)); // captured, not dispatched
        Assert.Null(page.Capturing);
        Assert.Contains("Play / stop is now Ctrl+E", page.Status);
        Assert.False(page.StatusIsError);
        Assert.Equal([EditorAction.TogglePlay], Press(keys, Key.ControlLeft, Key.E));
        Assert.Empty(Press(keys, Key.Space));
        // Saved and reloadable as the user's file.
        Assert.Equal([EditorAction.TogglePlay], Press(EditorKeymap.ForUser(path), Key.ControlLeft, Key.E));
    }

    [Fact]
    public void SingleKeyCaptureWorksOnTheFirstPress()
    {
        var (page, keys, _) = Open();
        page.BeginCapture("ToggleSnap");
        Press(keys, Key.N);
        Assert.Contains("Snap in timeline is now N", page.Status);
        Assert.Equal([EditorAction.ToggleSnap], Press(keys, Key.N));
    }

    [Fact]
    public void ConflictIsRejectedAndNamesTheOwner()
    {
        var (page, keys, path) = Open();
        page.BeginCapture("Export");
        Press(keys, Key.K);
        Assert.True(page.StatusIsError);
        Assert.Contains("K is already Shuttle stop", page.Status);
        Assert.Equal([EditorAction.Export], Press(keys, Key.ControlLeft, Key.M));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void EscapeCancelsCaptureThenClosesThePage()
    {
        var (page, keys, _) = Open();
        page.BeginCapture("Export");
        var escape = new UiKeyChanged(UiKey.Escape, true, false, default);
        page.Handle(escape);
        Assert.Null(page.Capturing); Assert.True(page.IsOpen); Assert.False(keys.Rebinding);
        Assert.Equal([EditorAction.Export], Press(keys, Key.ControlLeft, Key.M));
        page.Handle(escape);
        Assert.False(page.IsOpen);
    }

    [Fact]
    public void ResetRestoresDefaultsAndRemovesTheFile()
    {
        var (page, keys, path) = Open();
        page.BeginCapture("Export"); Press(keys, Key.ControlLeft, Key.E);
        Assert.True(File.Exists(path));
        Click(page, "settings.reset");
        Assert.False(File.Exists(path));
        Assert.Equal([EditorAction.Export], Press(keys, Key.ControlLeft, Key.M));
        Assert.Contains("Premiere Pro defaults", page.Status);
    }

    [Fact]
    public void FilterAndSectionsRender()
    {
        var (page, _, _) = Open();
        var frame = page.Prepare(1440, 900);
        Assert.True(frame.Resolved.Nodes.ContainsKey(new NodeId("settings.slot.RippleDelete.2")));
        Click(page, "settings.nav.About");
        Assert.Equal(SettingsSection.About, page.Section);
        Assert.False(page.Prepare(1440, 900).Resolved.Nodes.ContainsKey(new NodeId("settings.slot.Export")));
        Click(page, "settings.nav.Shortcuts");
        Click(page, "settings.close");
        Assert.False(page.IsOpen);
        // Narrow windows still lay out.
        page.Open(); page.Prepare(1280, 720); page.Open(SettingsSection.About); page.Prepare(1280, 720);
    }

    [Fact]
    public void VersionIsShownForBugReports() => Assert.False(string.IsNullOrWhiteSpace(SettingsPage.Version));
}
