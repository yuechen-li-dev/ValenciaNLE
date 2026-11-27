using Machina.Core.Actions;
using Machina.Presentation;
using Machina.Core.Styling;
using Valencia.App.Editing;
using Valencia.Rendering;
using Xunit;

namespace Valencia.Tests;

public sealed class BarLayoutTests
{
    private static readonly BarItem[] Tabs =
    [
        new("Tab:Media", "01 Media", "01 Med", "01"), new("Tab:Transcript", "02 Transcript", "02 Txt", "02"),
        new("Tab:Record", "03 Record", "03 Rec", "03"), new("Tab:Mix", "04 Mix", "04 Mix", "04"),
        new("Tab:Controls", "05 Controls", "05 Ctl", "05"),
    ];

    private static double Need(BarForm form, double padding = 7, double gap = 4) =>
        Tabs.Sum(t => NativeUiText.Width(BarLayout.Label(t, form).ToUpperInvariant(), TextSize.Md) + 2 * padding) + gap * (Tabs.Length - 1);

    [Fact]
    public void TheWholeBarStepsDownOneRungTogether()
    {
        var wide = BarLayout.Fit(Tabs, 0, Need(BarForm.Full) + 1, TextSize.Md, 7, 4, 0);
        Assert.Equal(BarForm.Full, wide.Form);
        Assert.All(wide.Slots, slot => Assert.Equal(Tabs[slot.Index].Full, slot.Label));

        // Too narrow for full names even with tight padding: every tab goes to its short code, none keeps its long name.
        var mid = BarLayout.Fit(Tabs, 0, Need(BarForm.Short, 5) + 1, TextSize.Md, 7, 4, 0);
        Assert.Equal(BarForm.Short, mid.Form);
        Assert.Equal(5, mid.Slots.Count);
        Assert.All(mid.Slots, slot => Assert.Equal(Tabs[slot.Index].Short, slot.Label));
        Assert.Empty(mid.Overflow);
    }

    [Fact]
    public void OverflowKeepsThePinnedTabAndCollectsTheRestBehindAMenuTab()
    {
        var pinned = Tabs.Select((t, i) => i == 4 ? t with { Pinned = true } : t).ToArray();
        var plan = BarLayout.Fit(pinned, 0, Need(BarForm.Code, 4) * .6, TextSize.Md, 7, 4, 0);
        Assert.Equal(BarForm.Code, plan.Form);
        Assert.Contains(plan.Slots, slot => slot.Index == 4);
        Assert.NotEmpty(plan.Overflow);
        Assert.Equal(Tabs.Length, plan.Slots.Count + plan.Overflow.Count);
        Assert.NotNull(plan.More);
        Assert.Equal("+" + plan.Overflow.Count, plan.More!.Value.Label);
        Assert.True(plan.More.Value.X >= plan.Slots.Max(s => s.X + s.Width));
        Assert.Equal(plan.Slots.OrderBy(s => s.Index).Select(s => s.Index), plan.Slots.Select(s => s.Index)); // bar order kept
    }

    [Fact]
    public void OverflowButtonDoesNotExtendBeyondAnExtremelyNarrowBar()
    {
        var plan = BarLayout.Fit(Tabs, 10, 1, TextSize.Md, 7, 4, 0);

        Assert.Empty(plan.Slots);
        Assert.Equal(Tabs.Length, plan.Overflow.Count);
        Assert.Null(plan.More);
    }

