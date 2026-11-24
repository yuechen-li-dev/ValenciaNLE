using SkiaSharp;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Media;

/// <summary>Small SDR software reference shared by native preview uploads and offline exports.</summary>
internal static class CreatorRenderer
{
    private static readonly VideoEffects IdentityEffects = new();
    public static void Composite(byte[] output, int width, int height, VideoFrame source, AssetStream stream, float opacity,
        VideoEffects effects, OutputProfile? profile, SequenceTime time, VulkanClipEffects? gpu, CancellationToken cancellation,
        double rotation = 0)
    {
        if (opacity <= 0) return;
        var framing = Framing(profile, time);
        double cropW = source.Width * (1 - effects.CropLeft - effects.CropRight);
        double cropH = source.Height * (1 - effects.CropTop - effects.CropBottom);
        bool fill = profile?.Fit is "fill" or "center";
        double scale = (fill ? Math.Max(width / cropW, height / cropH) : Math.Min(width / cropW, height / cropH)) * effects.Scale * framing.Scale;
        double w = cropW * scale, h = cropH * scale;
        double left = (width - w) * framing.X + (effects.X - .5) * width;
        double top = (height - h) * framing.Y + (effects.Y - .5) * height;
        if (effects == IdentityEffects && width == source.Width && height == source.Height
            && framing.Scale == 1 && opacity == 1 && rotation == 0)
        {
            source.Rgba.CopyTo(output, 0);
            return;
        }
        bool hasColor = ClipEffectFrame.HasColor(effects);
        if (!hasColor && effects.Region is null)
        {
            Geometry(output, width, height, source, opacity, effects, cropW, cropH, left, top, w, h, rotation);
            return;
        }
        // Keep the old grading path's sample convention for existing projects.
        double radians = rotation * Math.PI / 180;
        RotatedSampling? sampling = rotation == 0 ? null : new(left, top, w, h, scale, effects.CropLeft, effects.CropTop,
            source.Width, source.Height, Math.Cos(radians), Math.Sin(radians), hasColor ? 0 : .5);
        var pass = new ClipEffectFrame(source, stream, effects, opacity,
            ClipEffectFrame.Axis(width, source.Width, left, w, scale, effects.CropLeft, centers: !hasColor),
            ClipEffectFrame.Axis(height, source.Height, top, h, scale, effects.CropTop, centers: !hasColor), sampling);
        if (gpu is null || sampling is not null) pass.Composite(output, cancellation);
        else gpu.Composite(pass, output, cancellation);
    }

    private static unsafe void Geometry(byte[] output, int width, int height, VideoFrame source, float opacity, VideoEffects effects,
        double cropWidth, double cropHeight, double left, double top, double scaledWidth, double scaledHeight, double rotation)
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
            canvas.Save();
            if (rotation != 0) canvas.RotateDegrees((float)rotation, to.MidX, to.MidY);
            canvas.DrawImage(image, from, to, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
            canvas.Restore();
            if (opacity < 1) canvas.Restore();
            canvas.Flush();
        }
    }

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
