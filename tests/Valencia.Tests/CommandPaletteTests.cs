using Machina.Layout.Rows;
using Machina.Pipeline;
using Machina.Presentation;
using Machina.Runtime.Input;
using Machina.Standard.Widgets;
using Machina.Testing;
using Valencia.App.Editing;
using Valencia.Rendering;
using Xunit;

namespace Valencia.Tests;

public sealed class CommandPaletteTests
{
    private static IReadOnlyList<WidgetOption> Commands(EditorDocument? editor = null) =>
        EditorWindow.BuildPaletteCommands(EditorShortcuts.Default(), editor ?? new EditorDocument(), (-14, "video platform"));

    [Fact]
    public void EveryEditorCommandIsListedOnceWithItsLiveShortcut()
    {
        var commands = Commands();
        Assert.Equal(commands.Count, commands.Select(c => c.Key).Distinct().Count());
        foreach (var action in Enum.GetValues<EditorAction>().Where(a => a is not EditorAction.CommandPalette and not EditorAction.Cancel))
            Assert.Contains(commands, c => c.Key == action.ToString());
        Assert.DoesNotContain(commands, c => c.Key == nameof(EditorAction.CommandPalette));
        Assert.Equal("Ctrl+K", commands.Single(c => c.Key == nameof(EditorAction.Split)).Detail);
        Assert.Contains("Del", commands.Single(c => c.Key == nameof(EditorAction.RippleDelete)).Detail);
        // Button-only commands and export presets are reachable too.
        Assert.Contains(commands, c => c.Key == "NormalizeMix" && c.Label.Contains("-14 LUFS", StringComparison.Ordinal));
        Assert.Contains(commands, c => c.Key.StartsWith("export-preset/", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryCommandCarriesACapsCategoryShownBeforeItsLabel()
    {
        var commands = Commands();
        Assert.All(commands, c => Assert.False(string.IsNullOrWhiteSpace(c.Category), c.Key));
        Assert.Equal("EXPORT: Vertical 1080x1920", commands.Single(c => c.Key == "export-preset/vertical-1080x1920").Text);
        Assert.Equal("PANEL: 04 (Mix)", commands.Single(c => c.Key == nameof(EditorAction.Panel4)).Text);
        Assert.Equal("EDIT: Ripple delete", commands.Single(c => c.Key == nameof(EditorAction.RippleDelete)).Text);
        // A group name finds the whole group; every ASCII category keeps the column tidy.
        Assert.All(WidgetOptionFilters.Ranked(commands, "export").Take(5), c => Assert.Equal("Export", c.Category));
        Assert.All(commands, c => Assert.True(c.Text.All(ch => ch < 128), c.Text));
    }

    [Fact]
    public void ToggleLabelsSayWhatRunningThemWillDo()
    {
        var editor = new EditorDocument { Magnetic = false };
        Assert.Equal("Turn magnetic timeline on", Commands(editor).Single(c => c.Key == "ToggleMagnet").Label);
        editor.Magnetic = true;
        Assert.Equal("Turn magnetic timeline off", Commands(editor).Single(c => c.Key == "ToggleMagnet").Label);
    }

    [Theory]
    [InlineData("ripple", "RippleDelete")]
    [InlineData("rd", "RippleDelete")]
    [InlineData("split", "Split")]
    [InlineData("ctrl+k", "Split")]
    [InlineData("normal", "NormalizeMix")]
    [InlineData("whisper", "Transcribe")]
    [InlineData("mix", "Panel4")]
    public void RankedSearchPutsTheIntendedCommandFirst(string query, string expected) =>
        Assert.Equal(expected, WidgetOptionFilters.Ranked(Commands(), query).First(c => !c.Disabled).Key);

    [Fact]
    public void TypingThenEnterInThePaletteRunsTheTopMatch()
    {
        var commands = Commands();
        string query = "";
        using var session = new WidgetSession();
        var pipeline = new MachinaWidgetPipeline();
        MachinaPreparedPresentation Prepare() => pipeline.PrepareOverlayContent(session,
            w => EditorWindow.PaletteDeclaration(w, commands, query, 1280, 720, 64), 1280, 720, new(NativeUiText.Instance),
            style: EditorWindow.PaletteStyle);
        var frame = Prepare();
        Assert.True(session.Focus(EditorWindow.PaletteId.Child("input")));
        frame = Prepare();
        // An empty query lists commands (bounded rows), and the input owns typing.
        Assert.True(frame.Resolved.Nodes.ContainsKey(EditorWindow.PaletteId.Child("popup")));
        foreach (char c in "rippl")
        {
            var typed = session.Handle(new UiTextEntered(c.ToString()));
            query = typed.Changes.OfType<WidgetTextChanged>().Last().Text;
            frame = Prepare();
        }
        Assert.Equal("rippl", query);
        var run = session.Handle(new UiKeyChanged(UiKey.Enter, true, false, UiModifiers.None));
        Assert.Equal("RippleDelete", run.Changes.OfType<WidgetSelectionChanged>().Single().Keys.Single());
        Assert.Contains(run.Changes, c => c is WidgetOpenChanged { Open: false });
    }

    [Fact]
    public void PaletteCanRenderHeadlessly()
    {
        const int width = 1280, height = 720;
        var commands = Commands();
        using var session = new WidgetSession();
        var pipeline = new MachinaWidgetPipeline();
        var context = new EditorWindow.PaletteContext("Review.valencia", 42, null);
        MachinaPreparedPresentation Prepare() => pipeline.PrepareOverlayContent(session,
            w => EditorWindow.PaletteDeclaration(w, commands, "edit", width, height, 64, context),
            width, height, new(NativeUiText.Instance), style: EditorWindow.PaletteStyle);
        Prepare();
        Assert.True(session.Focus(EditorWindow.PaletteId.Child("input")));
        var frame = Prepare();
        Assert.Contains(EditorWindow.PaletteId.Child("popup"), frame.Resolved.Nodes.Keys);
        Assert.Contains(frame.PresentationFrame.Operations.OfType<PositionedTextOperation>(),
            text => text.Text.Contains("REVIEW.VALENCIA", StringComparison.Ordinal));

        string? previewPath = Environment.GetEnvironmentVariable("VALENCIA_PALETTE_PREVIEW_PNG");
        string path = previewPath ?? Path.Combine(Path.GetTempPath(), $"valencia-palette-{Guid.NewGuid():N}.png");
        try
        {
            var surface = HeadlessPreview.WritePng(frame, path);
            Assert.True(File.Exists(path));
            Assert.NotEqual(surface.GetPixel(0, 0), surface.GetPixel(width / 2, 75));
        }
        finally
        {
            if (previewPath is null)
                File.Delete(path);
        }
    }

    [Fact]
    public void EscapeClosesWithoutRunningAnything()
    {
        var commands = Commands();
        using var session = new WidgetSession();
        var pipeline = new MachinaWidgetPipeline();
        void Prepare() => pipeline.PrepareOverlayContent(session,
            w => EditorWindow.PaletteDeclaration(w, commands, "", 1280, 720, 64), 1280, 720, new(NativeUiText.Instance), style: EditorWindow.PaletteStyle);
        Prepare();
        session.Focus(EditorWindow.PaletteId.Child("input"));
        Prepare();
        var closed = session.Handle(new UiKeyChanged(UiKey.Escape, true, false, UiModifiers.None));
        Assert.Empty(closed.Changes.OfType<WidgetSelectionChanged>());
        Assert.Contains(closed.Changes, c => c is WidgetOpenChanged { Open: false });
    }
}
