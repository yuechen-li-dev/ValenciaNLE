using Machina.Core.Styling;
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
        var shown = Flow(items, w, all, rows, gap, groupGap, moreWidth + gap)!.Select(slot => slot.Index).ToList();
        foreach (int pinned in all.Where(i => items[i].Pinned && !shown.Contains(i)))
        {
            // Evict unpinned items from the end until the pinned one fits.
            while (shown.Count > 0 && Flow(items, w, [.. shown, pinned], rows, gap, groupGap, moreWidth + gap)!.Count < shown.Count + 1)
            {
                int victim = shown.FindLastIndex(i => !items[i].Pinned);
                if (victim < 0) break;
                shown.RemoveAt(victim);
            }
            shown.Add(pinned);
            shown.Sort();
        }
        var slots = Label(items, Flow(items, w, shown, rows, gap, groupGap, moreWidth + gap)!, lowest);
        var overflow = all.Except(slots.Select(slot => slot.Index)).ToList();
        if (overflow.Count == 0) return new(lowest, slots, [], null);
        int lastRow = rows.Count - 1;
        var onLast = slots.Where(slot => slot.Row == lastRow).ToList();
        double end = onLast.Count == 0 ? rows[lastRow].Left : onLast[^1].X + onLast[^1].Width + gap;
        string label = BarPlan.MoreLabel(overflow.Count);
        return new(lowest, slots, overflow, new BarSlot(-1, end, NativeUiText.Width(label, size) + 2 * padding, label, lastRow));
    }

    public static string Label(BarItem item, BarForm form) => form switch { BarForm.Full => item.Full, BarForm.Short => item.Short, _ => item.Code };

    private static List<BarSlot> Label(IReadOnlyList<BarItem> items, List<BarSlot> slots, BarForm form) =>
        slots.Select(slot => slot with { Label = Label(items[slot.Index], form) }).ToList();

    private static double[] Widths(IReadOnlyList<BarItem> items, BarForm form, TextSize size, double padding) =>
        items.Select(item => NativeUiText.Width(Label(item, form).ToUpperInvariant(), size) + 2 * padding + item.Extra).ToArray();

    /// <summary>Places chosen items in order across the rows; stops at the first item that does not fit.
    /// <paramref name="reserve"/> is kept free at the end of the last row (for the menu tab).</summary>
    private static List<BarSlot>? Flow(IReadOnlyList<BarItem> items, double[] widths, IReadOnlyList<int> chosen,
        IReadOnlyList<(double Left, double Width)> rows, double gap, double groupGap, double reserve)
    {
        var slots = new List<BarSlot>(chosen.Count);
        int row = 0; double x = rows[0].Left; bool first = true;
        foreach (int i in chosen)
        {
            while (true)
            {
                double start = first ? x : x + gap + (items[i].GroupStart ? groupGap : 0);
                double limit = rows[row].Left + rows[row].Width - (row == rows.Count - 1 ? reserve : 0);
                if (start + widths[i] <= limit + .001)
                {
                    slots.Add(new(i, start, widths[i], "", row));
                    x = start + widths[i]; first = false;
                    break;
                }
                if (++row >= rows.Count) return slots;
                x = rows[row].Left; first = true;
            }
        }
        return slots;
    }
}
