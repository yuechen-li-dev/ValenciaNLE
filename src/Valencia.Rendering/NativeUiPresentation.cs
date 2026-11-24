using Aurelian.Graphics.Vulkan.Native2D;
using Aurelian.Machina;
using Machina.Core.Styling;
using Machina.Layout.Geometry;
using Machina.Presentation;
using SkiaSharp;

namespace Valencia.Rendering;

/// <summary>
/// Narrow realization of Valencia's flat Machina panels. Geometry remains vector intent;
/// Data labels use a pixel-aligned hinted atlas. Commands and headings use Machina's MSDF authority. Unicode text uses a
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
        List<(PositionedTextOperation Text, Rect Clip, int Order)> fallback = [];
        var occluders = Occluders(frame);
        for (int order = 0; order < frame.Operations.Count; order++)
        {
            var operation = frame.Operations[order];
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
                    // Text is drawn in a later pass than every fill, so painter order is restored here:
                    // fills that come later (popups, dialogs, a caret) occlude or tint the glyphs beneath them.
                    var above = occluders.Where(o => o.Order > order && Overlaps(o.Rect, clip)).ToArray();
                    if (text.Text.All(c => c is >= ' ' and <= '~' || char.IsWhiteSpace(c)))
                    {
                        if (TypeScale.UsesRaster(text.Style.Size))
                        {
                            int firstRaster = RasterGlyphs.Count;
                            RasterText.Instance.Layout(text, clip, (float)(pixelWidth / frame.Viewport.Width), RasterGlyphs);
                            if (above.Length > 0)
                            {
                                var placed = RasterGlyphs.GetRange(firstRaster, RasterGlyphs.Count - firstRaster);
                                RasterGlyphs.RemoveRange(firstRaster, placed.Count);
                                foreach (var g in placed)
                                    foreach (var piece in Occlude(g.Destination, g.Uv, g.Color, above)) RasterGlyphs.Add(new(piece.Rect, piece.Uv, piece.Color));
                            }
                            break;
                        }
                        int firstMsdf = Glyphs.Count;
                        AppendMsdf(text, clip, atlas);
                        if (above.Length > 0)
                        {
                            var placed = Glyphs.GetRange(firstMsdf, Glyphs.Count - firstMsdf);
                            Glyphs.RemoveRange(firstMsdf, placed.Count);
                            foreach (var g in placed)
                                foreach (var piece in Occlude(g.Destination, g.Uv, g.Color, above))
                                    Glyphs.Add(g with { Destination = piece.Rect, Uv = piece.Uv, Color = piece.Color });
                        }
                    }
                    else fallback.Add((text, clip, order));
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
            foreach (var (text, clip, order) in fallback)
            {
                var above = occluders.Where(o => o.Order > order && Overlaps(o.Rect, clip)).ToArray();
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
                foreach (var o in above)
                {
                    var rect = new SKRect((float)o.Rect.X, (float)o.Rect.Y, (float)(o.Rect.X + o.Rect.Width), (float)(o.Rect.Y + o.Rect.Height));
                    if ((o.Rgba & 255) == 255) canvas.ClipRect(rect, SKClipOperation.Difference);
                    else if (Contains(o.Rect, clip))
                    {
                        var c = Through(Tint(text.Color.Rgba), o.Rgba);
                        paint.Color = new((byte)Math.Round(c.Red * 255), (byte)Math.Round(c.Green * 255), (byte)Math.Round(c.Blue * 255), paint.Color.Alpha);
                    }
                }
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

    private void AppendMsdf(PositionedTextOperation text, Rect clip, AurelianMsdfAtlasCache atlas)
    {
        var font = NativeUiText.Font;
        var qualified = font.Qualify(text);
        var resource = font.ResourceFor(qualified);
        float scale = 1;
        if (text.Style is { Size: TextSize.Md, AlignX: TextAlignX.Center, AlignY: TextAlignY.Center }
            && qualified.Primitive!.GlyphRun.Lines[0].InkBounds is { } ink)
        {
            // Fit the actual command ink with a small inset, retaining both its face and aspect ratio.
            double availableWidth = text.Rect.Width - 6, availableHeight = text.Rect.Height - 2;
            if (availableWidth <= 0 || availableHeight <= 0) return;
            scale = (float)Math.Min(1, Math.Min(availableWidth / (ink.Right - ink.Left), availableHeight / (ink.Bottom - ink.Top)));
        }

        double cx = text.Rect.X + text.Rect.Width / 2, cy = text.Rect.Y + text.Rect.Height / 2;
        // Clip in the unscaled coordinate space so the UV crop and final placement agree.
        var glyphClip = scale == 1 ? clip : new Rect(cx + (clip.X - cx) / scale, cy + (clip.Y - cy) / scale,
            clip.Width / scale, clip.Height / scale);
        int first = Glyphs.Count;
        AurelianMsdfTextPresentationAdapter.AdaptInto(qualified, resource, atlas, Glyphs, glyphClip);
        if (scale == 1) return;

        for (int i = first; i < Glyphs.Count; i++)
        {
            var glyph = Glyphs[i];
            var rect = glyph.Destination;
            Glyphs[i] = glyph with
            {
                Destination = new((float)(cx + (rect.X - cx) * scale), (float)(cy + (rect.Y - cy) * scale),
                    rect.Width * scale, rect.Height * scale),
                Msdf = glyph.Msdf with { FieldScale = glyph.Msdf.FieldScale * scale }
            };
        }
    }

    /// <summary>
    /// Fills in painter order with their clipped rectangles. Only axis-aligned rectangles (and nearly square-cornered
    /// rounded rectangles) are exact occluders; pills, circles and strokes never hide text.
    /// </summary>
    private static List<(int Order, Rect Rect, uint Rgba)> Occluders(MachinaPresentationFrame frame)
    {
        var result = new List<(int, Rect, uint)>();
        var clips = new Stack<Rect>();
        clips.Push(new(0, 0, frame.Viewport.Width, frame.Viewport.Height));
        for (int order = 0; order < frame.Operations.Count; order++)
        {
            (Rect Rect, uint Rgba)? fill = frame.Operations[order] switch
            {
                PushRectangularClipOperation push => Push(push.Rect),
                PopClipOperation => Pop(),
                FillRectangleOperation f => (f.Rect, f.Color.Rgba),
                MachinaAnalyticShapePrimitive { Kind: MachinaAnalyticShapeKind.RoundedRect, Radius: <= 2 } shape => (shape.DestinationRect, shape.FillColor.Rgba),
                _ => null,
            };
            if (fill is not { } value || (value.Rgba & 255) == 0) continue;
            var rect = Intersect(clips.Peek(), value.Rect);
            if (rect.Width > 0 && rect.Height > 0) result.Add((order, rect, value.Rgba));
        }
        return result;

        (Rect, uint)? Push(Rect rect) { clips.Push(Intersect(clips.Peek(), rect)); return null; }
        (Rect, uint)? Pop() { if (clips.Count > 1) clips.Pop(); return null; }
    }

    /// <summary>
    /// Splits one glyph quad against later fills: opaque fills remove the covered part, translucent fills
    /// recolor it as if the fill were blended over it (exact for a uniform overlay over solid text).
    /// </summary>
    private static List<(Native2DRect Rect, Native2DUvRect Uv, Native2DTint Color)> Occlude(Native2DRect rect, Native2DUvRect uv, Native2DTint color,
        IReadOnlyList<(int Order, Rect Rect, uint Rgba)> above)
    {
        var pieces = new List<(Native2DRect Rect, Native2DUvRect Uv, Native2DTint Color)> { (rect, uv, color) };
        foreach (var occluder in above)
        {
            float ox0 = (float)occluder.Rect.X, oy0 = (float)occluder.Rect.Y;
            float ox1 = (float)(occluder.Rect.X + occluder.Rect.Width), oy1 = (float)(occluder.Rect.Y + occluder.Rect.Height);
            bool opaque = (occluder.Rgba & 255) == 255;
            var next = new List<(Native2DRect Rect, Native2DUvRect Uv, Native2DTint Color)>();
            foreach (var piece in pieces)
            {
                var r = piece.Rect;
                float x0 = r.X, y0 = r.Y, x1 = r.X + r.Width, y1 = r.Y + r.Height;
                float ix0 = Math.Max(x0, ox0), iy0 = Math.Max(y0, oy0), ix1 = Math.Min(x1, ox1), iy1 = Math.Min(y1, oy1);
                if (ix1 <= ix0 || iy1 <= iy0) { next.Add(piece); continue; }
                // Up to four uncovered bands, then the covered middle.
                if (iy0 > y0) next.Add(Sub(piece, x0, y0, x1, iy0));
                if (iy1 < y1) next.Add(Sub(piece, x0, iy1, x1, y1));
                if (ix0 > x0) next.Add(Sub(piece, x0, iy0, ix0, iy1));
                if (ix1 < x1) next.Add(Sub(piece, ix1, iy0, x1, iy1));
                if (!opaque)
                {
                    var covered = Sub(piece, ix0, iy0, ix1, iy1);
                    next.Add(covered with { Color = Through(covered.Color, occluder.Rgba) });
                }
            }
            pieces = next;
            if (pieces.Count == 0) break;
        }
        return pieces;
    }

    private static (Native2DRect Rect, Native2DUvRect Uv, Native2DTint Color) Sub(
        (Native2DRect Rect, Native2DUvRect Uv, Native2DTint Color) piece, float x0, float y0, float x1, float y1)
    {
        var r = piece.Rect; var uv = piece.Uv;
        float U(float x) => uv.U0 + (uv.U1 - uv.U0) * ((x - r.X) / r.Width);
        float V(float y) => uv.V0 + (uv.V1 - uv.V0) * ((y - r.Y) / r.Height);
        return (new(x0, y0, x1 - x0, y1 - y0), new(U(x0), V(y0), U(x1), V(y1)), piece.Color);
    }

    /// <summary>Text color seen through a translucent fill, composited in linear light like the GPU blend.</summary>
    private static Native2DTint Through(Native2DTint text, uint fillRgba)
    {
        float a = (fillRgba & 255) / 255f;
        static float Linear(float c) => MathF.Pow(c, 2.2f);
        static float Encode(float c) => MathF.Pow(Math.Clamp(c, 0, 1), 1 / 2.2f);
        float Mix(float t, uint shift) => Encode(Linear(t) * (1 - a) + Linear(((fillRgba >> (int)shift) & 255) / 255f) * a);
        return new(Mix(text.Red, 24), Mix(text.Green, 16), Mix(text.Blue, 8), text.Alpha);
    }

    private static Native2DTint Tint(uint rgba) => new((rgba >> 24) / 255f, ((rgba >> 16) & 255) / 255f, ((rgba >> 8) & 255) / 255f, (rgba & 255) / 255f);
    private static bool Overlaps(Rect a, Rect b) => a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
    private static bool Contains(Rect outer, Rect inner) => inner.X >= outer.X && inner.Y >= outer.Y
        && inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;

    private static Rect Intersect(Rect a, Rect b)
    {
        double x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        return new(x, y, Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - x), Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - y));
    }
}
