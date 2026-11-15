using System.Threading.Channels;
using NAudio.Wave;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Media;

/// <summary>One immutable timeline seek generation, with independent bounded workers and a real device clock.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SequencePlayback : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping;
    private readonly Channel<SequenceFrame> video;
    private readonly AudioStreamBuffer audio;
    private readonly Task videoWorker, audioWorker;
    private WaveOut? output;
    private SequenceFrame? pending;
    private readonly TimelineQueries queries;
    private Exception? failure;
    private SequenceTime lastPosition;
    private readonly object clockGate = new();
    private readonly PreviewFrameClock presentationClock = new();
    private readonly OutputProfile? profile;
    public ProjectSnapshot Snapshot { get; }
    public SequenceDocument Sequence { get; }
    public SequenceTime Start { get; }
    public SequenceFrame? Current { get; private set; }
    public bool IsPlaying { get; private set; }
    public Exception? Failure => Volatile.Read(ref failure);
    public long UnderrunFrames => audio.UnderrunFrames;
    private SequencePlayback(ProjectSnapshot snapshot, Guid sequenceId, SequenceTime start, CancellationToken cancellation, OutputProfile? profile)
    {
        Snapshot = snapshot; queries = new(snapshot); Sequence = queries.Sequence(sequenceId);
        Start = start; lastPosition = start;
        this.profile = profile;
        stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        int capacity = Math.Clamp(32 * 1024 * 1024 / checked((profile?.Width ?? Sequence.Video.Width) * (profile?.Height ?? Sequence.Video.Height) * 4), 1, 4);
        video = Channel.CreateBounded<SequenceFrame>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
        });
        // Edited cuts may jump hours forward and need sample-anchor discovery. Keep a bounded
        // 64-block horizon (~2.7 s / 1 MiB at 48 kHz), independently of the short video queue.
        audio = new(Sequence.AudioSampleRate, capacity: 64);
        videoWorker = Task.Run(() => ProduceVideo(sequenceId));
        audioWorker = Task.Run(() => ProduceAudio(sequenceId));
    }
    public static async Task<SequencePlayback> OpenAsync(ProjectSnapshot snapshot, Guid sequenceId, SequenceTime start, CancellationToken cancellation = default, OutputProfile? profile = null)
    {
        var session = new SequencePlayback(snapshot, sequenceId, start, cancellation, profile);
        try
        {
            await session.video.Reader.WaitToReadAsync(cancellation).ConfigureAwait(false);
            while (session.audio.BufferedFrames < session.Sequence.AudioSampleRate / 4 && !session.audio.ProducerCompleted)
            {
                if (session.Failure is { } error) throw error;
                await Task.Delay(2, cancellation).ConfigureAwait(false);
            }
            if (session.Failure is { } failure) throw failure;
            session.output = new WaveOut { BufferMilliseconds = 30, NumberOfBuffers = 3 };
            session.output.Init(session.audio);
            session.presentationClock.Attach(() => session.Position.Ticks);
            session.output.PlaybackStopped += (_, e) => { if (e.Exception is { } error) Interlocked.CompareExchange(ref session.failure, error, null); };
            session.Update();
            return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }
    public SequenceTime Position
    {
        get
        {
            lock (clockGate)
            {
                long played = output is null ? 0 : audio.MediaFramesAtDevicePosition(output.GetPosition() / audio.WaveFormat.BlockAlign);
                long ticks = checked(Start.Ticks + TimelineTime.FromSamples(played, Sequence.AudioSampleRate).Ticks);
                if (IsPlaying && audio.Ended && output?.PlaybackState == PlaybackState.Stopped) ticks = Sequence.Duration.Ticks;
                lastPosition = new(Math.Clamp(Math.Max(lastPosition.Ticks, ticks), Start.Ticks, Sequence.Duration.Ticks));
                return lastPosition;
            }
        }
    }
    public void Play() { lock (clockGate) { output?.Play(); IsPlaying = true; } }
    public void Pause() { lock (clockGate) { output?.Pause(); IsPlaying = false; } }
    public bool Update()
    {
        var position = Position;
        bool changed = false;
        while (true)
        {
            if (pending is null && !video.Reader.TryRead(out pending)) break;
            if (Current is not null && pending.Time.Ticks > position.Ticks) break;
            Current = pending; pending = null; changed = true;
        }
        if (Current is { } current)
        {
            var active = queries.Evaluate(Sequence.Id, position).Where(a => a.Kind == TrackKind.Video).Select(a => a.Clip.Id);
            if (!active.SequenceEqual(current.VideoClips))
            {
                Current = new(position, current.Width, current.Height, new byte[current.Rgba.Length], [.. active]);
                changed = true; // black while a newly required cut frame is unavailable; never display the obsolete source
            }
        }
        if (IsPlaying && position.Ticks >= Sequence.Duration.Ticks) Pause();
        return changed;
    }
    private async Task ProduceVideo(Guid sequenceId)
    {
        try
        {
            using var evaluator = new SequenceEvaluator(Snapshot, sequenceId, profile, useProxies: true);
            await video.Writer.WriteAsync(evaluator.Video(Start, stopping.Token), stopping.Token);
            var frameRate = profile?.FrameRate ?? Sequence.Video.FrameRate;
            long first = TimelineTime.FrameIndex(Start, frameRate, TimeRounding.Floor) + 1;
            for (long i = first; ; i++)
            {
                var time = TimelineTime.FromFrames(i, frameRate);
                if (time.Ticks >= Sequence.Duration.Ticks) break;
                var frame = evaluator.Video(time, stopping.Token);
                await presentationClock.WaitUntilDueAsync(time.Ticks, stopping.Token).ConfigureAwait(false);
                await video.Writer.WriteAsync(frame, stopping.Token).ConfigureAwait(false);
            }
            video.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { video.Writer.TryComplete(); }
        catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); video.Writer.TryComplete(error); }
    }
    private async Task ProduceAudio(Guid sequenceId)
    {
        try
        {
            using var evaluator = new SequenceEvaluator(Snapshot, sequenceId);
            long samples = TimelineTime.SampleIndex(new(Sequence.Duration.Ticks - Start.Ticks), Sequence.AudioSampleRate, TimeRounding.Ceiling);
            for (long i = 0; i < samples; i += AudioStreamBuffer.BlockFrames)
                await audio.WriteAsync(evaluator.Audio(Start, i, (int)Math.Min(AudioStreamBuffer.BlockFrames, samples - i), stopping.Token), stopping.Token);
            audio.Complete();
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { audio.Complete(); }
        catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); audio.Complete(error); }
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            output?.Stop();
        }
        finally
        {
            try
            {
                await stopping.CancelAsync().ConfigureAwait(false);
                await Task.WhenAll(videoWorker, audioWorker).ConfigureAwait(false);
            }
            finally
            {
                output?.Dispose(); output = null; stopping.Dispose();
            }
        }
    }
}
