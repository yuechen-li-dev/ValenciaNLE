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
    private readonly List<long> reversePictures = [];
    private SequenceTime expected;
    private int trial;
    private bool awaiting, startedPlayback, sourceOpened, pausedDuringOpening;
    private int reversePhase;
    private SequenceTime reversePausedAt;
    private long reverseUnderruns;
    private readonly List<double> backwardDragMs = [];
    private long dragFrame;
    private bool dragStarted, dragAwaiting, dragFinished, originalSnapping;
    private long reverseStart;
    private TransitionWindow? reverseTransition;
    private int reverseTransitionPictures;
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
            Require(e.ScrubTarget is { } target && e.TimelinePlayhead == target,
                "The visible scrubber did not follow the pointer before a decode could finish.");
            window.PointerUp((end, y));
            expected = e.Playhead; awaiting = true; return;
        }
        if (!pausedDuringOpening)
        {
            window.Seek(default, true); window.Action("Pause"); pausedDuringOpening = true; return;
        }
        if (!dragFinished)
        {
            var viewport = e.Viewport;
            double y = window.RulerY + 10;
            var rate = e.Sequence.Video.FrameRate;
            if (!dragStarted)
            {
                originalSnapping = e.Snapping;
                e.Snapping = false;
                long duration = e.Sequence.Duration.Ticks;
                var begin = new SequenceTime(Math.Min(5 * SequenceTime.TicksPerSecond, duration - duration / 4));
                dragFrame = TimelineTime.FrameIndex(begin, rate, TimeRounding.Floor);
                window.PointerDown((viewport.X(TimelineTime.FromFrames(dragFrame, rate)), y));
                dragStarted = true;
                return;
            }
            if (dragAwaiting)
            {
                var requested = TimelineTime.FromFrames(dragFrame, rate);
                var shown = window.Playback?.Current?.Time;
                Require(shown == requested, $"Held backward drag requested {requested.Ticks} ticks and showed {shown?.Ticks}.");
                backwardDragMs.Add(latency.Elapsed.TotalMilliseconds);
            }
            if (backwardDragMs.Count == 12 || dragFrame == 0)
            {
                double x = viewport.X(TimelineTime.FromFrames(dragFrame, rate));
                window.Capture(Path.Combine(directory, "scrubbing.png"));
                window.PointerUp((x, y));
                e.Snapping = originalSnapping;
                dragFinished = true;
            }
            else
            {
                dragFrame--;
                latency.Restart();
                window.PointerMove((viewport.X(TimelineTime.FromFrames(dragFrame, rate)), y));
                dragAwaiting = true;
            }
            return;
        }
        if (!startedPlayback)
        {
            Require(window.Playback?.IsPlaying == false, "Pause did not cancel the pending playback start.");
            window.Action("Play"); startedPlayback = true; latency.Restart(); return;
        }
        if (!VerifyReverse(window)) return;
        window.Action("Pause");
        var playback = window.Playback ?? throw new InvalidOperationException("Transport did not reopen after scrubbing.");
        Require(playback.CanPlay, "Transport failed to reopen synchronized audio after scrubbing.");
        PlaybackQualification.Require(window.MaximumAvSkew, window.Graphics.ValidationErrorCount);
        Require(playback.UnderrunFrames == 0, "Audio underrun after scrubbing.");
        Require(reverseUnderruns == 0, "Audio underrun during reverse playback.");
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
            reversePlayback = true, reversePauseResume = true, forwardAfterReverse = true,
            reverseTransitionPictures,
            reversePictures = reversePictures.Count, reverseUnderruns, livePlayheadBeforeDecode = true, backwardDragMs,
            medianMs = (latencies.Order().ElementAt((latencies.Count - 1) / 2) + latencies.Order().ElementAt(latencies.Count / 2)) / 2,
            p95Ms = latencies.Order().ElementAt((int)(latencies.Count * .95)),
            seekRequests = window.SeekRequests, staleGenerationsRejected = window.StaleGenerationsRejected,
            maximumAvSkewSeconds = window.MaximumAvSkew, underrunFrames = window.Playback?.UnderrunFrames,
            validationAvailable = window.Graphics.ValidationAvailable, validationErrors = window.Graphics.ValidationErrorCount,
            revisionUnchanged = e.Session.Snapshot.Revision == revision,
            scope = "Native GPU/audio window and actual coordinate/capture handlers with scripted mouse input."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Finished = true;
    }

    private bool VerifyReverse(EditorWindow window)
    {
        var playback = window.Playback;
        var editor = window.Editor;
        switch (reversePhase)
        {
            case 0:
                if (playback?.IsPlaying == true && latency.Elapsed.TotalSeconds < 1) return false;
                Require(playback?.CanPlay == true && playback.UnderrunFrames == 0, "Initial forward playback failed.");
                window.Action("Pause");
                reverseTransition = editor.Sequence.Tracks.Where(track => track.Kind == TrackKind.Video && track.State.Visible)
                    .SelectMany(track => track.Transitions.Select(transition => new TransitionWindow(transition,
                        track.Clips.First(clip => clip.Id == transition.OutgoingClipId),
                        track.Clips.First(clip => clip.Id == transition.IncomingClipId))))
                    .FirstOrDefault(candidate => candidate.End.Ticks <= 7 * SequenceTime.TicksPerSecond
                        && candidate.Outgoing.Properties.Enabled && candidate.Incoming.Properties.Enabled);
                long through = reverseTransition is { } transitionWindow ? transitionWindow.End.Ticks + SequenceTime.TicksPerSecond : 0;
                reverseStart = Math.Min(editor.Sequence.Duration.Ticks, Math.Max(4 * SequenceTime.TicksPerSecond, through));
                window.Seek(new(reverseStart));
                reversePhase = 1;
                return false;
            case 1:
                window.Action("PlayReverse");
                reversePhase = 2;
                return false;
            case 2:
            case 4:
                Require(playback is { IsReverse: true, Failure: null }, "Reverse playback did not start.");
                if (playback!.Current is { } picture && (reversePictures.Count == 0 || picture.Time.Ticks != reversePictures[^1]))
                {
                    Require(reversePictures.Count == 0 || picture.Time.Ticks < reversePictures[^1], "Reverse video advanced.");
                    reversePictures.Add(picture.Time.Ticks);
                    if (reverseTransition is { } transition && picture.Time.Ticks >= transition.Start.Ticks
                        && picture.Time.Ticks < transition.End.Ticks && picture.SourceTimestamps is { Length: >= 2 }
                        && picture.VideoClips.Contains(transition.Outgoing.Id) && picture.VideoClips.Contains(transition.Incoming.Id))
                        reverseTransitionPictures++;
                }
                if (reversePhase == 2)
                {
                    if (editor.Playhead.Ticks > reverseStart / 2) return false;
                    window.Action("Pause");
                    reversePausedAt = playback.Position;
                    latency.Restart();
                    reversePhase = 3;
                    return false;
                }
                if (playback.IsPlaying) return false;
                Require(playback.Position.Ticks == 0 && reversePictures.Count >= 20, "Reverse playback did not run through to the start.");
                Require(reverseTransition is null || reverseTransitionPictures > 0, "No decoded blend reached native reverse playback.");
                reverseUnderruns = playback.UnderrunFrames;
                window.Action("Play");
                reversePhase = 5;
                return false;
            case 3:
                if (latency.ElapsedMilliseconds < 100) return false;
                Require(playback is { IsPlaying: false } && playback.Position == reversePausedAt, "Reverse pause did not hold the audio position.");
                window.Action("PlayReverse");
                reversePhase = 4;
                return false;
            case 5:
                Require(playback is { IsPlaying: true, IsReverse: false, Failure: null }, "Forward did not start after reverse.");
                return editor.Playhead.Ticks >= SequenceTime.TicksPerSecond / 4;
            default:
                throw new InvalidOperationException("Unknown reverse smoke phase.");
        }
    }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