    [Fact]
    public void RowsFillInOrderAndOnlyTheLastRowHoldsTheMenuTab()
    {
        double one = Need(BarForm.Full) / 2;
        var plan = BarLayout.Fit(Tabs, [(0, one), (0, one)], TextSize.Md, 7, 4, 0, BarForm.Full);
        Assert.Contains(plan.Slots, s => s.Row == 0);
        Assert.Contains(plan.Slots, s => s.Row == 1);
        Assert.True(plan.Slots.Where(s => s.Row == 0).Max(s => s.Index) < plan.Slots.Where(s => s.Row == 1).Min(s => s.Index));
        if (plan.More is { } more) Assert.Equal(1, more.Row);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(768, 1024)]
    [InlineData(390, 844)]
    public void PanelTabsShareOneSizeAndEveryTabIsReachable(int width, int height)
    {
        var editor = new EditorDocument();
        editor.MixMode = true;
        var prepared = new EditorView().Prepare(editor, new(editor), false, false, width, height);
        var labels = prepared.PresentationFrame.Operations.OfType<PositionedTextOperation>().ToDictionary(text => text.SourceId);
        var shown = prepared.Lowering.Actions.Where(entry => entry.Value.Name.StartsWith("Tab:", StringComparison.Ordinal)).ToArray();
        Assert.Contains(shown, entry => entry.Value.Name == "Tab:Mix"); // the open tab never overflows
        var sizes = shown.Select(entry => labels[entry.Key.Child("label").Value].Style.Size).Distinct().ToArray();
        Assert.Single(sizes);
        Assert.Equal(TextSize.Md, sizes[0]);
        var reachable = shown.Select(e => e.Value.Name).Concat(editor.TabOverflow.Select(t => t.Action)).ToHashSet();
        Assert.Superset(new HashSet<string> { "Tab:Media", "Tab:Transcript", "Tab:Record", "Tab:Mix" }, reachable);
        if (editor.TabOverflow.Count > 0) Assert.Contains(prepared.Lowering.Actions, entry => entry.Value.Name == "TabMore");
    }

    [Fact]
    public void OverflowAlwaysKeepsAReachableMenuTab()
    {
        // Narrow enough that the pinned tab plus "+N" barely fit: the menu tab must still be there for every overflowed item.
        var pinned = Tabs.Select((t, i) => i == 2 ? t with { Pinned = true } : t).ToArray();
        for (double width = 40; width <= Need(BarForm.Code, 4); width += 3)
        {
            var plan = BarLayout.Fit(pinned, 0, width, TextSize.Md, 7, 4, 0);
            if (plan.Overflow.Count == 0) continue;
            Assert.Equal(Tabs.Length, plan.Slots.Count + plan.Overflow.Count);
            // The menu tab is only ever missing when nothing but pinned items is left on a bar too narrow for it.
            if (plan.More is not { } more) Assert.All(plan.Slots, slot => Assert.True(pinned[slot.Index].Pinned, $"width {width}"));
            else Assert.True(more.X + more.Width <= width + .01, $"width {width}");
        }
    }

}

/// <summary>Regression: placed MSDF text ignored its alignment and all-caps display text drew ~0.19 em high.</summary>
public sealed class TypeAlignmentTests(TimelineFixture fixture) : IClassFixture<TimelineFixture>
{
    [Fact]
    public void DisplayAndCommandTextCentreOnTheirInkInsideTheirBox()
    {
        var editor = new EditorDocument();
        editor.Open(fixture.Snapshot, "fixture.valencia");
        var prepared = new EditorView().Prepare(editor, new(editor), false, false, 1280, 720);
        var texts = prepared.PresentationFrame.Operations.OfType<PositionedTextOperation>().ToArray();
        var title = texts.Single(t => t.Text == "SEQUENCE");
        Assert.Equal(TextAlignY.Center, title.Style.AlignY);
        var wordmark = texts.Single(t => t.Text == "VALENCIA");
        Assert.Equal(TextAlignY.Center, wordmark.Style.AlignY);
        // The PGM badge's label fills the badge, so its centring has a box to centre in.
        var badge = texts.Single(t => t.Text == "PGM");
        Assert.Equal(TextAlignX.Center, badge.Style.AlignX);
        Assert.Equal(NativeUiText.Width("PGM", TextSize.Md) + 14, badge.Rect.Width, 1);
        // Data text keeps its baseline grid.
        Assert.All(texts.Where(t => t.Style.Size is TextSize.Xs or TextSize.Sm && !t.SourceId.EndsWith(".label", StringComparison.Ordinal)),
            t => Assert.Equal(TextAlignY.Top, t.Style.AlignY)); // button labels centre themselves
    }
}
