using Valencia.Media;
using Xunit;

namespace Valencia.Tests;

public sealed class MediaTests(MediaFixture fixture) : IClassFixture<MediaFixture>
{
    [Fact]
    public async Task RealtimePreviewEvictsObsoleteFramesWhileTheConsumerIsBlocked()
    {
        var info = MediaProbe.Read(fixture.Mp4) with { AudioStreamIndex = null };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var decode = new DecodeSession(info, 0, deadline.Token, realtimePreview: true);
        var first = await decode.Video.ReadAsync(deadline.Token);
        long ticks = 0;
        decode.AttachPresentationClock(() => Interlocked.Read(ref ticks));
        await Task.Delay(50, deadline.Token);
        Assert.False(decode.Video.TryPeek(out _)); // paused transport cannot run to EOF
        Interlocked.Exchange(ref ticks, PreviewFrameClock.TransportTicks(.8));
        // Do not consume anything during catch-up: the old Wait queue remains
        // stuck on its first four frames and fails this real-decoder regression.
        while (!decode.Video.TryPeek(out var frame) || frame.PositionSeconds < .8 - decode.VideoCapacity * .034)
            await Task.Delay(5, deadline.Token);
        var frames = new List<VideoFrame>();
        while (decode.Video.TryRead(out var frame)) frames.Add(frame);
        Assert.InRange(frames.Count, 1, decode.VideoCapacity);
        Assert.InRange(.8 - frames[^1].PositionSeconds, 0, .034);
        Assert.InRange(first.PositionSeconds, 0, .034);
    }

    [Fact]
    public async Task RealtimePreviewCancelsBeforeTheDeviceClockIsAttached()
    {
        var info = MediaProbe.Read(fixture.Mp4) with { AudioStreamIndex = null };
        var decode = new DecodeSession(info, 0, realtimePreview: true);
        _ = await decode.Video.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await decode.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null(decode.Failure);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodesRealContainersAndDrainsBFramesInPresentationOrder(bool matroska)
    {
        var info = MediaProbe.Read(matroska ? fixture.Mkv : fixture.Mp4);
        Assert.Equal(160, info.Width);
        Assert.Equal(90, info.Height);
        Assert.NotNull(info.AudioStreamIndex);
        Assert.InRange(info.NominalFrameRate, 29.96, 29.98);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var decode = new DecodeSession(info, 0, deadline.Token);
        var times = new List<double>();
        var hashes = new HashSet<string>();
        await foreach (var frame in decode.Video.ReadAllAsync(deadline.Token))
        {
            Assert.Equal(160 * 90 * 4, frame.Rgba.Length);
            times.Add(frame.PositionSeconds);
            hashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(frame.Rgba)));
        }
        Assert.Null(decode.Failure);
        Assert.Equal(60, times.Count);
        Assert.True(times.SequenceEqual(times.Order()));
        Assert.InRange(times[^1], 1.95, 2.05);
        Assert.True(hashes.Count > 40, "Decoded frames did not change.");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.811)]
    [InlineData(1.95)]
    public async Task SeekDecodesThePresentationIntervalContainingTheTarget(double target)
    {
        var info = MediaProbe.Read(fixture.Mp4);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var decode = new DecodeSession(info, target, deadline.Token);
        var first = await decode.Video.ReadAsync(deadline.Token);
        Assert.True(first.PositionSeconds <= target + 0.001);
        Assert.InRange(target - first.PositionSeconds, -0.001, 0.034);
    }

    [Fact]
    public async Task IndependentAudioDecoderResamplesToStereoWithoutConsumingVideo()
    {
        var info = MediaProbe.Read(fixture.Mp4);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var decode = new DecodeSession(info, 0, deadline.Token);
        byte[] bytes = new byte[AudioStreamBuffer.BlockFrames * 2 * sizeof(float)];
        var nonzero = new List<float>();
        long validFrames = 0;
        while (!decode.Audio.Ended)
        {
            deadline.Token.ThrowIfCancellationRequested();
            long before = decode.Audio.UnderrunFrames;
            int read = decode.Audio.Read(bytes);
            validFrames += read / 8 - (decode.Audio.UnderrunFrames - before);
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, read));
            foreach (float value in samples)
            {
                Assert.True(float.IsFinite(value));
                if (Math.Abs(value) > 0.001)
                {
                    nonzero.Add(value);
                }
            }
            await Task.Delay(1, deadline.Token);
        }
        Assert.Null(decode.Failure);
        Assert.InRange(validFrames, 95_000, 99_000);
        Assert.True(nonzero.Count > 100_000);
        Assert.InRange(nonzero.Max(), 0.05f, 0.2f);
    }

    [Fact]
    public async Task CancellationReleasesWorkersBlockedOnFullQueues()
    {
        var info = MediaProbe.Read(fixture.Mp4);
        var decode = new DecodeSession(info, 0);
        await Task.Delay(200);
        Assert.InRange(decode.Video.Count, 1, decode.VideoCapacity);
        Assert.InRange(decode.Audio.BufferedFrames, 1, (AudioStreamBuffer.QueueCapacity + 1) * AudioStreamBuffer.BlockFrames);
        await decode.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task NonzeroContainerStartIsNormalizedWithoutDiscardingSourceTimestamp()
    {
        var info = MediaProbe.Read(fixture.Offset);
        Assert.InRange(info.StartSeconds, 11.9, 12.1);
        Assert.InRange(info.DurationSeconds, 1.9, 2.2);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var decode = new DecodeSession(info, 0, deadline.Token);
        var first = await decode.Video.ReadAsync(deadline.Token);
        Assert.InRange(first.PositionSeconds, 0, 0.1);
        Assert.True(first.SourceTimestamp.TotalSeconds > 11);
    }

    [Fact]
    public void VideoOnlyAndInvalidInputsHaveExplicitResults()
    {
        Assert.Null(MediaProbe.Read(fixture.Silent).AudioStreamIndex);
        Assert.Throws<FileNotFoundException>(() => MediaProbe.Read(Path.Combine(fixture.DirectoryPath, "missing.mkv")));
        string invalid = Path.Combine(fixture.DirectoryPath, "invalid.mp4");
        File.WriteAllText(invalid, "not a media container");
        var error = Assert.Throws<InvalidDataException>(() => MediaProbe.Read(invalid));
        Assert.Contains("Open media", error.Message);
    }
}
