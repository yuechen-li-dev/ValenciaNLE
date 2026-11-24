using System.Numerics;
using Valencia.Core.Timeline;

namespace Valencia.Media;

/// <summary>One resolved SDR clip pass. Both backends sample the same integer source pixels.</summary>
internal sealed class ClipEffectFrame(VideoFrame source, AssetStream stream, VideoEffects effects, float opacity, int[] columns, int[] rows,
    RotatedSampling? rotated = null)
{
    internal const int HeaderWords = 20;
    private const int Blur = 1, Pixelate = 2, Cover = 3;
    public VideoFrame Source { get; } = source;
    public int Width => columns.Length;
    public int Height => rows.Length;
    private readonly Vector3 multiplier = new((float)(Math.Pow(2, effects.Exposure) * (1 + .2 * effects.Temperature)),
        (float)Math.Pow(2, effects.Exposure), (float)(Math.Pow(2, effects.Exposure) * (1 - .2 * effects.Temperature)));
    private readonly (int Left, int Top, int Right, int Bottom, int SizeX, int SizeY, int Mode) region = Resolve(effects.Region, source, stream);
    private readonly bool grade = HasColor(effects);
    internal static bool HasColor(VideoEffects v) => v.Exposure != 0 || v.Contrast != 1 || v.Saturation != 1 || v.Temperature != 0;

    internal static int[] Axis(int length, int sourceLength, double start, double span, double scale, double crop, bool centers)
    {
        var indices = new int[length];
        for (int i = 0; i < indices.Length; i++)
        {
            double sample = centers ? i + .5 : i;
            indices[i] = sample < start || sample >= start + span ? -1
                : Math.Clamp((int)((sample - start) / scale + crop * sourceLength), 0, sourceLength - 1);
        }
        return indices;
    }

    private static (int, int, int, int, int, int, int) Resolve(RegionEffect? effect, VideoFrame source, AssetStream stream)
    {
        if (effect is null) return default;
        int width = source.Width, height = source.Height;
        // A lower-resolution proxy should cover the same source area and use the same apparent filter size.
        int Size(int decoded, int? original) => Math.Max(1, (int)Math.Round(effect.Size * (double)decoded / (original ?? decoded), MidpointRounding.AwayFromZero));
        return ((int)Math.Floor(effect.Left * width), (int)Math.Floor(effect.Top * height),
            (int)Math.Ceiling(effect.Right * width), (int)Math.Ceiling(effect.Bottom * height), Size(width, stream.Width), Size(height, stream.Height),
            effect.Mode switch { "blur" => Blur, "pixelate" => Pixelate, "cover" => Cover, _ => throw new ArgumentException("Unknown region effect.") });
    }

    public int ParameterCount => HeaderWords + Width + Height;
    public void Pack(Span<int> values)
    {
        values[0] = Width; values[1] = Height; values[2] = Source.Width; values[3] = region.Mode;
        values[4] = region.Left; values[5] = region.Top; values[6] = region.Right; values[7] = region.Bottom;
        values[8] = region.SizeX;
        values[9] = BitConverter.SingleToInt32Bits(opacity);
        values[10] = BitConverter.SingleToInt32Bits(multiplier.X);
        values[11] = BitConverter.SingleToInt32Bits(multiplier.Y);
        values[12] = BitConverter.SingleToInt32Bits(multiplier.Z);
        values[13] = BitConverter.SingleToInt32Bits((float)effects.Contrast);
        values[14] = BitConverter.SingleToInt32Bits((float)effects.Saturation);
        values[15] = grade ? 1 : 0;
        values[16] = region.SizeY;
        values[17..HeaderWords].Clear();
        columns.CopyTo(values[HeaderWords..]);
        rows.CopyTo(values[(HeaderWords + Width)..]);
    }

    public void Composite(byte[] destination, CancellationToken cancellation)
    {
        for (int y = 0; y < Height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < Width; x++)
            {
                var (sx, sy) = rotated is { } transform ? transform.At(x, y) : (columns[x], rows[y]);
                int offset = (y * Width + x) * 4;
                Vector3 color = Vector3.Zero;
                bool masked = sx >= region.Left && sx < region.Right && sy >= region.Top && sy < region.Bottom;
                if (sx >= 0 && sy >= 0 && !(masked && region.Mode == Cover))
                {
                    color = masked ? Filter(sx, sy) : Pixel(sx, sy);
                    if (grade)
                    {
                        color = (color * (1f / 255) * multiplier - new Vector3(.5f)) * (float)effects.Contrast + new Vector3(.5f);
                        float luma = Vector3.Dot(color, new(.2126f, .7152f, .0722f));
                        color = (new Vector3(luma) + (float)effects.Saturation * (color - new Vector3(luma))) * 255;
                    }
                }
                destination[offset] = Blend(color.X, destination[offset]);
                destination[offset + 1] = Blend(color.Y, destination[offset + 1]);
                destination[offset + 2] = Blend(color.Z, destination[offset + 2]);
            }
        }
    }

    private byte Blend(float sample, byte previous) => (byte)Math.Clamp(sample * opacity + previous * (1 - opacity), 0, 255);

    private Vector3 Pixel(int x, int y)
    {
        int offset = (y * Source.Width + x) * 4;
        var pixels = Source.Rgba;
        return new(pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }

    private Vector3 Filter(int x, int y)
    {
        int stepX = region.SizeX, stepY = region.SizeY;
        if (region.Mode == Pixelate)
        {
            int sx = Math.Min(region.Right - 1, region.Left + (x - region.Left) / stepX * stepX + stepX / 2);
            int sy = Math.Min(region.Bottom - 1, region.Top + (y - region.Top) / stepY * stepY + stepY / 2);
            return Pixel(sx, sy);
        }
        Vector3 sum = Vector3.Zero;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
            sum += Pixel(Math.Clamp(x + dx * stepX, region.Left, region.Right - 1),
                Math.Clamp(y + dy * stepY, region.Top, region.Bottom - 1)) * ((2 - dx * dx) * (2 - dy * dy));
        return sum * (1f / 16);
    }
}

/// <summary>Inverse rotation in output pixels, followed by the normal source crop.</summary>
internal readonly record struct RotatedSampling(double Left, double Top, double Width, double Height, double Scale,
    double CropLeft, double CropTop, int SourceWidth, int SourceHeight, double Cosine, double Sine, double SampleOffset)
{
    public (int X, int Y) At(int x, int y)
    {
        double dx = x + SampleOffset - (Left + Width / 2);
        double dy = y + SampleOffset - (Top + Height / 2);
        double u = Cosine * dx + Sine * dy + Width / 2;
        double v = -Sine * dx + Cosine * dy + Height / 2;
        if (u < 0 || v < 0 || u >= Width || v >= Height) return (-1, -1);
        return (Math.Clamp((int)(u / Scale + CropLeft * SourceWidth), 0, SourceWidth - 1),
            Math.Clamp((int)(v / Scale + CropTop * SourceHeight), 0, SourceHeight - 1));
    }
}
