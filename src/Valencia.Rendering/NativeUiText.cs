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
        AurelianNativeUiFont.Create(TypeScale.Specs(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts"))));
    internal static AurelianNativeUiFont Font => NativeFont.Value;
    internal static TextSize Size(TextSize size) => size;
    public IntrinsicSize MeasureText(string value, TextStyle style)
    {
        if (value.All(c => c is >= ' ' and <= '~' || char.IsWhiteSpace(c)))
            return TypeScale.Raster ? RasterText.Instance.Measure(value, style.Size) : Font.MeasureText(value, style with { Size = Size(style.Size) });
        // The pixel fallback's system-font coverage can differ. Reserve a full em per
        // non-ASCII scalar, including surrogate pairs, rather than measuring missing glyphs as zero.
        double em = TypeScale.Pixels(style.Size);
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

/// <summary>
/// Edge Code type scale: one MSDF atlas per Machina text size. Plex Mono carries data, Archivo Expanded carries
/// commands (the default button size), and the extra-condensed Noto Serif Display carries titles and timecode.
/// All three are SIL OFL 1.1 and ship in Assets/Fonts with their licenses.
/// </summary>
public static class TypeScale
{
    /// <summary>Fixed-size UI text is rasterized (hinted, pixel-snapped); MSDF remains available for scaled text.</summary>
    public static bool Raster { get; } = true;
    public const string Mono = "IBMPlexMono-Regular.ttf", Command = "Archivo-ExpandedBold.ttf", Display = "NotoSerifDisplay-ExtraCondensedBlack.ttf";
    public static int Pixels(TextSize size) => size switch
    {
        TextSize.Xs => 10, TextSize.Sm => 12, TextSize.Md => 11, TextSize.Lg => 20, TextSize.Xl => 32, TextSize.H1 => 48, TextSize.H2 => 64,
        _ => 12
    };
    public static string File(TextSize size) => size switch
    {
        TextSize.Xs or TextSize.Sm => Mono, TextSize.Md => Command, _ => Display
    };
    public static IEnumerable<AurelianNativeUiFont.FontSpec> Specs(string directory) =>
        new[] { TextSize.Xs, TextSize.Sm, TextSize.Md, TextSize.Lg, TextSize.Xl, TextSize.H1, TextSize.H2 }
            .Select(size => new AurelianNativeUiFont.FontSpec(size, Path.Combine(directory, File(size)),
                "valencia-" + Path.GetFileNameWithoutExtension(File(size)).ToLowerInvariant(), Pixels(size)));
}
