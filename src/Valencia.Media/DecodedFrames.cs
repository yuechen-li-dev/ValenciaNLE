using Valencia.Core;

namespace Valencia.Media;

/// <summary>Owned, tightly packed top-to-bottom RGBA8 frame. Queues bound residency.</summary>
public sealed record VideoFrame(MediaTimestamp SourceTimestamp, double PositionSeconds, int Width, int Height, byte[] Rgba,
    MediaTimestamp? Duration = null, MediaTimestamp? DecodeTimestamp = null)
{
    public DecodedFrameTiming? Timing { get; init; }
}

/// <summary>Transient monotonic host diagnostics, independent of authoritative media timestamps.</summary>
public sealed record DecodedFrameTiming(long ReadyHostTicks, double DecoderReadMilliseconds, double RgbaPreparationMilliseconds);

public sealed record AudioBlock(float[] Samples);
