using Aurelian.Graphics.Vulkan.Native2D;
using Machina.Core.Measurement;
using Machina.Core.Styling;
using Machina.Layout.Geometry;
using Machina.Presentation;
using SkiaSharp;

namespace Valencia.Rendering;

/// <summary>One rasterized glyph placed on the framebuffer, in logical units.</summary>
public readonly record struct RasterGlyphQuad(Native2DRect Destination, Native2DUvRect Uv, Native2DTint Color);

/// <summary>
/// Hinted, pixel-snapped text for Valencia's small data roles. Each (size, device scale, character) is rasterized
/// by Skia into a shared RGBA atlas (white RGB, coverage in alpha) and drawn as tinted 1:1 quads on device pixels.
/// Glyph origins and baselines snap to device pixels; advances come from hinted metrics, so measurement and
/// presentation agree exactly.
/// </summary>
public sealed class RasterText
{
    public const int AtlasSize = 2048;
    public static RasterText Instance { get; } = new();

    private sealed class Face
    {
        public required SKFont Font { get; init; }
        public float Ascent { get; init; }
        public float Descent { get; init; }
        public Dictionary<char, float> Advances { get; } = [];
    }
    private readonly record struct Slot(int X, int Y, int Width, int Height, int Left, int Top);

    private readonly Dictionary<string, SKTypeface> typefaces = [];
    private readonly object gate = new(); // shared measurer may be queried while another caller warms a face
    private readonly Dictionary<(TextSize Size, float Scale), Face> faces = [];
    private readonly Dictionary<(TextSize Size, float Scale, char Glyph, bool Dark), Slot?> slots = [];
    private int penX = 1, penY = 1, rowHeight;

    /// <summary>Atlas pixels (RGBA, straight alpha). Upload whenever <see cref="Version"/> changes.</summary>
    public byte[] Pixels { get; } = new byte[AtlasSize * AtlasSize * 4];
    public int Version { get; private set; }
    public int GlyphCount => slots.Count;

