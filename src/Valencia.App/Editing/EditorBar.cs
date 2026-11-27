using Machina.Core.Styling;
using Machina.Layout.Geometry;
using Valencia.Rendering;

namespace Valencia.App.Editing;

/// <summary>One bar entry with its label at each rung of the ladder.</summary>
/// <param name="Extra">Fixed width beside the label (a state pip), kept at every rung.</param>
/// <param name="GroupStart">A wider gap opens a new group before this item.</param>
internal sealed record BarItem(string Action, string Full, string Short, string Code, bool Pinned = false, double Extra = 0, bool GroupStart = false);

internal enum BarForm { Full, Short, Code }

internal readonly record struct BarSlot(int Index, double X, double Width, string Label, int Row = 0);

/// <summary>Where every visible item goes, which items overflowed into the menu, and the menu tab's slot.</summary>
internal sealed record BarPlan(BarForm Form, IReadOnlyList<BarSlot> Slots, IReadOnlyList<int> Overflow, BarSlot? More)
{
    public static string MoreLabel(int count) => "+" + count;
}

/// <summary>
/// One law for every bar (panel tabs, toolbar): all labels share one type size and one form. If full labels do not fit,
/// the whole bar steps down a rung (full, short, code) together; the type never shrinks per item. When the last allowed
/// rung still overflows, trailing items move into a "+N" menu tab. Pinned items (the open tab, an active tool) stay on
/// the bar, taking the place of the last unpinned one. Rows fill in order; only the last row holds the menu tab.
/// </summary>
internal static class BarLayout
{
    public static BarPlan Fit(IReadOnlyList<BarItem> items, double left, double width, TextSize size, double padding,
        double gap = 2, double groupGap = 8, BarForm lowest = BarForm.Code) =>
        Fit(items, [(left, width)], size, padding, gap, groupGap, lowest);

    public static BarPlan Fit(IReadOnlyList<BarItem> items, IReadOnlyList<(double Left, double Width)> rows, TextSize size, double padding,
        double gap = 2, double groupGap = 8, BarForm lowest = BarForm.Code)
    {
        var all = Enumerable.Range(0, items.Count).ToList();
        // Each rung first tightens padding (two thirds, then half) before the labels change form.
        double tight = Math.Max(2, Math.Round(padding / 2));
        for (var form = BarForm.Full; form <= lowest; form++)
            foreach (double pad in new[] { padding, Math.Round(padding * 2 / 3), tight })
            {
                var widths = Widths(items, form, size, pad);
                if (Flow(items, widths, all, rows, gap, groupGap, reserve: 0) is { } placed && placed.Count == items.Count)
                    return new(form, Label(items, placed, form), [], null);
            }
        padding = tight;
        var w = Widths(items, lowest, size, padding);
        double moreWidth = NativeUiText.Width(BarPlan.MoreLabel(items.Count), size) + 2 * padding;
        var shown = Flow(items, w, all, rows, gap, groupGap, moreWidth + gap).Select(slot => slot.Index).ToList();
        foreach (int pinned in all.Where(i => items[i].Pinned && !shown.Contains(i)))
        {
            // Evict unpinned items from the end until the pinned one fits.
            while (shown.Count > 0 && Flow(items, w, [.. shown, pinned], rows, gap, groupGap, moreWidth + gap).Count < shown.Count + 1)
            {
                int victim = shown.FindLastIndex(i => !items[i].Pinned);
                if (victim < 0) break;
                shown.RemoveAt(victim);
            }
            shown.Add(pinned);
            shown.Sort();
        }
        int lastRow = rows.Count - 1;
        double rowEnd = rows[lastRow].Left + rows[lastRow].Width;
        while (true)
        {
            var slots = Label(items, Flow(items, w, shown, rows, gap, groupGap, moreWidth + gap), lowest);
            var overflow = all.Except(slots.Select(slot => slot.Index)).ToList();
            if (overflow.Count == 0) return new(lowest, slots, [], null);
            var onLast = slots.Where(slot => slot.Row == lastRow).ToList();
            double end = onLast.Count == 0 ? rows[lastRow].Left : onLast[^1].X + onLast[^1].Width + gap;
            string label = BarPlan.MoreLabel(overflow.Count);
            double labelWidth = NativeUiText.Width(label, size) + 2 * padding;
            if (end + labelWidth <= rowEnd + LinearFit.Tolerance)
                return new(lowest, slots, overflow, new BarSlot(-1, end, labelWidth, label, lastRow));
            // Every overflowed item must stay reachable: make room for the menu tab by moving the last unpinned
            // visible item into it, rather than dropping the menu and stranding the overflow.
            // Only pinned items left and still no room: a menu tab overlapping them would be worse, so the bar is
            // simply too narrow to offer the menu (the degenerate case; normal widths always reach the branch above).
            int victim = shown.FindLastIndex(i => !items[i].Pinned);
            if (victim < 0) return new(lowest, slots, overflow, null);
            shown.RemoveAt(victim);
        }
    }

    public static string Label(BarItem item, BarForm form) => form switch { BarForm.Full => item.Full, BarForm.Short => item.Short, _ => item.Code };

    private static List<BarSlot> Label(IReadOnlyList<BarItem> items, List<BarSlot> slots, BarForm form) =>
        slots.Select(slot => slot with { Label = Label(items[slot.Index], form) }).ToList();

    private static double[] Widths(IReadOnlyList<BarItem> items, BarForm form, TextSize size, double padding) =>
        items.Select(item => NativeUiText.Width(Label(item, form).ToUpperInvariant(), size) + 2 * padding + item.Extra).ToArray();

    /// <summary>Places chosen items in order across the rows; stops at the first item that does not fit.
    /// <paramref name="reserve"/> is kept free at the end of the last row (for the menu tab).</summary>
    private static List<BarSlot> Flow(IReadOnlyList<BarItem> items, double[] widths, IReadOnlyList<int> chosen,
        IReadOnlyList<(double Left, double Width)> rows, double gap, double groupGap, double reserve)
    {
        var measured = chosen.Select(index => new LinearItem(widths[index], items[index].GroupStart ? groupGap : 0)).ToArray();
        var lanes = rows.Select(row => new LinearLane(row.Left, Math.Max(0, row.Width))).ToArray();
        return LinearFit.Place(measured, lanes, gap, reserve)
            .Select((slot, index) => new BarSlot(chosen[index], slot.Start, slot.Length, "", slot.Lane))
            .ToList();
    }
}
