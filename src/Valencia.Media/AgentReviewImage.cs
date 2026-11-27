using SkiaSharp;

namespace Valencia.Media;

public sealed record ReviewTile(byte[]? Rgba, int Width, int Height, IReadOnlyList<string> Labels, object Metadata);
public sealed record AudioReviewBin(double From, double To, float Minimum, float Maximum, double Rms);
public sealed record AudioReviewMark(double From, double To, string Label, string Kind);

/// <summary>Bounded annotation/layout for frames rendered by the existing compositor and source decoder.</summary>
public static class AgentReviewImage
{
    public static unsafe (int Width, int Height, object[] Samples) Sheet(int count, int columns, int maxEdge, string header,
        Func<int, ReviewTile> render, string path, CancellationToken cancellation)
    {
        int cellWidth = Math.Max(270, maxEdge), imageHeight = maxEdge, labelHeight = 92, margin = 8, headerHeight = 30;
        int rows = (count + columns - 1) / columns;
        int width = cellWidth * columns, height = headerHeight + rows * (imageHeight + labelHeight);
        using var surface = Surface(width, height);
        using var font = new SKFont(SKTypeface.Default, 14);
        using var ink = new SKPaint { Color = new SKColor(235, 242, 250), IsAntialias = true };
        surface.Canvas.Clear(new SKColor(22, 27, 34));
        var samples = new object[count];
        surface.Canvas.DrawText(header, margin, 21, font, ink);
        for (int i = 0; i < count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var tile = render(i);
            int x = i % columns * cellWidth, y = headerHeight + i / columns * (imageHeight + labelHeight);
            using var box = new SKPaint { Color = new SKColor(11, 15, 20) };
            surface.Canvas.DrawRect(new SKRect(x + 2, y, x + cellWidth - 2, y + imageHeight), box);
            (int w, int h) = (0, 0);
            int left = x, top = y;
            if (tile.Rgba is not null)
            {
                (w, h) = TimelineReviewImage.Fit(tile.Width, tile.Height, maxEdge);
                left = x + (cellWidth - w) / 2;
                top = y + (imageHeight - h) / 2;
                fixed (byte* pixels = tile.Rgba)
                {
                    using var source = new SKPixmap(new SKImageInfo(tile.Width, tile.Height, SKColorType.Rgba8888, SKAlphaType.Opaque),
                        (nint)pixels, tile.Width * 4);
                    using var image = SKImage.FromPixels(source) ?? throw new InvalidOperationException("Review frame could not be loaded.");
                    surface.Canvas.DrawImage(image, new SKRect(left, top, left + w, top + h), new SKSamplingOptions(SKFilterMode.Linear));
                }
            }
            surface.Canvas.Save();
            surface.Canvas.ClipRect(new SKRect(x, y + imageHeight, x + cellWidth, y + imageHeight + labelHeight));
            for (int j = 0; j < Math.Min(5, tile.Labels.Count); j++)
                surface.Canvas.DrawText(tile.Labels[j], x + margin, y + imageHeight + 17 * (j + 1), font, ink);
            surface.Canvas.Restore();
            samples[i] = new { index = i, x = left, y = top, width = w, height = h, tile.Metadata };
        }
        Save(surface, path, cancellation);
        return (width, height, samples);
    }

    public static void Audio(int width, int height, string header, string footer, IReadOnlyList<AudioReviewBin> bins,
        IReadOnlyList<AudioReviewMark> marks, string path, CancellationToken cancellation)
    {
        using var surface = Surface(width, height);
        using var font = new SKFont(SKTypeface.Default, 14);
        using var ink = new SKPaint { Color = new SKColor(234, 242, 248), IsAntialias = true };
        using var fill = new SKPaint();
        var c = surface.Canvas;
        c.Clear(new SKColor(22, 27, 34));
        const int margin = 14;
        int plotWidth = width - margin * 2;
        float X(double x) => margin + (float)(Math.Clamp(x, 0, 1) * plotWidth);
        c.DrawText(header, margin, 22, font, ink);
        c.DrawText(footer, margin, height - 10, font, ink);
        c.DrawText("waveform   ·   RMS dBFS below   ·   red: cut   ·   green: activity   ·   grey: silence", margin, 45, font, ink);
        foreach (var m in marks.Where(m => m.Kind is "silence" or "activity"))
        {
            fill.Color = m.Kind == "silence" ? new SKColor(85, 91, 102, 100) : new SKColor(62, 145, 94, 105);
            c.DrawRect(new SKRect(X(m.From), 52, X(m.To), height - 65), fill);
        }
        fill.Color = new SKColor(75, 91, 106);
        c.DrawLine(margin, 91, width - margin, 91, fill);
        float Rms(double v) => 160 - (float)(Math.Clamp(20 * Math.Log10(Math.Max(v, .0001)), -60, 0) + 60) / 2;
        foreach (var bin in bins)
        {
            float x = X((bin.From + bin.To) / 2);
            fill.Color = new SKColor(89, 195, 248);
            c.DrawLine(x, 91 - Math.Clamp(bin.Maximum, -1, 1) * 36, x, 91 - Math.Clamp(bin.Minimum, -1, 1) * 36, fill);
            fill.Color = new SKColor(255, 201, 100);
            c.DrawRect(new SKRect(X(bin.From), Rms(bin.Rms), Math.Max(x + 1, X(bin.To)), 161), fill);
        }
        foreach (var m in marks.Where(m => m.Kind == "cut"))
        {
            fill.Color = new SKColor(248, 110, 114);
            c.DrawLine(X(m.From), 52, X(m.From), height - 49, fill);
        }
        float[] wordEnds = [margin - 4, margin - 4];
        foreach (var m in marks.Where(m => m.Kind == "word"))
        {
            cancellation.ThrowIfCancellationRequested();
            float x = X(m.From), textWidth = font.MeasureText(m.Label, ink);
            int lane = x >= wordEnds[0] + 4 ? 0 : x >= wordEnds[1] + 4 ? 1 : -1;
            fill.Color = new SKColor(140, 163, 255);
            if (lane >= 0 && x + textWidth <= width - margin)
            {
                float y = height - (lane == 0 ? 43 : 25);
                c.DrawLine(x, y + 2, X(m.To), y + 2, fill);
                c.DrawText(m.Label, x, y, font, ink);
                wordEnds[lane] = Math.Max(x + textWidth, X(m.To));
            }
            else c.DrawLine(x, height - 52, X(m.To), height - 52, fill);
        }
        Save(surface, path, cancellation);
    }

    private static SKSurface Surface(int width, int height) =>
        SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque))
        ?? throw new InvalidOperationException("Review surface could not be created.");

    private static void Save(SKSurface surface, string path, CancellationToken cancellation)
    {
        using var raster = surface.Snapshot();
        using var png = raster.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("Review PNG encode failed.");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew)) png.SaveTo(file);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
