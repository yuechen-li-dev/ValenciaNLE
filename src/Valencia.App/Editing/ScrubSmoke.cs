using System.Diagnostics;
using System.Text.Json;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.App.Editing;

/// <summary>Native-window regression using the same coordinate/capture handlers as Silk mouse callbacks.</summary>
internal sealed class ScrubSmoke(string directory)
{
    private readonly Stopwatch deadline = Stopwatch.StartNew();
    private readonly Stopwatch latency = new();
    private readonly List<double> latencies = [];
    private SequenceTime expected;
    private int trial;
    private bool awaiting, startedPlayback, sourceOpened, pausedDuringOpening;
    private long? revision;
    public bool Finished { get; private set; }

    public void Tick(EditorWindow window)
    {
        if (deadline.Elapsed.TotalSeconds > 90) throw new TimeoutException($"Scrub smoke timed out at {trial}.");
        if (window.Busy) return;
        var e = window.Editor;
        revision ??= e.Session.Snapshot.Revision;
        if (!sourceOpened)
        {
            window.Action("MonitorSource"); sourceOpened = true; return;
        }
        if (awaiting)
        {
            Require(window.Playback?.Current?.Time == expected, "A stale or incorrect seek result was published.");
            Require(e.Playhead == expected, "Playhead differs from the requested final position.");
            Require(window.InputFailures == 0, "Pointer callback failed.");
            Require(e.Session.Snapshot.Revision == revision, "Scrubbing changed the document.");
            latencies.Add(latency.Elapsed.TotalMilliseconds); awaiting = false; trial++;
        }
        if (trial < 24)
        {
            double seconds = Math.Min(5, Math.Max(.1, e.Sequence.Duration.TotalSeconds - .1));
            var v = e.Viewport; double y = window.RulerY + 10;
            double start = v.X(new((long)(seconds * .9 * SequenceTime.TicksPerSecond)));
            double end = v.X(new((long)(seconds * ((trial % 6) / 6.0) * SequenceTime.TicksPerSecond)));
            latency.Restart();
            window.PointerDown((start, y));
            for (int i = 1; i <= 100; i++) window.PointerMove((start + (end - start) * i / 100, y));
            window.PointerUp((end, y));
            expected = e.Playhead; awaiting = true; return;
        }
        if (!pausedDuringOpening)
        {
            window.Seek(default, true); window.Action("Pause"); pausedDuringOpening = true; return;
        }
        if (!startedPlayback)
        {
            Require(window.Playback?.IsPlaying == false, "Pause did not cancel the pending playback start.");
            window.Action("Play"); startedPlayback = true; latency.Restart(); return;
        }
        if (window.Playback?.IsPlaying == true && latency.Elapsed.TotalSeconds < 1) return;
        window.Action("Pause");
        var playback = window.Playback ?? throw new InvalidOperationException("Transport did not reopen after scrubbing.");
        Require(playback.CanPlay, "Transport failed to reopen synchronized audio after scrubbing.");
        PlaybackQualification.Require(window.MaximumAvSkew, window.Graphics.ValidationErrorCount);
        Require(playback.UnderrunFrames == 0, "Audio underrun after scrubbing.");
        long callbackFailures = window.InputFailures;
        window.HandlePointer(() => throw new InvalidOperationException("Scrub smoke: deliberate native callback boundary regression probe."));
        Require(window.InputFailures == callbackFailures + 1 && window.Editor.Status.StartsWith("Timeline input error:", StringComparison.Ordinal),
            "Input exception was not diagnosed and contained.");
        Require(e.Session.Snapshot.Revision == revision, "Failed input changed the document.");
        e.Status = "Scrub smoke passed, including deliberate callback error recovery.";
        Directory.CreateDirectory(directory);
        window.Capture(Path.Combine(directory, "scrub.png"));
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            outcome = "passed", gpu = window.Graphics.GpuName, runtime = Environment.Version.ToString(),
            source = e.Session.Snapshot.Document.Assets[0], trials = trial, pointerMoves = trial * 100,
            callbackFailures, injectedCallbackFailureReported = true, requestToDisplayedFrameMs = latencies,
            sourceToTimelineSwitch = true, pauseCancelsPendingPlay = true,
            medianMs = (latencies.Order().ElementAt((latencies.Count - 1) / 2) + latencies.Order().ElementAt(latencies.Count / 2)) / 2,
            p95Ms = latencies.Order().ElementAt((int)(latencies.Count * .95)),
            seekRequests = window.SeekRequests, staleGenerationsRejected = window.StaleGenerationsRejected,
            maximumAvSkewSeconds = window.MaximumAvSkew, underrunFrames = window.Playback?.UnderrunFrames,
            validationAvailable = window.Graphics.ValidationAvailable, validationErrors = window.Graphics.ValidationErrorCount,
            revisionUnchanged = e.Session.Snapshot.Revision == revision,
            scope = "Native GPU/audio window; actual coordinate/capture handlers, synthetic events. Physical pointer drags qualified separately."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Finished = true;
    }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