    private Face FaceFor(TextSize size, float scale)
    {
        scale = MathF.Round(scale * 100) / 100;
        if (faces.TryGetValue((size, scale), out var face)) return face;
        string file = TypeScale.File(size);
        if (!typefaces.TryGetValue(file, out var typeface))
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", file);
            typeface = SKTypeface.FromFile(path) ?? throw new FileNotFoundException("Valencia UI font is missing.", path);
            typefaces[file] = typeface;
        }
        var font = new SKFont(typeface, TypeScale.Pixels(size) * scale)
        {
            Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.Normal, Subpixel = false, LinearMetrics = false, BaselineSnap = true,
        };
        var metrics = font.Metrics;
        face = new Face { Font = font, Ascent = MathF.Ceiling(-metrics.Ascent), Descent = MathF.Ceiling(metrics.Descent) };
        faces[(size, scale)] = face;
        // Warm the whole printable ASCII set for both coverage classes now, so the atlas uploads once per face
        // (startup or a DPI change) instead of whenever a new character first appears mid-playback.
        for (char c = '!'; c <= '~'; c++) { Glyph(face, size, scale, c, false); Glyph(face, size, scale, c, true); }
        return face;
    }

    private static float Advance(Face face, char c)
    {
        if (!face.Advances.TryGetValue(c, out float advance))
            face.Advances[c] = advance = MathF.Round(face.Font.MeasureText(c.ToString()));
        return advance;
    }

    /// <summary>Logical size of one line (ASCII) at the role's size.</summary>
    public IntrinsicSize Measure(string value, TextSize size)
    {
        lock (gate)
        {
            var face = FaceFor(size, 1);
            float width = 0;
            foreach (char c in value) width += Advance(face, c);
            return new(Math.Ceiling(width) + 1, face.Ascent + face.Descent + 2);
        }
    }

    /// <summary>Appends the glyph quads for one positioned text operation, clipped to <paramref name="clip"/>.</summary>
    public void Layout(PositionedTextOperation text, Rect clip, float scale, List<RasterGlyphQuad> into)
    {
        lock (gate) LayoutCore(text, clip, scale, into);
    }
    private void LayoutCore(PositionedTextOperation text, Rect clip, float scale, List<RasterGlyphQuad> into)
    {
        var size = text.Style.Size;
        var face = FaceFor(size, scale);
        uint rgba = text.Color.Rgba;
        var tint = new Native2DTint((rgba >> 24) / 255f, ((rgba >> 16) & 255) / 255f, ((rgba >> 8) & 255) / 255f, (rgba & 255) / 255f);
        bool dark = .2126f * tint.Red + .7152f * tint.Green + .0722f * tint.Blue < .25f;
        // Align the visible glyph run, using this device scale's hinted advances and bearings.
        // Logical 1x metrics differ at fractional DPI, and advance boxes include unequal side bearings.
        float advanceWidth = 0, inkLeft = float.PositiveInfinity, inkTop = float.PositiveInfinity;
        float inkRight = float.NegativeInfinity, inkBottom = float.NegativeInfinity;
        foreach (char c in text.Text)
        {
            if (!char.IsWhiteSpace(c) && Glyph(face, size, scale, c, dark) is { } slot)
            {
                inkLeft = Math.Min(inkLeft, advanceWidth + slot.Left); inkRight = Math.Max(inkRight, advanceWidth + slot.Left + slot.Width);
                inkTop = Math.Min(inkTop, slot.Top); inkBottom = Math.Max(inkBottom, slot.Top + slot.Height);
            }
            advanceWidth += Advance(face, c);
        }
        if (!float.IsFinite(inkLeft)) return;
        // Device-pixel space from here: whole-pixel origins and baseline.
        float pen = MathF.Round((float)(text.Rect.X * scale) + (text.Style.AlignX switch
        {
            TextAlignX.Center => ((float)text.Rect.Width * scale - inkLeft - inkRight) / 2,
            TextAlignX.Right => (float)text.Rect.Width * scale - inkRight, _ => 0,
        }));
        float baseline = MathF.Round((float)(text.Rect.Y * scale) + (text.Style.AlignY switch
        {
            TextAlignY.Center => ((float)text.Rect.Height * scale - inkTop - inkBottom) / 2,
            TextAlignY.Bottom => (float)text.Rect.Height * scale - inkBottom, _ => scale + face.Ascent,
        }));
        int clipLeft = (int)Math.Floor(clip.X * scale), clipTop = (int)Math.Floor(clip.Y * scale);
        int clipRight = (int)Math.Ceiling((clip.X + clip.Width) * scale), clipBottom = (int)Math.Ceiling((clip.Y + clip.Height) * scale);
        foreach (char c in text.Text)
        {
            float advance = Advance(face, c);
            if (!char.IsWhiteSpace(c) && Glyph(face, size, scale, c, dark) is { } slot)
            {
                int left = (int)pen + slot.Left, top = (int)baseline + slot.Top, right = left + slot.Width, bottom = top + slot.Height;
                int l = Math.Max(left, clipLeft), t = Math.Max(top, clipTop), r = Math.Min(right, clipRight), b = Math.Min(bottom, clipBottom);
                if (r > l && b > t)
                {
                    float u0 = (slot.X + l - left) / (float)AtlasSize, v0 = (slot.Y + t - top) / (float)AtlasSize;
                    float u1 = (slot.X + r - left) / (float)AtlasSize, v1 = (slot.Y + b - top) / (float)AtlasSize;
                    into.Add(new(new(l / scale, t / scale, (r - l) / scale, (b - t) / scale), new(u0, v0, u1, v1), tint));
                }
            }
            pen += advance;
        }
    }

    private Slot? Glyph(Face face, TextSize size, float scale, char c, bool dark)
    {
        var key = (size, MathF.Round(scale * 100) / 100, c, dark);
        if (slots.TryGetValue(key, out var cached)) return cached;
        string s = c.ToString();
        face.Font.MeasureText(s, out SKRect bounds);
        if (bounds.Width <= 0 || bounds.Height <= 0) { slots[key] = null; return null; }
        int left = (int)Math.Floor(bounds.Left) - 1, top = (int)Math.Floor(bounds.Top) - 1;
        int width = (int)Math.Ceiling(bounds.Right) - left + 1, height = (int)Math.Ceiling(bounds.Bottom) - top + 1;
        if (penX + width + 1 > AtlasSize) { penX = 1; penY += rowHeight + 1; rowHeight = 0; }
        if (penY + height + 1 > AtlasSize)
        {
            // Full: start over. Slots from earlier this frame may sample new glyphs until the next prepare.
            Array.Clear(Pixels); slots.Clear(); penX = 1; penY = 1; rowHeight = 0;
        }
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.White, IsAntialias = true })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawText(s, -left, -top, face.Font, paint);
        }
        // Coverage is blended in linear space by the GPU, which thins dark-on-light stems; lift dark glyph coverage.
        var source = bitmap.GetPixelSpan();
        for (int row = 0; row < height; row++)
            for (int col = 0; col < width; col++)
            {
                byte alpha = source[(row * width + col) * 4 + 3];
                if (dark && alpha is > 0 and < 255) alpha = (byte)Math.Round(255 * Math.Pow(alpha / 255.0, .5));
                int i = ((penY + row) * AtlasSize + penX + col) * 4;
                Pixels[i] = Pixels[i + 1] = Pixels[i + 2] = 255; Pixels[i + 3] = alpha;
            }
        var slot = new Slot(penX, penY, width, height, left, top);
        slots[key] = slot;
        penX += width + 1; rowHeight = Math.Max(rowHeight, height); Version++;
        return slot;
    }
}
