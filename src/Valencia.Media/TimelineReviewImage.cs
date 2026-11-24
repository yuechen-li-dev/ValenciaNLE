using System.Collections.Immutable;
using System.Globalization;
using SkiaSharp;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Media;

public sealed record ReviewSample(SequenceTime Time, int X, int Y, int Width, int Height, ImmutableArray<Guid> VideoClips);
public sealed record ReviewImage(int Width, int Height, ImmutableArray<ReviewSample> Samples);

/// <summary>Renders original-media composites, with thumbnail scaling after timeline evaluation.</summary>
public static class TimelineReviewImage
{
    public static (int Width, int Height) Fit(int width, int height, int maximumEdge, int alignment = 1)
    {
        double scale = Math.Min(1, (double)(maximumEdge / alignment * alignment) / Math.Max(width, height));
        int Axis(int size) => Math.Max(alignment, (int)Math.Round(size * scale / alignment) * alignment);
        return (Axis(width), Axis(height));
    }

    public static unsafe ReviewImage Write(ProjectSnapshot snapshot, Guid sequenceId, OutputProfile? profile,
        ImmutableArray<long> times, int maximumEdge, bool sheet, string path, CancellationToken cancellation)
    {
        using var evaluator = new SequenceEvaluator(snapshot, sequenceId, profile);
        var (width, height) = Fit(profile?.Width ?? evaluator.Sequence.Video.Width, profile?.Height ?? evaluator.Sequence.Video.Height, maximumEdge);
        int columns = sheet ? Math.Min(3, times.Length) : 1;
        int labelHeight = sheet ? 24 : 0;
        int rowHeight = height + labelHeight;
        int imageWidth = columns * width;
        int imageHeight = (times.Length + columns - 1) / columns * rowHeight;
        using var surface = SKSurface.Create(new SKImageInfo(imageWidth, imageHeight, SKColorType.Rgba8888, SKAlphaType.Opaque))
            ?? throw new InvalidOperationException("Review image surface could not be created.");
        surface.Canvas.Clear(new SKColor(21, 22, 26));
        using var font = new SKFont(SKTypeface.Default, 13);
        using var ink = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var samples = ImmutableArray.CreateBuilder<ReviewSample>(times.Length);
        for (int i = 0; i < times.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var time = new SequenceTime(times[i]);
            var frame = evaluator.Video(time, cancellation);
            int x = i % columns * width;
            int y = i / columns * rowHeight;
            fixed (byte* rgba = frame.Rgba)
            {
                using var source = new SKPixmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Opaque),
                    (nint)rgba, frame.Width * 4);
                using var image = SKImage.FromPixels(source) ?? throw new InvalidOperationException("Review frame could not be loaded.");
                surface.Canvas.DrawImage(image, new SKRect(x, y, x + width, y + height),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            }
            if (sheet)
            {
                string label = (i + 1).ToString(CultureInfo.InvariantCulture) + "  " + time.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + " s";
                surface.Canvas.Save();
                surface.Canvas.ClipRect(new SKRect(x, y + height, x + width, y + rowHeight));
                surface.Canvas.DrawText(label, x + 5, y + height + 17, font, ink);
                surface.Canvas.Restore();
            }
            samples.Add(new(time, x, y, width, height, frame.VideoClips));
        }
        using var raster = surface.Snapshot();
        using var png = raster.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("Review PNG encode failed.");
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".valencia-review-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew)) png.SaveTo(file);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(imageWidth, imageHeight, samples.MoveToImmutable());
    }
}
