using System.Diagnostics;
using System.Text.Json;
using Silk.NET.Maths;
using Valencia.Core;

namespace Valencia.App;

/// <summary>Bounded acceptance driver using the real window, GPU and audio device path.</summary>
internal sealed class SmokeScenario
{
    private readonly string directory;
    private readonly Stopwatch deadline = Stopwatch.StartNew();
    private readonly Stopwatch phase = new();
    private int state;
    private double pausedPosition;
    private double maximumSkew;
    private int skewSamples;
    private double previousTickSeconds;
    private readonly List<SkewPeak> skewPeaks = [];
    private readonly List<double[]> frameCosts = [];
    private sealed record SkewPeak(int State, double Position, double VideoPosition, double TickGapSeconds, double ElapsedSeconds);
    public void RecordFrame(double layoutMs, double uiMs, double drawMs)
    {
        if (frameCosts.Count < 1000) frameCosts.Add([layoutMs, uiMs, drawMs]);
    }
    public void ObservePresentedSkew(double skew) => maximumSkew = Math.Max(maximumSkew, skew);
    public bool Finished { get; private set; }

    public SmokeScenario(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
    }

    public void Tick(PlayerWindow window)
    {
        if (deadline.Elapsed > TimeSpan.FromSeconds(45))
        {
            throw new TimeoutException($"M0 smoke did not finish (state {state}).");
        }
        if (window.LastError is { } error)
        {
            throw new InvalidOperationException("M0 smoke failed on the real player path.", error);
        }
        var session = window.Session;
        if (window.Busy || session is null)
        {
            return;
        }
        double position = session.PositionSeconds;
        double elapsed = deadline.Elapsed.TotalSeconds;
        double tickGap = elapsed - previousTickSeconds;
        previousTickSeconds = elapsed;
        if (session.IsPlaying && session.CurrentFrame is { } frame)
        {
            double skew = Math.Abs(position - frame.PositionSeconds);
            if (skew > maximumSkew)
            {
                maximumSkew = skew;
                skewPeaks.Add(new(state, position, frame.PositionSeconds, tickGap, elapsed));
            }
            skewSamples++;
        }
        switch (state)
        {
            case 0:
                Require(session.Info.AudioStreamIndex is not null, "Smoke fixture must contain a real audio stream.");
                Require(session.Info.DurationSeconds >= 6, "Smoke fixture must be at least six seconds.");
                window.Execute(TransportAction.TogglePlayback);
                state = 1;
                break;
            case 1 when position >= 1.4:
                window.Execute(TransportAction.TogglePlayback);
                pausedPosition = session.PositionSeconds;
                phase.Restart();
                state = 2;
                break;
            case 2 when phase.ElapsedMilliseconds >= 250:
                Require(Math.Abs(position - pausedPosition) < 0.015, "Audio device clock advanced while paused.");
                window.Seek(3.2, play: false);
                state = 3;
                break;
            case 3:
                Require(Math.Abs(position - 3.2) < 0.015, "Seek did not reset transport time.");
                Require(session.CurrentFrame is not null && Math.Abs(session.CurrentFrame.PositionSeconds - 3.2) < 0.08, "Seek selected the wrong decoded frame.");
                window.Capture(Path.Combine(directory, "seek-frame.png"));
                window.Execute(TransportAction.TogglePlayback);
                state = 4;
                break;
            case 4 when position >= 3.8:
                window.Graphics.Window.Size = new Vector2D<int>(960, 540);
                state = 5;
                break;
            case 5 when position >= 4.3:
                window.Seek(session.Info.DurationSeconds - 0.5, play: true);
                state = 6;
                break;
            case 6 when !session.IsPlaying && position >= session.Info.DurationSeconds - 0.01:
                File.WriteAllText(Path.Combine(directory, "av-skew-peaks.json"), JsonSerializer.Serialize(skewPeaks));
                File.WriteAllText(Path.Combine(directory, "frame-costs.json"), JsonSerializer.Serialize(new { columns = new[] { "layoutMs", "uiMs", "drawMs" }, frames = frameCosts }));
                Require(skewSamples > 20 && maximumSkew <= 0.15, $"Video/audio skew exceeded 150 ms: {maximumSkew:0.000}s.");
                PlaybackQualification.Require(maximumSkew, window.Graphics.ValidationErrorCount);
                Require(window.Graphics.UploadedVideoFrames > 30, "Decoded video did not advance through the GPU.");
                Require(window.Graphics.PresentedFrames > 30, "Native presentation did not run.");
                window.Capture(Path.Combine(directory, "final-frame.png"));
                File.WriteAllText(Path.Combine(directory, "smoke.json"), JsonSerializer.Serialize(new
                {
                    outcome = "passed",
                    source = session.Info,
                    gpu = window.Graphics.GpuName,
                    validationAvailable = window.Graphics.ValidationAvailable,
                    audioBackend = session.AudioBackend,
                    nativeWindow = true,
                    gpuReadbackMatchesDecodedPixels = true,
                    softwareDecode = true,
                    windowResize = true,
                    pauseClockStable = true,
                    seekSeconds = 3.2,
                    maximumAvSkewSeconds = maximumSkew,
                    sampledFrames = skewSamples,
                    presentedFrames = window.Graphics.PresentedFrames,
                    uploadedVideoFrames = window.Graphics.UploadedVideoFrames,
                    finalGenerationUnderrunFrames = session.UnderrunFrames,
                    elapsedSeconds = deadline.Elapsed.TotalSeconds
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"M0 smoke passed: {directory}; max AV skew {maximumSkew * 1000:0.0} ms");
                Finished = true;
                break;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
