using SkiaSharp;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Media;

/// <summary>Small SDR software reference shared by native preview uploads and offline exports.</summary>
internal static class CreatorRenderer
{
    private static readonly VideoEffects IdentityEffects = new();
    public static void Composite(byte[] output, int width, int height, VideoFrame source, float opacity,
        VideoEffects effects, OutputProfile? profile, SequenceTime time)
    {
        var framing = Framing(profile, time);
        double cropW = source.Width * (1 - effects.CropLeft - effects.CropRight);
        double cropH = source.Height * (1 - effects.CropTop - effects.CropBottom);
        bool fill = profile?.Fit is "fill" or "center";
        double scale = (fill ? Math.Max(width / cropW, height / cropH) : Math.Min(width / cropW, height / cropH)) * effects.Scale * framing.Scale;
        double w = cropW * scale, h = cropH * scale;
        double left = (width - w) * framing.X + (effects.X - .5) * width;
        double top = (height - h) * framing.Y + (effects.Y - .5) * height;
        if (effects == IdentityEffects && width == source.Width && height == source.Height
            && framing.Scale == 1 && opacity == 1)
        {
            source.Rgba.CopyTo(output, 0);
            return;
        }
        if (effects.Exposure == 0 && effects.Contrast == 1 && effects.Saturation == 1 && effects.Temperature == 0)
        {
            Geometry(output, width, height, source, opacity, effects, cropW, cropH, left, top, w, h);
            return;
        }
        double exposure = Math.Pow(2, effects.Exposure);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int destination = (y * width + x) * 4;
            bool inside = x >= left && x < left + w && y >= top && y < top + h;
            double r = 0, g = 0, b = 0;
            if (inside)
            {
                int sx = Math.Clamp((int)((x - left) / scale + effects.CropLeft * source.Width), 0, source.Width - 1);
                int sy = Math.Clamp((int)((y - top) / scale + effects.CropTop * source.Height), 0, source.Height - 1);
                int input = (sy * source.Width + sx) * 4;
                r = source.Rgba[input] / 255.0; g = source.Rgba[input + 1] / 255.0; b = source.Rgba[input + 2] / 255.0;
                r = (r * exposure * (1 + .2 * effects.Temperature) - .5) * effects.Contrast + .5;
                g = (g * exposure - .5) * effects.Contrast + .5;
                b = (b * exposure * (1 - .2 * effects.Temperature) - .5) * effects.Contrast + .5;
                double luma = .2126 * r + .7152 * g + .0722 * b;
                r = luma + effects.Saturation * (r - luma); g = luma + effects.Saturation * (g - luma); b = luma + effects.Saturation * (b - luma);
            }
            output[destination] = Blend(r, output[destination], opacity);
            output[destination + 1] = Blend(g, output[destination + 1], opacity);
            output[destination + 2] = Blend(b, output[destination + 2], opacity);
        }
    }

    private static unsafe void Geometry(byte[] output, int width, int height, VideoFrame source, float opacity, VideoEffects effects,
        double cropWidth, double cropHeight, double left, double top, double scaledWidth, double scaledHeight)
    {
        if (opacity <= 0) return;
        // Native raster geometry avoids scalar per-pixel color math for ordinary fit/fill/crop.
        // Nearest sampling uses pixel centers. The same path serves preview and export.
        fixed (byte* input = source.Rgba)
        fixed (byte* target = output)
        {
            using var pixmap = new SKPixmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Opaque), (nint)input, source.Width * 4);
            using var image = SKImage.FromPixels(pixmap) ?? throw new InvalidOperationException("Source raster image creation failed.");
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque), (nint)target, width * 4)
                ?? throw new InvalidOperationException("Composition surface creation failed.");
            var canvas = surface.Canvas;
            using var layer = new SKPaint { Color = new SKColor(255, 255, 255, (byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255)) };
            if (opacity < 1) canvas.SaveLayer(layer);
            canvas.Clear(SKColors.Black);
            var from = new SKRect((float)(source.Width * effects.CropLeft), (float)(source.Height * effects.CropTop),
                (float)(source.Width * effects.CropLeft + cropWidth), (float)(source.Height * effects.CropTop + cropHeight));
            var to = new SKRect((float)left, (float)top, (float)(left + scaledWidth), (float)(top + scaledHeight));
            canvas.DrawImage(image, from, to, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
            if (opacity < 1) canvas.Restore();
            canvas.Flush();
        }
    }
    private static byte Blend(double value, byte previous, float opacity) => (byte)Math.Clamp(value * 255 * opacity + previous * (1 - opacity), 0, 255);

    private static (double X, double Y, double Scale) Framing(OutputProfile? profile, SequenceTime time)
    {
        if (profile is null) return (.5, .5, 1);
        if (profile.Framing is not { IsDefaultOrEmpty: false } keys) return (profile.FocalX, profile.FocalY, 1);
        if (time.Ticks <= keys[0].Time.Ticks) return (keys[0].X, keys[0].Y, keys[0].Scale);
        for (int i = 1; i < keys.Length; i++)
        {
            if (time.Ticks > keys[i].Time.Ticks) continue;
            var a = keys[i - 1]; var b = keys[i];
            double t = (double)(time.Ticks - a.Time.Ticks) / (b.Time.Ticks - a.Time.Ticks);
            return (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Scale + (b.Scale - a.Scale) * t);
        }
        return (keys[^1].X, keys[^1].Y, keys[^1].Scale);
    }

    public static SKTypeface OpenFont(string name)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), name == "Arial" ? "arial.ttf" : "consola.ttf");
        return SKTypeface.FromFile(path) ?? throw new NotSupportedException("Caption font unavailable: " + name);
    }
    public static unsafe void Captions(byte[] pixels, int width, int height, CaptionLine line, CaptionStyle style, double? y, SKTypeface typeface)
    {
        using var font = new SKFont(typeface, Math.Min(style.Size * height / 720f, width * .075f));
        using var outline = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, font.Size / 10), IsAntialias = true };
        using var fill = new SKPaint { IsAntialias = true };
        string text = string.Join(" ", line.Words.Select(w => w.Text));
        if (font.GetGlyphs(text).Any(g => g == 0)) throw new NotSupportedException("Selected caption font cannot render the supplied text.");
        float measured = font.MeasureText(text);
        if (measured > width * .88f) font.Size *= width * .88f / measured;
        float start = (width - font.MeasureText(text)) / 2;
        float baseline = (float)(height * (y ?? style.Y));
        fixed (byte* pointer = pixels)
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque), (nint)pointer, width * 4)
                ?? throw new InvalidOperationException("Caption raster surface could not be created.");
            var canvas = surface.Canvas;
            using var background = new SKPaint { Color = new SKColor(0, 0, 0, 150), IsAntialias = true };
            canvas.DrawRoundRect(new SKRect(start - 6, baseline - font.Size - 4, width - start + 6, baseline + font.Size * .3f), 4, 4, background);
            float x = start;
            for (int i = 0; i < line.Words.Length; i++)
            {
                uint color = i == line.ActiveWord ? style.Highlight : style.Color;
                fill.Color = new SKColor((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color);
                canvas.DrawText(line.Words[i].Text, x, baseline, font, outline);
                canvas.DrawText(line.Words[i].Text, x, baseline, font, fill);
                x += font.MeasureText(line.Words[i].Text + " ");
            }
            canvas.Flush();
        }
    }
}
