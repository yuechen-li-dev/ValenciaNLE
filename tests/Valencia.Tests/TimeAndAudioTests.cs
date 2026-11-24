using System.Runtime.InteropServices;
using Valencia.Core;
using Valencia.Media;
using Xunit;

namespace Valencia.Tests;

public sealed class TimeAndAudioTests
{
    [Fact]
    public void FractionalFrameAndSampleGridsRemainExactAtProjectBoundary()
    {
        Assert.Equal(8_475_667_200, new MediaTimestamp(1, 1001, 30000).ToSequenceTime().Ticks);
        Assert.Equal(SequenceTime.TicksPerSecond, SequenceTime.FromSamples(48_000, 48_000).Ticks);
        Assert.Equal(SequenceTime.TicksPerSecond * 21_600, new MediaTimestamp(21_600, 1, 1).ToSequenceTime().Ticks);
    }

    [Fact]
    public void InvalidTimebasesAndOverflowAreRejectedInsteadOfRoundingThroughDouble()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaTimestamp(1, 1, 0).ToSequenceTime());
        Assert.Throws<OverflowException>(() => new MediaTimestamp(long.MaxValue, 1, 1).ToSequenceTime());
        Assert.Throws<OverflowException>(() => new MediaTimestamp(long.MaxValue, int.MaxValue, 1).ToSequenceTime());
        Assert.Equal(-SequenceTime.TicksPerSecond, new MediaTimestamp(-48_000, 1, 48_000).ToSequenceTime().Ticks);
    }

    [Fact]
    public async Task MonitorVolumeAndMuteScalePcmWithoutChangingTheMediaClock()
    {
        var buffer = new AudioStreamBuffer { Gain = .5f };
        await buffer.WriteAsync(new float[] { .5f, -.5f, 1, -1, .25f, -.25f }, CancellationToken.None);
        buffer.Complete();
        byte[] stereo = new byte[2 * sizeof(float)];
        Assert.Equal(stereo.Length, buffer.Read(stereo));
        Assert.Equal(new[] { .25f, -.25f }, MemoryMarshal.Cast<byte, float>(stereo).ToArray());
        buffer.Gain = 0;
        Assert.Equal(stereo.Length, buffer.Read(stereo));
        Assert.Equal(new float[] { 0, 0 }, MemoryMarshal.Cast<byte, float>(stereo).ToArray());
        buffer.Gain = 1;
        Assert.Equal(stereo.Length, buffer.Read(stereo));
        Assert.Equal(new[] { .25f, -.25f }, MemoryMarshal.Cast<byte, float>(stereo).ToArray());
        Assert.Equal(3, buffer.MediaFramesAtDevicePosition(3));
        Assert.Equal(0, buffer.UnderrunFrames);
    }

    [Fact]
    public async Task DeviceClockCountsOnlyMediaPlayedRatherThanQueuedUnderrunSilence()
    {
        var buffer = new AudioStreamBuffer();
        await buffer.WriteAsync(new float[] { 0.25f, 0.5f, 0.75f, 1 }, CancellationToken.None);
        byte[] output = new byte[4 * 2 * sizeof(float)];
        Assert.Equal(output.Length, buffer.Read(output));
        Assert.Equal(new[] { 0.25f, 0.5f, 0.75f, 1, 0, 0, 0, 0 }, MemoryMarshal.Cast<byte, float>(output).ToArray());
        Assert.Equal(2, buffer.UnderrunFrames);
        Assert.Equal(1, buffer.MediaFramesAtDevicePosition(1));
        Assert.Equal(2, buffer.MediaFramesAtDevicePosition(3));
        Assert.Equal(2, buffer.MediaFramesAtDevicePosition(4));
        await buffer.WriteAsync(new float[] { 0.2f, 0.2f }, CancellationToken.None);
        buffer.Complete();
        Assert.Equal(8, buffer.Read(output));
        Assert.Equal(3, buffer.MediaFramesAtDevicePosition(5));
        Assert.Equal(0, buffer.Read(output));
        Assert.True(buffer.Ended);
    }
}
