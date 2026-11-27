using System.Runtime.InteropServices;
using System.Collections.Frozen;
using System.Threading.Channels;
using NAudio.Wave;

namespace Valencia.Media;

/// <summary>Linear stereo peak and RMS for the most recent buffer handed to the audio device.</summary>
public readonly record struct AudioOutputLevels(int Frames, float LeftPeak, float RightPeak, float LeftRms, float RightRms);
public sealed record AudioMeterReading(AudioOutputLevels Output, IReadOnlyDictionary<Guid, AudioOutputLevels> Tracks, double? MomentaryLufs)
{
    public static AudioMeterReading Empty { get; } = new(default, FrozenDictionary<Guid, AudioOutputLevels>.Empty, null);
}

internal sealed record MonitoredAudio(float[] Samples, IReadOnlyDictionary<Guid, float[]> Tracks);

/// <summary>Bounded streaming PCM provider. The device callback never waits for the decoder.</summary>
public sealed class AudioStreamBuffer : IWaveProvider
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BlockFrames = 2048;
    public const int QueueCapacity = 16;
    private sealed record QueuedAudio(ReadOnlyMemory<float> Samples, IReadOnlyDictionary<Guid, float[]>? Tracks, int TrackOffset);
    private readonly Channel<QueuedAudio> blocks;
    private readonly object clockGate = new();
    private readonly Queue<(long Start, long End)> silence = new();
    private QueuedAudio? current;
    private int offset;
    private long physicalFrames;
    private long retiredSilence;
    private long queuedSamples;
    private long underrunFrames;
    private readonly MomentaryLoudness loudness;
    private readonly Dictionary<Guid, LevelAccumulator> trackLevels = [];
    private AudioMeterReading meterReading = AudioMeterReading.Empty;
    private volatile bool producerCompleted;
    private volatile float gain = 1;

    public AudioStreamBuffer(int sampleRate = SampleRate, int capacity = QueueCapacity)
    {
        if (capacity is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(capacity));
        blocks = Channel.CreateBounded<QueuedAudio>(new BoundedChannelOptions(capacity)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, Channels);
        loudness = new(sampleRate);
    }
    public WaveFormat WaveFormat { get; }
    public long BufferedFrames => Interlocked.Read(ref queuedSamples) / Channels;
    public long UnderrunFrames => Interlocked.Read(ref underrunFrames);
    public AudioOutputLevels OutputLevels => MeterReading.Output;
    public AudioMeterReading MeterReading => Volatile.Read(ref meterReading);
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
            await QueueAsync(new(samples.Slice(start, count).ToArray(), null, 0), cancellation).ConfigureAwait(false);
        }
    }

    // The playback evaluator transfers ownership of all arrays, so slices share storage without copying.
    internal async ValueTask WriteMonitoredAsync(MonitoredAudio audio, CancellationToken cancellation)
    {
        for (int start = 0; start < audio.Samples.Length; start += BlockFrames * Channels)
        {
            int count = Math.Min(audio.Samples.Length - start, BlockFrames * Channels);
            await QueueAsync(new(audio.Samples.AsMemory(start, count), audio.Tracks, start), cancellation).ConfigureAwait(false);
        }
    }

    private async ValueTask QueueAsync(QueuedAudio block, CancellationToken cancellation)
    {
        Interlocked.Add(ref queuedSamples, block.Samples.Length);
        try { await blocks.Writer.WriteAsync(block, cancellation).ConfigureAwait(false); }
        catch { Interlocked.Add(ref queuedSamples, -block.Samples.Length); throw; }
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
        trackLevels.Clear();
        while (written < output.Length)
        {
            if (current is null && !blocks.Reader.TryRead(out current))
            {
                break;
            }
            int count = Math.Min(output.Length - written, current.Samples.Length - offset);
            current.Samples.Span.Slice(offset, count).CopyTo(output[written..]);
            if (current.Tracks is { } tracks)
                foreach (var (id, samples) in tracks)
                    CollectionsMarshal.GetValueRefOrAddDefault(trackLevels, id, out _)
                        .Add(samples.AsSpan(current.TrackOffset + offset, count));
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
        bool final = current is null && blocks.Reader.Completion.IsCompleted;
        int delivered = final ? written : output.Length;
        output[written..delivered].Clear();
        int frames = delivered / Channels;
        var levels = new LevelAccumulator();
        levels.Add(output[..delivered]);
        double? momentary = loudness.Observe(output[..delivered]);
        // A plain dictionary: built on the device callback every buffer and read once or twice by the UI,
        // so FrozenDictionary's construction-time analysis would cost more than it saves.
        IReadOnlyDictionary<Guid, AudioOutputLevels> channels = trackLevels.Count == 0 ? FrozenDictionary<Guid, AudioOutputLevels>.Empty
            : trackLevels.ToDictionary(pair => pair.Key, pair => pair.Value.Levels(frames));
        Volatile.Write(ref meterReading, frames == 0 ? AudioMeterReading.Empty : new(levels.Levels(frames), channels, momentary));
        lock (clockGate)
        {
            long missingFrames = (delivered - written) / Channels;
            if (missingFrames > 0)
            {
                silence.Enqueue((physicalFrames + written / Channels, physicalFrames + delivered / Channels));
                Interlocked.Add(ref underrunFrames, missingFrames);
            }
            physicalFrames += delivered / Channels;
            if (final) ended = true;
        }
        return final ? written * sizeof(float) : buffer.Length;
    }

    private struct LevelAccumulator
    {
        private float leftPeak, rightPeak;
        private double leftPower, rightPower;
        public void Add(ReadOnlySpan<float> samples)
        {
            for (int index = 0; index + 1 < samples.Length; index += Channels)
            {
                float left = samples[index], right = samples[index + 1];
                if (float.IsFinite(left))
                {
                    leftPeak = Math.Max(leftPeak, Math.Abs(left));
                    leftPower += (double)left * left;
                }
                if (float.IsFinite(right))
                {
                    rightPeak = Math.Max(rightPeak, Math.Abs(right));
                    rightPower += (double)right * right;
                }
            }
        }
        public readonly AudioOutputLevels Levels(int frames) => frames == 0 ? default
            : new(frames, leftPeak, rightPeak, (float)Math.Sqrt(leftPower / frames), (float)Math.Sqrt(rightPower / frames));
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
