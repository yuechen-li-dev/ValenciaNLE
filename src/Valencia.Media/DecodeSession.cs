using System.Threading.Channels;
using Valencia.Core;

namespace Valencia.Media;

/// <summary>A seek generation. Cancellation terminates both workers before their native contexts are released.</summary>
public sealed class DecodeSession : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping;
    private readonly Channel<VideoFrame> video;
    private readonly Task videoWorker;
    private readonly Task audioWorker;
    private Exception? failure;
    private readonly PreviewFrameClock presentationClock = new();
    private readonly bool realtimePreview;
    internal void AttachPresentationClock(Func<long> clock) => presentationClock.Attach(clock);

    public DecodeSession(MediaInfo info, double positionSeconds, CancellationToken cancellation = default, bool realtimePreview = false)
    {
        this.realtimePreview = realtimePreview;
        Info = info;
        StartSeconds = Math.Clamp(positionSeconds, 0, info.DurationSeconds);
        stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        int capacity = Math.Clamp(32 * 1024 * 1024 / checked(info.Width * info.Height * 4), 1, 4);
        VideoCapacity = capacity;
        video = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            // Frames own managed RGBA arrays. Eviction drops only obsolete preview
            // references; no GPU lease or audio buffer is stored in this channel.
            FullMode = realtimePreview ? BoundedChannelFullMode.DropOldest : BoundedChannelFullMode.Wait
        });
        Audio = new AudioStreamBuffer();
        videoWorker = Task.Run(DecodeVideoAsync);
        audioWorker = info.AudioStreamIndex is null ? Task.CompletedTask : Task.Run(DecodeAudioAsync);
        if (info.AudioStreamIndex is null)
        {
            Audio.Complete();
        }
    }

    public MediaInfo Info { get; }
    public double StartSeconds { get; }
    public int VideoCapacity { get; }
    public ChannelReader<VideoFrame> Video => video.Reader;
    public AudioStreamBuffer Audio { get; }
    public Exception? Failure => Volatile.Read(ref failure);

    private async Task DecodeVideoAsync()
    {
        try
        {
            using var decoder = new FfmpegDecoder(Info, Info.VideoStreamIndex, StartSeconds);
            VideoFrame? previous = null;
            bool reachedTarget = false;
            bool firstPublished = false;
            while (decoder.Read(stopping.Token))
            {
                VideoFrame decoded = decoder.CopyVideo(Info.StartSeconds);
                if (!reachedTarget && decoded.PositionSeconds < StartSeconds)
                {
                    previous = decoded;
                    continue;
                }
                if (!reachedTarget && previous is not null)
                {
                    await video.Writer.WriteAsync(previous, stopping.Token).ConfigureAwait(false);
                    firstPublished = true;
                }
                reachedTarget = true;
                if (realtimePreview && firstPublished)
                    await presentationClock.WaitUntilDueAsync(PreviewFrameClock.TransportTicks(decoded.PositionSeconds), stopping.Token).ConfigureAwait(false);
                await video.Writer.WriteAsync(decoded, stopping.Token).ConfigureAwait(false);
                firstPublished = true;
            }
            if (!reachedTarget && previous is not null)
            {
                await video.Writer.WriteAsync(previous, stopping.Token).ConfigureAwait(false);
            }
            video.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            video.Writer.TryComplete();
        }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref failure, error, null);
            video.Writer.TryComplete(error);
        }
    }

    private async Task DecodeAudioAsync()
    {
        try
        {
            using var decoder = new FfmpegDecoder(Info, Info.AudioStreamIndex!.Value, StartSeconds);
            long expectedSample = checked((long)Math.Round(StartSeconds * AudioStreamBuffer.SampleRate));
            bool initializedTimestamp = false;
            long outputSample = 0;
            float[] silence = new float[AudioStreamBuffer.BlockFrames * 2];
            while (decoder.Read(stopping.Token))
            {
                if (!initializedTimestamp)
                {
                    outputSample = checked((long)Math.Round((decoder.Timestamp.TotalSeconds - Info.StartSeconds) * AudioStreamBuffer.SampleRate));
                    initializedTimestamp = true;
                }
                float[] samples = decoder.CopyAudio();
                int frames = samples.Length / 2;
                int skipFrames = checked((int)Math.Clamp(expectedSample - outputSample, 0, frames));
                while (outputSample > expectedSample)
                {
                    int gap = (int)Math.Min(outputSample - expectedSample, AudioStreamBuffer.BlockFrames);
                    await Audio.WriteAsync(silence.AsMemory(0, gap * 2), stopping.Token).ConfigureAwait(false);
                    expectedSample += gap;
                }
                if (skipFrames < frames)
                {
                    await Audio.WriteAsync(samples.AsMemory(skipFrames * 2), stopping.Token).ConfigureAwait(false);
                    expectedSample += frames - skipFrames;
                }
                outputSample += frames;
            }
            float[] tail;
            while ((tail = decoder.DrainAudio()).Length > 0)
            {
                await Audio.WriteAsync(tail, stopping.Token).ConfigureAwait(false);
            }
            Audio.Complete();
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            Audio.Complete();
        }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref failure, error, null);
            Audio.Complete(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(videoWorker, audioWorker).ConfigureAwait(false);
        stopping.Dispose();
    }
}
