using System.Diagnostics;
using System.Text.Json;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Media;

namespace Valencia.App.Editing;

internal sealed record PerformanceOptions(double Start, double Seconds, string Work);

/// <summary>Timed observation of distinct pictures that reached the native present path.</summary>
internal sealed class PerformanceSmoke(string directory, PerformanceOptions options) : IDisposable
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly CancellationTokenSource stopping = new();
    private readonly List<double> cadence = [];
    private readonly Dictionary<string, int> backends = [];
    private Task<string>? worker;
    private bool opened;
    private double started = -1, lastPresented;
    private long? picture;
    private int ownedPixels, proxyPictures;
    public bool Finished { get; private set; }

    public void Tick(EditorWindow window)
    {
        if (elapsed.Elapsed.TotalSeconds > options.Seconds + 60) throw new TimeoutException("Native performance trial timed out.");
        if (window.Busy) return;
        if (!opened)
        {
            if (options.Start + options.Seconds + .25 > window.Editor.Sequence.Duration.TotalSeconds)
                throw new ArgumentException("The source sequence is too short for the selected playback range.");
            window.Seek(new((long)(options.Start * SequenceTime.TicksPerSecond)), play: true);
            opened = true;
            return;
        }
        if (window.Playback is not { IsPlaying: true } playback)
            throw new InvalidOperationException("Native performance playback did not stay running.", window.Playback?.Failure);
        if (playback.Failure is { } error) throw new InvalidOperationException("Native playback failed.", error);
        double now = elapsed.Elapsed.TotalSeconds;
        if (started < 0) started = now;
        if (window.Graphics.LastDrawPresented && playback.Current is { } frame && picture != frame.Time.Ticks)
        {
            if (picture is not null) cadence.Add((now - lastPresented) * 1000);
            picture = frame.Time.Ticks;
            lastPresented = now;
            string backend = frame.PrimaryDecode?.Backend ?? "no-source";
            backends[backend] = backends.GetValueOrDefault(backend) + 1;
            if (frame.UsedOwnedSourcePixels) ownedPixels++;
            if (frame.PrimaryUsesProxy) proxyPictures++;
        }
        if (options.Work != "none" && worker is null && now - started >= 2)
            worker = StartWork(window.Editor.Session, window.Editor.SequenceId, playback.Start, stopping.Token);
        if (now - started < options.Seconds) return;

        string work = worker switch
        {
            null => "none",
            { IsCompletedSuccessfully: true } successful => successful.Result,
            { IsFaulted: true } failed => failed.Exception!.GetBaseException().Message,
            _ => "running at end of the measured interval; cancellation requested"
        };
        stopping.Cancel();
        bool passed = PlaybackQualification.TimingPasses(window.MaximumAvSkew) && playback.UnderrunFrames == 0
            && window.Graphics.ValidationErrorCount == 0 && backends.Count > 0;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            outcome = passed ? "passed" : "failed", window.Graphics.GpuName, options.Start,
            playbackSeconds = now - started, timeToPlaybackSeconds = started, sequenceVideo = playback.Sequence.Video,
            distinctPresented = backends.Values.Sum(), presentedFps = backends.Values.Sum() / (now - started),
            cadenceMilliseconds = cadence, backends, ownedPixels, proxyPictures, window.MaximumAvSkew,
            playback.UnderrunFrames, window.Graphics.ValidationAvailable, window.Graphics.ValidationErrorCount,
            background = options.Work, backgroundResult = work
        }, new JsonSerializerOptions { WriteIndented = true }));
        window.Capture(Path.Combine(directory, "playback.png"));
        if (!passed) throw new InvalidOperationException("Native performance smoke failed its timing or underrun contract.");
        Finished = true;
    }

    private Task<string> StartWork(Valencia.Core.Editing.EditingSession session, Guid id, SequenceTime start, CancellationToken token)
        => Task.Run(async () =>
        {
            if (options.Work == "export")
            {
                var result = await SequenceExport.RunAsync(session.Snapshot, id, Path.Combine(directory, "concurrent.mp4"),
                    cancellation: token, range: (start, new(start.Ticks + 5 * SequenceTime.TicksPerSecond)),
                    encoder: ExportCatalog.Encoder("h264-nvenc"));
                return $"completed, {result.Frames} frames in {result.ElapsedSeconds:F2}s";
            }
            await using var engine = new EditingEngine(session);
            var asset = engine.Query().Assets.First();
            var stream = asset.Streams.First(item => item.Kind == (options.Work == "proxy" ? TrackKind.Video : TrackKind.Audio));
            Guid job = engine.Analysis.Start(new(asset.Id, stream.Index, options.Work == "analysis" ? "audio" : options.Work));
            using var registration = token.Register(() => engine.Analysis.Cancel(job));
            var status = await engine.Analysis.WaitAsync(job, token);
            if (status.Error is { } error) return $"{status.State}: {error}";
            return $"{status.State}, cache hit {status.CacheHit}, {status.AnalysisMs:F2}ms";
        }, token);

    public void Dispose()
    {
        stopping.Cancel();
        if (worker is not null)
            try { worker.GetAwaiter().GetResult(); }
            catch (Exception) when (worker.IsCanceled || worker.IsFaulted) { }
        stopping.Dispose();
    }
}
