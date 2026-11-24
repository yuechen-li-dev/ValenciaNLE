using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.Wave;

namespace Valencia.Media;

/// <summary>Bounded streaming PCM provider. The device callback never waits for the decoder.</summary>
public sealed class AudioStreamBuffer : IWaveProvider
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BlockFrames = 2048;
    public const int QueueCapacity = 16;
    private readonly Channel<AudioBlock> blocks;
    private readonly object clockGate = new();
    private readonly Queue<(long Start, long End)> silence = new();
    private AudioBlock? current;
    private int offset;
    private long physicalFrames;
    private long retiredSilence;
    private long queuedSamples;
    private long underrunFrames;
    private volatile bool producerCompleted;
    private volatile float gain = 1;

    public AudioStreamBuffer(int sampleRate = SampleRate, int capacity = QueueCapacity)
    {
        if (capacity is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(capacity));
        blocks = Channel.CreateBounded<AudioBlock>(new BoundedChannelOptions(capacity)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, Channels);
    }
    public WaveFormat WaveFormat { get; }
    public long BufferedFrames => Interlocked.Read(ref queuedSamples) / Channels;
    public long UnderrunFrames => Interlocked.Read(ref underrunFrames);
    private volatile bool ended;
    public bool Ended => ended;
    public Task Completion => blocks.Reader.Completion;
    public bool ProducerCompleted => producerCompleted;
    public float Gain
    {
        get => gain;
        set => gain = float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<float> samples, CancellationToken cancellation)
    {
        for (int start = 0; start < samples.Length; start += BlockFrames * Channels)
        {
            int count = Math.Min(samples.Length - start, BlockFrames * Channels);
            float[] owned = samples.Slice(start, count).ToArray();
            Interlocked.Add(ref queuedSamples, count);
            try
            {
                await blocks.Writer.WriteAsync(new AudioBlock(owned), cancellation).ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Add(ref queuedSamples, -count);
                throw;
            }
        }
    }

    public void Complete(Exception? error = null)
    {
        blocks.Writer.TryComplete(error);
        producerCompleted = true;
    }

    public int Read(Span<byte> buffer)
    {
        Span<float> output = MemoryMarshal.Cast<byte, float>(buffer);
        int written = 0;
        while (written < output.Length)
        {
            if (current is null && !blocks.Reader.TryRead(out current))
            {
                break;
            }
            int count = Math.Min(output.Length - written, current.Samples.Length - offset);
            current.Samples.AsSpan(offset, count).CopyTo(output[written..]);
            offset += count;
            written += count;
            Interlocked.Add(ref queuedSamples, -count);
            if (offset == current.Samples.Length)
            {
                current = null;
                offset = 0;
            }
        }

        float level = gain;
        if (level != 1)
            for (int i = 0; i < written; i++) output[i] *= level;

        // Return a short final block at EOF, rather than endless trailing silence.
        if (current is null && blocks.Reader.Completion.IsCompleted)
        {
            ended = true;
            lock (clockGate)
            {
                physicalFrames += written / Channels;
            }
            return written * sizeof(float);
        }

        output[written..].Clear();
        lock (clockGate)
        {
            long missingFrames = (output.Length - written) / Channels;
            if (missingFrames > 0)
            {
                silence.Enqueue((physicalFrames + written / Channels, physicalFrames + output.Length / Channels));
                Interlocked.Add(ref underrunFrames, missingFrames);
            }
            physicalFrames += output.Length / Channels;
        }
        return buffer.Length;
    }

    public long MediaFramesAtDevicePosition(long playedFrames)
    {
        lock (clockGate)
        {
            while (silence.TryPeek(out var range) && range.End <= playedFrames)
            {
                retiredSilence += range.End - range.Start;
                silence.Dequeue();
            }
            long missing = retiredSilence;
            foreach (var range in silence)
            {
                missing += Math.Clamp(playedFrames - range.Start, 0, range.End - range.Start);
            }
            return Math.Max(0, playedFrames - missing);
        }
    }
}
