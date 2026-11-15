using System.Diagnostics;
using NAudio.Wave;
using Valencia.Core;

namespace Valencia.Media;

/// <summary>One player generation. Transport/frame selection belong to its owner; device-clock reads are serialized with its preview producer.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class PlaybackSession : IAsyncDisposable
{
    private readonly DecodeSession decode;
    private readonly WaveOut? output;
    private readonly Stopwatch silentClock = new();
    private VideoFrame? pending;
    private double silentPosition;
    private double lastPosition;
    private Exception? deviceFailure;
    private bool usingTailClock;
    private readonly object clockGate = new();

    private PlaybackSession(DecodeSession decode, WaveOut? output)
    {
        this.decode = decode;
        this.output = output;
        silentPosition = decode.StartSeconds;
        lastPosition = decode.StartSeconds;
    }

    public MediaInfo Info => decode.Info;
    public bool IsPlaying { get; private set; }
    public VideoFrame? CurrentFrame { get; private set; }
    public long UnderrunFrames => decode.Audio.UnderrunFrames;
    public string AudioBackend => output is null ? "No audio stream" : "NAudio 3 / WaveOut / device clock";
    public Exception? Failure => deviceFailure ?? decode.Failure;

    public static async Task<PlaybackSession> OpenAsync(string path, double seconds = 0, CancellationToken cancellation = default)
    {
        MediaInfo info = await Task.Run(() => MediaProbe.Read(path), cancellation).ConfigureAwait(false);
        var decode = new DecodeSession(info, seconds, cancellation, realtimePreview: true);
        WaveOut? output = null;
        try
        {
            bool available = await decode.Video.WaitToReadAsync(cancellation).ConfigureAwait(false);
            if (!available)
            {
                throw decode.Failure ?? new InvalidDataException("No video frame could be decoded at this position.");
            }
            if (info.AudioStreamIndex is not null)
            {
                // Prebuffer 100 ms or the complete short remainder before starting the device.
                while (decode.Audio.BufferedFrames < 4800 && !decode.Audio.ProducerCompleted)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (decode.Failure is { } failure)
                    {
                        throw failure;
                    }
                    // A full video queue cannot prevent the independent audio decoder from filling.
                    await Task.Delay(5, cancellation).ConfigureAwait(false);
                }
                output = new WaveOut { BufferMilliseconds = 30, NumberOfBuffers = 3 };
                output.Init(decode.Audio);
            }
            var session = new PlaybackSession(decode, output);
            decode.AttachPresentationClock(() => PreviewFrameClock.TransportTicks(session.PositionSeconds));
            if (output is not null)
            {
                output.PlaybackStopped += (_, args) =>
                {
                    if (args.Exception is not null)
                    {
                        Volatile.Write(ref session.deviceFailure, args.Exception);
                    }
                };
            }
            session.UpdateFrame();
            return session;
        }
        catch
        {
            try { await decode.DisposeAsync().ConfigureAwait(false); }
            finally { output?.Dispose(); }
            throw;
        }
    }

    public double PositionSeconds
    {
        get
        {
            lock (clockGate)
            {
                double position;
                if (output is not null && !usingTailClock)
                {
                    long played = output.GetPosition() / decode.Audio.WaveFormat.BlockAlign;
                    position = decode.StartSeconds + (double)decode.Audio.MediaFramesAtDevicePosition(played) / AudioStreamBuffer.SampleRate;
                    if (IsPlaying && decode.Audio.Ended && output.PlaybackState == PlaybackState.Stopped)
                    {
                        silentPosition = Math.Max(lastPosition, position);
                        silentClock.Restart();
                        usingTailClock = true;
                    }
                }
                else
                {
                    position = silentPosition + silentClock.Elapsed.TotalSeconds;
                }
                lastPosition = Math.Clamp(Math.Max(lastPosition, position), decode.StartSeconds, Info.DurationSeconds);
                return lastPosition;
            }
        }
    }

    public void Play()
    {
        lock (clockGate)
        {
            if (IsPlaying)
            {
                return;
            }
            IsPlaying = true;
            if (output is not null && !usingTailClock)
            {
                output.Play();
            }
            else
            {
                silentClock.Start();
            }
        }
    }

    public void Pause()
    {
        lock (clockGate)
        {
            if (!IsPlaying)
            {
                return;
            }
            output?.Pause();
            silentClock.Stop();
            IsPlaying = false;
        }
    }

    public bool UpdateFrame()
    {
        double position = PositionSeconds;
        bool changed = false;
        while (true)
        {
            if (pending is null && !decode.Video.TryRead(out pending))
            {
                break;
            }
            if (CurrentFrame is not null && pending.PositionSeconds > position + 0.001)
            {
                break;
            }
            CurrentFrame = pending;
            pending = null;
            changed = true;
        }
        if (IsPlaying && position >= Info.DurationSeconds)
        {
            Pause();
        }
        return changed;
    }

    public async ValueTask DisposeAsync()
    {
        // Stop the callback before releasing its provider; cancel blocked channel writers next.
        try
        {
            output?.Stop();
        }
        finally
        {
            // The video producer reads the device clock. Keep that device alive
            // until cancellation has joined both decode workers, even on device failure.
            try { await decode.DisposeAsync().ConfigureAwait(false); }
            finally { output?.Dispose(); }
        }
    }
}
