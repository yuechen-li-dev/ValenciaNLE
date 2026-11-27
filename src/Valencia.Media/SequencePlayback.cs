using System.Collections.Immutable;
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
    private readonly Channel<QueuedFrame> video;
    private readonly AudioStreamBuffer audio;
    private readonly Task videoWorker, audioWorker;
    private WaveOut? output;
    private QueuedFrame? pending;
    private readonly int reversePrebuffer;
    private volatile bool videoProducerCompleted;
    private readonly TimelineQueries queries;
    private Exception? failure;
    private SequenceTime lastPosition;
    private readonly object clockGate = new();
    private readonly PreviewFrameClock presentationClock = new();
    private readonly OutputProfile? profile;
    private sealed record QueuedFrame(SequenceFrame Frame, long DueTicks);
    public ProjectSnapshot Snapshot { get; }
    public SequenceDocument Sequence { get; }
    public SequenceTime Start { get; }
    public SequenceFrame? Current { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool IsReverse { get; }
    /// <summary>Paused video-only seeks must reopen at the requested timestamp before starting audio playback.</summary>
    public bool CanPlay { get; }
    public Exception? Failure => Volatile.Read(ref failure);
    public long UnderrunFrames => audio.UnderrunFrames;
    public float MonitorGain { get => audio.Gain; set => audio.Gain = value; }
    public AudioOutputLevels OutputLevels => IsPlaying ? audio.OutputLevels : default;
    /// <summary>Audio tracks isolated for this playback generation. An empty set monitors the normal mix.</summary>
    public ImmutableHashSet<Guid> SoloAudioTracks { get; }
    private SequencePlayback(ProjectSnapshot snapshot, Guid sequenceId, SequenceTime start, CancellationToken cancellation,
        OutputProfile? profile, SequenceFrame? preview = null, bool reverse = false, ImmutableHashSet<Guid>? soloAudioTracks = null)
    {
        Snapshot = snapshot; queries = new(snapshot); Sequence = queries.Sequence(sequenceId);
        Start = start; lastPosition = start;
        IsReverse = reverse;
        SoloAudioTracks = soloAudioTracks is { Count: > 0 }
            ? soloAudioTracks.Intersect(Sequence.Tracks.Where(track => track.Kind == TrackKind.Audio).Select(track => track.Id))
            : ImmutableHashSet<Guid>.Empty;
        this.profile = profile;
        stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        int frameBytes = checked((profile?.Width ?? Sequence.Video.Width) * (profile?.Height ?? Sequence.Video.Height) * 4);
        // Reverse decoding crosses history boundaries by seeking back to a keyframe and decoding forward.
        // Put that work ahead of the audio clock. Never drop a future reverse frame to make room for another.
        int capacity = reverse ? Math.Clamp(96 * 1024 * 1024 / frameBytes, 1, 18) : Math.Clamp(32 * 1024 * 1024 / frameBytes, 1, 4);
        var rate = profile?.FrameRate ?? Sequence.Video.FrameRate;
        reversePrebuffer = reverse ? (int)Math.Clamp(TimelineTime.FrameIndex(new(SequenceTime.TicksPerSecond / 5), rate, TimeRounding.Ceiling), 1, capacity) : 0;
        video = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true, SingleWriter = true, FullMode = reverse ? BoundedChannelFullMode.Wait : BoundedChannelFullMode.DropOldest
        });
        // Edited cuts may jump hours forward and need sample-anchor discovery. Keep a bounded
        // 64-block horizon (~2.7 s / 1 MiB at 48 kHz), independently of the short video queue.
        audio = new(Sequence.AudioSampleRate, capacity: 64);
        CanPlay = preview is null;
        if (preview is not null)
        {
            Current = preview; video.Writer.TryComplete(); audio.Complete();
            videoWorker = audioWorker = Task.CompletedTask;
        }
        else
        {
            videoWorker = Task.Run(() => ProduceVideo(sequenceId));
            audioWorker = Task.Run(() => ProduceAudio(sequenceId));
        }
    }
    public static SequencePlayback OpenPreview(SequencePreview preview, ProjectSnapshot snapshot, Guid sequenceId,
        SequenceTime start, CancellationToken cancellation = default, OutputProfile? profile = null) =>
        new(snapshot, sequenceId, start, cancellation, profile, preview.Video(snapshot, sequenceId, start, cancellation, profile));
    public static Task<SequencePlayback> OpenAsync(ProjectSnapshot snapshot, Guid sequenceId, SequenceTime start,
        CancellationToken cancellation = default, OutputProfile? profile = null, bool reverse = false)
        => OpenAsync(snapshot, sequenceId, start, cancellation, profile, reverse, soloAudioTracks: null);

    public static async Task<SequencePlayback> OpenAsync(ProjectSnapshot snapshot, Guid sequenceId, SequenceTime start,
        CancellationToken cancellation, OutputProfile? profile, bool reverse, ImmutableHashSet<Guid>? soloAudioTracks)
    {
        var session = new SequencePlayback(snapshot, sequenceId, start, cancellation, profile, reverse: reverse,
            soloAudioTracks: soloAudioTracks);
        try
        {
            await session.video.Reader.WaitToReadAsync(cancellation).ConfigureAwait(false);
            while ((session.audio.BufferedFrames < session.Sequence.AudioSampleRate / 4 && !session.audio.ProducerCompleted)
                || (reverse && session.video.Reader.Count < session.reversePrebuffer && !session.videoProducerCompleted))
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
                long elapsed = TimelineTime.FromSamples(played, Sequence.AudioSampleRate).Ticks;
                long ticks = IsReverse ? Start.Ticks - elapsed : checked(Start.Ticks + elapsed);
                if (IsPlaying && audio.Ended && output?.PlaybackState == PlaybackState.Stopped)
                    ticks = IsReverse ? 0 : Sequence.Duration.Ticks;
                lastPosition = IsReverse
                    ? new(Math.Clamp(Math.Min(lastPosition.Ticks, ticks), 0, Start.Ticks))
                    : new(Math.Clamp(Math.Max(lastPosition.Ticks, ticks), Start.Ticks, Sequence.Duration.Ticks));
                return lastPosition;
            }
        }
    }
    public void Play()
    {
        if (!CanPlay) throw new InvalidOperationException("Reopen paused preview with OpenAsync before playing audio.");
        lock (clockGate) { output?.Play(); IsPlaying = true; }
    }
    public void Pause() { lock (clockGate) { output?.Pause(); IsPlaying = false; } }
    public bool Update()
    {
        var position = Position;
        bool changed = false;
        while (true)
        {
            if (pending is null && !video.Reader.TryRead(out pending)) break;
            if (Current is not null && (IsReverse ? pending.DueTicks < position.Ticks : pending.DueTicks > position.Ticks)) break;
            Current = pending.Frame; pending = null; changed = true;
        }
        if (Current is { } current)
        {
            var sampleTime = IsReverse ? ReverseBoundary(position) : position;
            var active = queries.Evaluate(Sequence.Id, sampleTime).Where(a => a.Kind == TrackKind.Video).Select(a => a.Clip.Id);
            if (!active.SequenceEqual(current.VideoClips))
            {
                Current = new(position, current.Width, current.Height, new byte[current.Rgba.Length], [.. active]);
                changed = true; // black while a newly required cut frame is unavailable; never display the obsolete source
            }
        }
        if (IsPlaying && (IsReverse ? position.Ticks == 0 : position.Ticks >= Sequence.Duration.Ticks)) Pause();
        return changed;
    }
    private async Task ProduceVideo(Guid sequenceId)
    {
        try
        {
            using var evaluator = new SequenceEvaluator(Snapshot, sequenceId, profile, useProxies: true, retainVideoHistory: IsReverse);
            var initial = IsReverse ? ReverseBoundary(Start) : Start;
            evaluator.PrepareTransition(initial, IsReverse, initial: true, stopping.Token);
            await video.Writer.WriteAsync(new(evaluator.VideoForTransport(initial, stopping.Token), Start.Ticks), stopping.Token);
            var frameRate = profile?.FrameRate ?? Sequence.Video.FrameRate;
            long initialFrame = TimelineTime.FrameIndex(initial, frameRate, TimeRounding.Floor);
            if (IsReverse)
            {
                for (long i = initialFrame - 1; i >= 0; i--)
                {
                    var due = TimelineTime.FromFrames(i + 1, frameRate);
                    var time = TimelineTime.FromFrames(i, frameRate);
                    await video.Writer.WaitToWriteAsync(stopping.Token).ConfigureAwait(false);
                    evaluator.PrepareTransition(time, reverse: true, initial: false, stopping.Token);
                    var frame = evaluator.Video(time, stopping.Token);
                    await video.Writer.WriteAsync(new(frame, due.Ticks), stopping.Token).ConfigureAwait(false);
                }
            }
            else
            {
                for (long i = initialFrame + 1; ; i++)
                {
                    var time = TimelineTime.FromFrames(i, frameRate);
                    if (time.Ticks >= Sequence.Duration.Ticks) break;
                    evaluator.PrepareTransition(time, reverse: false, initial: false, stopping.Token);
                    var frame = evaluator.VideoForTransport(time, stopping.Token);
                    await presentationClock.WaitUntilDueAsync(time.Ticks, stopping.Token).ConfigureAwait(false);
                    await video.Writer.WriteAsync(new(frame, time.Ticks), stopping.Token).ConfigureAwait(false);
                }
            }
            video.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { video.Writer.TryComplete(); }
        catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); video.Writer.TryComplete(error); }
        finally { videoProducerCompleted = true; }
    }
    private async Task ProduceAudio(Guid sequenceId)
    {
        try
        {
            using var evaluator = new SequenceEvaluator(Snapshot, sequenceId);
            int rate = Sequence.AudioSampleRate;
            if (IsReverse)
            {
                long end = TimelineTime.SampleIndex(Start, rate, TimeRounding.Ceiling);
                while (end > 0)
                {
                    // Evaluate a bounded range forward so FFmpeg/resampling and
                    // the normal mixing rules still see contiguous source samples.
                    int count = (int)Math.Min(end, rate);
                    var block = evaluator.ReverseAudio(end, count, stopping.Token, SoloAudioTracks);
                    await audio.WriteAsync(block, stopping.Token).ConfigureAwait(false);
                    end -= count;
                }
            }
            else
            {
                long samples = TimelineTime.SampleIndex(new(Sequence.Duration.Ticks - Start.Ticks), rate, TimeRounding.Ceiling);
                for (long i = 0; i < samples; i += AudioStreamBuffer.BlockFrames)
                {
                    int count = (int)Math.Min(AudioStreamBuffer.BlockFrames, samples - i);
                    await audio.WriteAsync(evaluator.Audio(Start, i, count, stopping.Token, SoloAudioTracks), stopping.Token);
                }
            }
            audio.Complete();
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { audio.Complete(); }
        catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); audio.Complete(error); }
    }
    private static SequenceTime ReverseBoundary(SequenceTime position) => new(Math.Max(0, position.Ticks - 1));
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
