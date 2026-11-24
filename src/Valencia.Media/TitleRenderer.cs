using SkiaSharp;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Media;

/// <summary>Draws the sequence's title layer in output coordinates after captions.</summary>
internal sealed class TitleRenderer : IDisposable
{
    private readonly SKTypeface typeface;
    private readonly SKFont font;
    private readonly SKPaint outline = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint fill = new() { IsAntialias = true };

    public TitleRenderer()
    {
        using var stream = typeof(TitleRenderer).Assembly.GetManifestResourceStream("Valencia.Media.TitleFont.ttf")!;
        using var data = SKData.Create(stream);
        typeface = SKTypeface.FromData(data) ?? throw new InvalidOperationException("Embedded title font could not be loaded.");
        font = new SKFont(typeface, 1);
    }

    public unsafe void Draw(byte[] rgba, int width, int height, SequenceTime time,
        IEnumerable<TitleCue> titles, CancellationToken cancellation)
    {
        fixed (byte* pointer = rgba)
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque), (nint)pointer, width * 4)
                ?? throw new InvalidOperationException("Title raster surface could not be created.");
            var canvas = surface.Canvas;
            foreach (var title in titles)
            {
                cancellation.ThrowIfCancellationRequested();
                if (TitleMotion.At(title, time) is not { Opacity: > 0 } pose) continue;
                var transform = title.Transform?.At(time.Ticks - title.Start.Ticks) ?? new TransformPose(title.X, title.Y, 1, 0, 1);
                double opacity = pose.Opacity * transform.Opacity;
                if (opacity <= 0) continue;

                font.Size = (float)(title.Size * height);
                font.MeasureText(title.Text, out SKRect bounds);
                float stroke = Math.Max(1, font.Size * .12f);
                float peakScale = title.Motion switch { "impact" => 1.4f, "pop" => 1.05f, _ => 1f };
                float fit = Math.Min(1, width * .9f / ((bounds.Width + stroke) * peakScale));
                font.Size *= fit * (float)(pose.Scale * transform.Scale);
                font.MeasureText(title.Text, out bounds);
                outline.StrokeWidth = Math.Max(1, font.Size * .12f);
                outline.Color = Tint(title.Outline, opacity);
                fill.Color = Tint(title.Color, opacity);
                float x = (float)((transform.X + pose.OffsetX) * width);
                float y = (float)((transform.Y + pose.OffsetY) * height);
                float left = x - bounds.MidX;
                float baseline = y - bounds.MidY;
                canvas.Save();
                if (transform.Rotation % 360 != 0) canvas.RotateDegrees((float)(transform.Rotation % 360), x, y);
                canvas.DrawText(title.Text, left, baseline, font, outline);
                canvas.DrawText(title.Text, left, baseline, font, fill);
                canvas.Restore();
            }
            canvas.Flush();
        }
    }

    private static SKColor Tint(uint rgba, double opacity) =>
        new((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)Math.Round((byte)rgba * opacity));

    public void Dispose()
    {
        fill.Dispose();
        outline.Dispose();
        font.Dispose();
        typeface.Dispose();
    }
}
