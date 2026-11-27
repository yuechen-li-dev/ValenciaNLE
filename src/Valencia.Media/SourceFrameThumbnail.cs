using SkiaSharp;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Media;

public sealed record SourcePoint(double X, double Y);
public sealed record SourceFrameInfo(Guid AssetId, int Stream, SequenceTime RequestedOffset, MediaTimestamp PresentationTimestamp,
    int SourceWidth, int SourceHeight, int ImageWidth, int ImageHeight, SourcePoint? Point, string Output);

/// <summary>A single ungraded source frame and an optional normalized pixel-edge coordinate.</summary>
public static class SourceFrameThumbnail
{
    public static VideoFrame Read(MediaAsset asset, int streamIndex, SequenceTime offset, CancellationToken cancellation = default)
    {
        var stream = asset.Streams.FirstOrDefault(s => s.Index == streamIndex && s.Kind == TrackKind.Video)
            ?? throw new ArgumentException("Source frame requires a video stream.");
        if (offset.Ticks < 0) throw new ArgumentException("Source offset must be nonnegative.");
        if ((long)(stream.Width ?? 0) * (stream.Height ?? 0) > 16_777_216)
            throw new NotSupportedException("Source thumbnails support inputs up to 16 megapixels.");
        var origin = stream.Start ?? new MediaTimestamp(0, stream.TimeBaseNumerator, stream.TimeBaseDenominator);
        var position = new SourcePosition(origin).Shift(offset);
        if (stream.End is { } end && position.CompareTo(new(end)) >= 0)
            throw new ArgumentException("Requested offset is at or after the video stream end.");
        return SequenceEvaluator.ReadSourceFrame(asset, stream, position, cancellation);
    }

    public static SourceFrameInfo Write(MediaAsset asset, int streamIndex, SequenceTime offset, int maximumEdge, string output,
        SourcePoint? imagePoint = null, CancellationToken cancellation = default)
    {
        if (offset.Ticks < 0 || maximumEdge is < 64 or > 1024)
            throw new ArgumentException("Source offset must be nonnegative and thumbnail edge must be between 64 and 1024 pixels.");
        if (!Path.GetExtension(output).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Source thumbnail output must be a new .png file.");
        string fullPath = Path.GetFullPath(output);
        if (File.Exists(fullPath)) throw new IOException("Source thumbnail output already exists.");
        var frame = Read(asset, streamIndex, offset, cancellation);
        double scale = Math.Min(1, (double)maximumEdge / Math.Max(frame.Width, frame.Height));
        int width = Math.Max(1, (int)Math.Round(frame.Width * scale)), height = Math.Max(1, (int)Math.Round(frame.Height * scale));
        SourcePoint? point = null;
        if (imagePoint is { } pixel)
        {
            if (!double.IsFinite(pixel.X) || !double.IsFinite(pixel.Y) || pixel.X < 0 || pixel.Y < 0 || pixel.X > width || pixel.Y > height)
                throw new ArgumentException("Thumbnail point must lie inside its pixel edges.");
            point = new(pixel.X / width, pixel.Y / height);
        }
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var data = Encode(frame, width, height);
            using (var file = new FileStream(temporary, FileMode.CreateNew)) data.SaveTo(file);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(asset.Id, streamIndex, offset, frame.SourceTimestamp, frame.Width, frame.Height, width, height, point, fullPath);
    }

    private static unsafe SKData Encode(VideoFrame frame, int width, int height)
    {
        fixed (byte* rgba = frame.Rgba)
        {
            using var source = new SKPixmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Opaque), (nint)rgba, frame.Width * 4);
            using var image = SKImage.FromPixels(source) ?? throw new InvalidOperationException("Source thumbnail image failed.");
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque))
                ?? throw new InvalidOperationException("Source thumbnail surface failed.");
            surface.Canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            using var raster = surface.Snapshot();
            return raster.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("Source thumbnail PNG encode failed.");
        }
    }
}
