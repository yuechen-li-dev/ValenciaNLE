using System.Diagnostics;
using System.Text.Json;
using Valencia.Media;
using Valencia.Rendering;

namespace Valencia.App;

/// <summary>Bounded hardware comparisons on the real presenter. Not an alternate playback implementation.</summary>
internal static class RenderProbe
{
    public static int Run(string[] args)
    {
        string Option(string name, string fallback)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
        string mode = Option("--render-probe", "static");
        string directory = Path.GetFullPath(Option("--output", "artifacts/local/render-probe"));
        string source = Option("--source", "artifacts/local/player-fixtures/av.mp4");
        int count = int.Parse(Option("--frames", "240"), System.Globalization.CultureInfo.InvariantCulture);
        int stallMs = int.Parse(Option("--stall-ms", "0"), System.Globalization.CultureInfo.InvariantCulture);
        if (stallMs is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(stallMs));
        if (count is < 120 or > 1800) throw new ArgumentOutOfRangeException(nameof(count));
        if (mode is not ("static" or "dynamic" or "video" or "av" or "changing-ui"))
            throw new ArgumentException("Probe mode must be static, dynamic, video, av or changing-ui.");
        Directory.CreateDirectory(directory);
        using var graphics = new NativeVideoPresenter(tracePath: Path.Combine(directory, "trace.json"));
        var view = new TransportView();
        var pcm = new byte[640 * 360 * 4];
        for (int index = 0; index < pcm.Length; index += 4)
        {
            pcm[index] = (byte)(index / 4 % 251);
            pcm[index + 1] = (byte)(index / (640 * 4) % 251);
            pcm[index + 2] = 63;
            pcm[index + 3] = 255;
        }
        PlaybackSession? playback = null;
        var frames = new List<object>();
        int stalledDraws = 0;
        try
        {
            if (mode is "video" or "av")
            {
                var opened = Stopwatch.StartNew();
                playback = PlaybackSession.OpenAsync(source).GetAwaiter().GetResult();
                graphics.RecordCpuPhase("decoderOpenAndPrebufferMs", opened.Elapsed.TotalMilliseconds);
                if (mode == "video" && playback.Info.AudioStreamIndex is not null)
                    throw new ArgumentException("Video-only comparison requires a remuxed source without an audio stream.");
            }
            else graphics.UpdateVideo(640, 360, pcm);
            var layout = Stopwatch.StartNew();
            var prepared = view.Prepare(playback?.Info, 0, playback is not null, mode, false);
            graphics.RecordCpuPhase("layoutAndPresentationBuildMs", layout.Elapsed.TotalMilliseconds);
            graphics.UpdateUi(prepared.PresentationFrame);
            // Prepare the first native UI/atlas before starting the device clock,
            // as the application's opening screen does. Cold font setup is not playback.
            if (playback?.CurrentFrame is { } first) graphics.UpdateVideo(first.Width, first.Height, first.Rgba);
            playback?.Play();
            var clock = Stopwatch.StartNew();
            double maximumSkew = 0;
            for (int index = 0; index < count; index++)
            {
                long started = Stopwatch.GetTimestamp();
                graphics.Window.DoEvents();
                // Explicit fault injection for the bounded-queue regression, never
                // attributed to a driver/GPU stall in qualification reports.
                if (stallMs > 0 && index > 0 && index % 60 == 0) Thread.Sleep(stallMs);
                if (graphics.Window.IsClosing) throw new OperationCanceledException("Probe window closed.");
                if (clock.Elapsed.TotalMinutes > 5) throw new TimeoutException("Probe exceeded five minutes.");
                if (mode == "dynamic")
                {
                    long preparedAt = Stopwatch.GetTimestamp();
                    pcm[0] = (byte)index;
                    graphics.RecordCpuPhase("cpuFramePreparationMs", Stopwatch.GetElapsedTime(preparedAt).TotalMilliseconds);
                    graphics.UpdateVideo(640, 360, pcm);
                }
                if (playback is not null)
                {
                    long selected = Stopwatch.GetTimestamp();
                    bool updated = playback.UpdateFrame();
                    if (playback.CurrentFrame is { } selectedFrame)
                        graphics.RecordMediaFrame(new { selectedFrame.SourceTimestamp, selectedFrame.PositionSeconds,
                            selectedFrame.Timing, selectedHostTicks = Stopwatch.GetTimestamp(), clockSeconds = playback.PositionSeconds });
                    graphics.RecordCpuPhase("frameSelectionMs", Stopwatch.GetElapsedTime(selected).TotalMilliseconds);
                    if (updated && playback.CurrentFrame is { } frame) graphics.UpdateVideo(frame.Width, frame.Height, frame.Rgba);
                    if (playback.IsPlaying && playback.CurrentFrame is { } current)
                        maximumSkew = Math.Max(maximumSkew, Math.Abs(playback.PositionSeconds - current.PositionSeconds));
                    if (playback.Failure is { } failure) throw new InvalidOperationException("Decoder/output failed.", failure);
                }
                if (mode == "changing-ui")
                {
                    long preparedAt = Stopwatch.GetTimestamp();
                    prepared = view.Prepare(null, index / 60.0, false, $"Changing declaration {index}", false);
                    graphics.RecordCpuPhase("layoutAndPresentationBuildMs", Stopwatch.GetElapsedTime(preparedAt).TotalMilliseconds);
                    graphics.UpdateUi(prepared.PresentationFrame);
                }
                long drawStarted = Stopwatch.GetTimestamp();
                graphics.Draw();
                double drawMs = Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds;
                if (playback is { IsPlaying: true, CurrentFrame: { } presented })
                {
                    double observedClock = playback.PositionSeconds;
                    graphics.ObservePresentationClock(presented.PositionSeconds, observedClock);
                    maximumSkew = Math.Max(maximumSkew, Math.Abs(observedClock - presented.PositionSeconds));
                }
                if (index >= 10 && drawMs >= 200) stalledDraws++;
                frames.Add(new { index, drawMs, workMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                double remaining = (index + 1) / 60.0 - clock.Elapsed.TotalSeconds;
                if (remaining > .001) Thread.Sleep(TimeSpan.FromSeconds(remaining));
            }
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            {
                mode, source = playback?.Info, injectedUiStallMilliseconds = stallMs, graphics.GpuName, graphics.ValidationAvailable,
                validationErrors = graphics.ValidationErrorCount, maximumSkew, stalledDraws,
                underrunFrames = playback?.UnderrunFrames, elapsedSeconds = clock.Elapsed.TotalSeconds,
                frames, presentedFrames = graphics.PresentedFrames,
                outcome = !graphics.ValidationAvailable ? "unavailable" : graphics.ValidationErrorCount == 0
                    && PlaybackQualification.TimingPasses(maximumSkew) && stalledDraws == 0 ? "passed" : "failed"
            }));
            if (!graphics.ValidationAvailable) return 77;
            return graphics.ValidationErrorCount == 0 && PlaybackQualification.TimingPasses(maximumSkew) && stalledDraws == 0 ? 0 : 1;
        }
        finally
        {
            playback?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
