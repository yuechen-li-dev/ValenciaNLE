using System.Runtime.InteropServices;
using System.Text.Json;
using Aurelian.Machina;
using Machina.Core.Authoring;
using Machina.Core.Nodes;
using Machina.Core.Styling;
using Machina.Layout.Geometry;
using Machina.Pipeline;
using Machina.Presentation;
using Machina.Standard.Authoring;
using SkiaSharp;
using Valencia.Rendering;

namespace Valencia.App;

/// <summary>Own-window Vulkan readback and an independent Skia outline reference; no desktop capture.</summary>
internal static class TypographySmoke
{
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        using var graphics = new NativeVideoPresenter();
        var samples = new[]
        {
            ("SEQUENCE", TextSize.H1, new Rect(20, 20, 700, 90)),
            ("Drop a recording here, press Ctrl+I, or start OBS from the Record tab.", TextSize.Lg, new Rect(20, 140, 1200, 50)),
            ("AVATAR / To WA", TextSize.Xl, new Rect(20, 230, 1000, 70))
        };
        var children = new List<UiNode>();
        foreach (var (text, size, rect) in samples)
            children.Add(StandardUI.Place(UI.Text(text, size: size, color: ColorToken.White), rect.X, rect.Y, rect.Width, rect.Height));
        var frame = new MachinaPresentationPipeline().Prepare(UI.Surface(color: ColorToken.Hex(0x000000FF), children: children),
            1280, 720, new Machina.Core.Lowering.UiLoweringOptions(NativeUiText.Instance));
        graphics.UpdateUi(frame.PresentationFrame);
        byte[] pixels = graphics.Draw(true) ?? throw new InvalidOperationException("No Vulkan readback.");
        int width = graphics.Window.FramebufferSize.X, height = graphics.Window.FramebufferSize.Y;
        using var image = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Marshal.Copy(pixels, 0, image.GetPixels(), pixels.Length);
        Save(image, Path.Combine(directory, "gpu.png"));

        using var reference = new SKBitmap(width, height);
        using var canvas = new SKCanvas(reference);
        canvas.Clear(SKColors.Black);
        canvas.Scale(width / 1280f, height / 720f);
        using var face = SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", TypeScale.Display));
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var native = AurelianNativeUiFont.Create(TypeScale.Specs(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts"))
            .Where(spec => !TypeScale.UsesRaster(spec.Size)));
        var metrics = new List<object>();
        foreach (var text in frame.PresentationFrame.Operations.OfType<PositionedTextOperation>())
        {
            var run = native.Qualify(text).Primitive!.GlyphRun;
            using var font = new SKFont(face, TypeScale.Pixels(text.Style.Size))
            {
                Hinting = SKFontHinting.None, LinearMetrics = true, Subpixel = true
            };
            foreach (var glyph in run.Glyphs)
            {
                string character = char.ConvertFromUtf32(glyph.Key.Codepoint);
                float advance = font.MeasureText(character);
                using var outline = font.GetGlyphPath(font.GetGlyphs(character)[0]);
                SKRect bounds = outline?.Bounds ?? default;
                canvas.DrawText(character, (float)(text.Rect.X + glyph.OriginX),
                    (float)(text.Rect.Y + glyph.BaselineY), font, paint);
                metrics.Add(new { text.Text, character, glyph.OriginX, glyph.BaselineY,
                    glyph.Advance, glyph.PlaneBounds, skiaAdvance = advance,
                    skiaBounds = new { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom } });
            }
        }
        canvas.Flush();
        Save(reference, Path.Combine(directory, "outline-reference.png"));
        var comparisons = samples.Select(sample =>
        {
            var gpu = InkBounds(image, sample.Item3);
            var expected = InkBounds(reference, sample.Item3);
            int maximumEdgeError = new[] { Math.Abs(gpu.Left - expected.Left), Math.Abs(gpu.Top - expected.Top),
                Math.Abs(gpu.Right - expected.Right), Math.Abs(gpu.Bottom - expected.Bottom) }.Max();
            return new { text = sample.Item1, gpu, expected, maximumEdgeError };
        }).ToArray();
        bool passed = graphics.ValidationErrorCount == 0 && comparisons.All(sample => sample.maximumEdgeError <= 1);
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            outcome = passed ? "passed" : "failed", gpu = graphics.GpuName, runtime = Environment.Version.ToString(),
            graphics.ValidationAvailable, graphics.ValidationErrorCount,
            font = TypeScale.Display, metrics, comparisons,
            reference = "Independent Skia outlines at the shared run's origins; numerical advance/bounds parity is tested separately."
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }

    private sealed record PixelBounds(int Left, int Top, int Right, int Bottom);

    private static PixelBounds InkBounds(SKBitmap bitmap, Rect rect)
    {
        double scaleX = bitmap.Width / 1280d, scaleY = bitmap.Height / 720d;
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        for (int y = (int)(rect.Y * scaleY); y < (rect.Y + rect.Height) * scaleY; y++)
        for (int x = (int)(rect.X * scaleX); x < (rect.X + rect.Width) * scaleX; x++)
        {
            if (bitmap.GetPixel(x, y).Red <= 128) continue;
            left = Math.Min(left, x); top = Math.Min(top, y);
            right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        return new(left, top, right, bottom);
    }

    private static void Save(SKBitmap bitmap, string path)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(path);
        data.SaveTo(output);
    }
}
