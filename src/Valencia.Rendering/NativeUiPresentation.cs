using Aurelian.Graphics.Vulkan.Native2D;
using Aurelian.Machina;
using Machina.Core.Styling;
using Machina.Layout.Geometry;
using Machina.Presentation;
using SkiaSharp;

namespace Valencia.Rendering;

/// <summary>
/// Narrow realization of Valencia's flat Machina panels. Geometry remains vector intent;
/// ASCII text uses Machina's existing MSDF font/atlas authority. Unicode text uses a
/// pixel-sized Skia fallback, never a stretched 1280x720 bitmap. This is not a widget toolkit.
/// </summary>
internal sealed class NativeUiPresentation
{
    public List<NativeAnalyticShapeSubmission> Shapes { get; } = [];
    public List<NativeMsdfQuadSubmission> Glyphs { get; } = [];
    public List<RasterGlyphQuad> RasterGlyphs { get; } = [];
    public byte[]? FallbackPixels { get; private set; }
    public int FallbackTextRuns { get; private set; }

    public void Prepare(MachinaPresentationFrame frame, AurelianMsdfAtlasCache atlas, int pixelWidth, int pixelHeight)
    {
        Shapes.Clear(); Glyphs.Clear(); RasterGlyphs.Clear(); FallbackPixels = null; FallbackTextRuns = 0;
        var clips = new Stack<Rect>();
        clips.Push(new(0, 0, frame.Viewport.Width, frame.Viewport.Height));
        List<(PositionedTextOperation Text, Rect Clip)> fallback = [];
        foreach (var operation in frame.Operations)
        {
            switch (operation)
            {
                case PushRectangularClipOperation push: clips.Push(Intersect(clips.Peek(), push.Rect)); break;
                case PopClipOperation: clips.Pop(); break;
                case FillRectangleOperation fill:
                    if (fill.Rect.Width > 0 && fill.Rect.Height > 0)
                        Shape(new(fill.SourceId, MachinaAnalyticShapeKind.RoundedRect, fill.Rect, fill.Color));
                    break;
                case StrokeRectangleOperation stroke:
                    if (stroke.Rect.Width > 0 && stroke.Rect.Height > 0)
                        Shape(new(stroke.SourceId, MachinaAnalyticShapeKind.RoundedRect, stroke.Rect, ColorToken.Hex(0),
                            borderColor: stroke.Color, borderWidth: stroke.Thickness));
                    break;
                case MachinaAnalyticShapePrimitive shape: Shape(shape); break;
                case PositionedTextOperation text:
                    var clip = Intersect(clips.Peek(), text.Rect);
                    if (clip.Width <= 0 || clip.Height <= 0) break;
                    if (text.Text.All(c => c is >= ' ' and <= '~' || char.IsWhiteSpace(c)))
                    {
                        if (TypeScale.Raster)
                        {
                            RasterText.Instance.Layout(text, clip, (float)(pixelWidth / frame.Viewport.Width), RasterGlyphs);
                            break;
                        }
                        var sized = new PositionedTextOperation(text.SourceId, text.Rect, text.Text,
                            text.Style with { Size = NativeUiText.Size(text.Style.Size) }, text.Color);
                        var qualified = NativeUiText.Font.Qualify(sized);
                        int first = Glyphs.Count;
                        AurelianMsdfTextPresentationAdapter.AdaptInto(qualified, NativeUiText.Font.ResourceFor(qualified), atlas, Glyphs, clip);
                        for (int i = first; i < Glyphs.Count; i++) Glyphs[i] = DarkStemCompensation(Glyphs[i]);
                    }
                    else fallback.Add((text, clip));
                    break;
                default:
                    throw new NotSupportedException($"Valencia native UI cannot render {operation.GetType().Name}.");
            }
        }
        if (fallback.Count > 0)
        {
            FallbackTextRuns = fallback.Count;
            using var bitmap = new SKBitmap(new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)(pixelWidth / frame.Viewport.Width), (float)(pixelHeight / frame.Viewport.Height));
            using var typeface = SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", TypeScale.Mono));
            foreach (var (text, clip) in fallback)
            {
                using var font = new SKFont(typeface, TypeScale.Pixels(text.Style.Size));
                using var paint = new SKPaint { IsAntialias = true, Color = new((byte)(text.Color.Rgba >> 24),
                    (byte)(text.Color.Rgba >> 16), (byte)(text.Color.Rgba >> 8), (byte)text.Color.Rgba) };
                // Font fallback is per scalar, with explicit ownership. Complex script shaping
                // and bidi are not supplied by this small native editor text path.
                var runs = new List<(string Text, SKFont Font)>();
                var owned = new List<IDisposable>();
                float width = 0;
                foreach (var rune in text.Text.EnumerateRunes())
                {
                    SKFont selected = font;
                    if (!font.ContainsGlyph(rune.Value) && SKFontManager.Default.MatchCharacter(rune.Value) is { } face)
                    {
                        owned.Add(face); selected = new SKFont(face, font.Size); owned.Add(selected);
                    }
                    string value = rune.ToString(); runs.Add((value, selected)); width += selected.MeasureText(value);
                }
                float x = (float)text.Rect.X + (text.Style.AlignX switch
                { TextAlignX.Center => ((float)text.Rect.Width - width) / 2, TextAlignX.Right => (float)text.Rect.Width - width, _ => 0 });
                float baseline = (float)text.Rect.Y + (text.Style.AlignY switch
                { TextAlignY.Center => ((float)text.Rect.Height - font.Size) / 2 + font.Size * .8f,
                    TextAlignY.Bottom => (float)text.Rect.Height - font.Size * .2f, _ => font.Size * .8f });
                canvas.Save(); canvas.ClipRect(new((float)clip.X, (float)clip.Y, (float)(clip.X + clip.Width), (float)(clip.Y + clip.Height)));
                try
                {
                    foreach (var run in runs) { canvas.DrawText(run.Text, x, baseline, run.Font, paint); x += run.Font.MeasureText(run.Text); }
                }
                finally { canvas.Restore(); foreach (var resource in owned.AsEnumerable().Reverse()) resource.Dispose(); }
            }
            FallbackPixels = bitmap.Bytes;
        }

        void Shape(MachinaAnalyticShapePrimitive shape)
        {
            if (AurelianAnalyticShapePresentationAdapter.Adapt(shape, clips.Peek()) is { } submission) Shapes.Add(submission);
        }
    }


    private static Rect Intersect(Rect a, Rect b)
    {
        double x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        return new(x, y, Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - x), Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - y));
    }

    /// <summary>
    /// Coverage is blended in linear space, which swells light-on-dark stems and thins dark-on-light ones. The shared
    /// small-size threshold is tuned for light text; dark glyphs (ink on bone, paper or orange) get their edge moved
    /// outward by about a third of a screen pixel instead, so 10-12 px ink labels read at full weight.
    /// </summary>
    internal static NativeMsdfQuadSubmission DarkStemCompensation(NativeMsdfQuadSubmission glyph)
    {
        var c = glyph.Color;
        double luminance = .2126 * c.Red + .7152 * c.Green + .0722 * c.Blue;
        if (luminance >= .25) return glyph;
        float screenRange = Math.Max(.5f, glyph.Msdf.PixelRange * glyph.Msdf.FieldScale);
        float threshold = Math.Max(.3f, glyph.Msdf.Threshold - Math.Min(.2f, .35f / screenRange));
        return glyph with { Msdf = glyph.Msdf with { Threshold = threshold } };
    }
}
