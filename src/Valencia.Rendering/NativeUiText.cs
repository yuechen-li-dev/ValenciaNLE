using System.Text;
using Aurelian.Machina;
using Machina.Core.Measurement;
using Machina.Core.Styling;

namespace Valencia.Rendering;

/// <summary>One font metric authority for both Machina layout and native text presentation.</summary>
public sealed class NativeUiText : ITextMeasurer
{
    public static NativeUiText Instance { get; } = new();
    private static readonly Lazy<AurelianNativeUiFont> NativeFont = new(() =>
        AurelianNativeUiFont.Create(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts")));
    internal static AurelianNativeUiFont Font => NativeFont.Value;
    internal static TextSize Size(TextSize size) => size == TextSize.H1 ? TextSize.H1 : TextSize.Sm;
    public IntrinsicSize MeasureText(string value, TextStyle style)
    {
        if (value.All(c => c is >= ' ' and <= '~' || char.IsWhiteSpace(c)))
            return Font.MeasureText(value, style with { Size = Size(style.Size) });
        // The pixel fallback's system-font coverage can differ. Reserve a full em per
        // non-ASCII scalar, including surrogate pairs, rather than measuring missing glyphs as zero.
        double em = style.Size == TextSize.H1 ? 30 : 12;
        return new(Math.Ceiling(value.EnumerateRunes().Sum(r => r.IsAscii ? em * .6 : em) + 2), Math.Ceiling(em * 1.25 + 2));
    }
    public static double Width(string value, TextSize size = TextSize.Sm) =>
        Instance.MeasureText(value, new TextStyle(Size: size)).Width;

    public static string Ellipsize(string value, double width, TextSize size = TextSize.Sm)
    {
        if (Width(value, size) <= width) return value;
        if (Width("...", size) > width) return "";
        var runes = value.EnumerateRunes().ToArray();
        int lo = 0, hi = runes.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            string candidate = string.Concat(runes.Take(mid).Select(r => r.ToString())) + "...";
            if (Width(candidate, size) <= width) lo = mid; else hi = mid - 1;
        }
        return string.Concat(runes.Take(lo).Select(r => r.ToString())) + "...";
    }

    /// <summary>Wrap whole shortcut groups. No partial glyphs or hidden suffixes.</summary>
    public static IReadOnlyList<string> WrapHints(IEnumerable<string> hints, double width)
    {
        List<string> lines = [];
        string line = "";
        foreach (string hint in hints)
        {
            string candidate = line.Length == 0 ? hint : line + " | " + hint;
            if (line.Length > 0 && Width(candidate) > width) { lines.Add(line); line = hint; }
            else line = candidate;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }
}
