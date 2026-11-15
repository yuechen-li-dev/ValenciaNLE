using System.Diagnostics;
using System.Text.Json;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Media;

namespace Valencia.App.Editing;

/// <summary>Bounded native qualification over actual editor/engine calls; screenshots are not physical-input tests.</summary>
internal sealed class QualificationSmoke(string directory) : IDisposable
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly Random random = new(20261115);
    private Task? operation;
    private int phase, seeks, playbackFrameIndex;
    private long uploadedAtStart;
    private long playbackUnderrunFrames;
    private double playingSince, recordingSince, playbackSeconds, previewFps;
    private SequenceTime captionTime, lastBurst;
    private TimelineClip? guide;
    private bool playbackCaptured, exportCaptured;
    private double[] playbackFrameTimes = [];
    private readonly CancellationTokenSource backgroundStopping = new();
    private Task<AnalysisStatus>? proxyJob;
    public bool Finished { get; private set; }

    public void Tick(EditorWindow window)
    {
        if (elapsed.Elapsed.TotalSeconds > 180) throw new TimeoutException("Native qualification timed out in phase " + phase);
        if (window.Playback?.Failure is { } failure) throw new InvalidOperationException("Native preview failed", failure);
        if (phase == 2)
        {
            if (!playbackCaptured && elapsed.Elapsed.TotalSeconds - playingSince > 2)
            {
                window.Capture(Path.Combine(directory, "playback.png")); playbackCaptured = true;
            }
            if (elapsed.Elapsed.TotalSeconds - playingSince < 10) return;
            playbackSeconds = elapsed.Elapsed.TotalSeconds - playingSince;
            previewFps = (window.Graphics.UploadedVideoFrames - uploadedAtStart) / playbackSeconds;
            playbackFrameTimes = window.FrameMilliseconds.Skip(playbackFrameIndex).ToArray();
            playbackUnderrunFrames = window.Playback!.UnderrunFrames;
            window.Playback!.Pause(); phase = 3;
        }
        if (phase == 17 && operation is { IsCompleted: false } && !exportCaptured)
        {
            window.Capture(Path.Combine(directory, "export.png")); exportCaptured = true;
        }
        if (operation is { IsCompleted: true }) { operation.GetAwaiter().GetResult(); operation = null; }
        if (operation is not null || window.Busy) return;
        var editor = window.Editor;
        Directory.CreateDirectory(directory);
        switch (phase)
        {
            case 0:
                var snapshot = editor.Session.Snapshot;
                string path = editor.ProjectPath ?? Path.Combine(directory, "source.valencia");
                var asset = snapshot.Document.Assets[0];
                window.Playback?.Pause(); window.Graphics.ClearVideo();
                editor.New(); window.Capture(Path.Combine(directory, "empty.png"));
                editor.Commit(new AddAsset(asset)); editor.SelectedAsset = asset.Id;
                window.Capture(Path.Combine(directory, "imported-media.png"));
                editor.Open(snapshot, path);
                guide = editor.Sequence.Tracks.First(t => t.Kind == TrackKind.Video).Clips[0];
                window.Seek(guide.Start); phase++; break;
            case 1:
                window.Capture(Path.Combine(directory, "timeline.png"));
                window.Seek(guide!.Start, true); phase = 20; break;
            case 20:
                playingSince = elapsed.Elapsed.TotalSeconds;
                uploadedAtStart = window.Graphics.UploadedVideoFrames;
                playbackFrameIndex = window.FrameMilliseconds.Count;
                var captured = editor.Session.Snapshot;
                proxyJob = Task.Run(async () =>
                {
                    var asset = captured.Document.Assets[0];
                    await using var engine = EditingEngine.Create(Guid.NewGuid(), "Concurrent qualification analysis");
                    asset = asset with { Id = Guid.NewGuid() };
                    var registered = engine.Execute(new EditCommand(Guid.NewGuid(), 1, new AddAsset(asset), 0));
                    if (!registered.Success) throw new InvalidOperationException(registered.Message);
                    Guid job = engine.Analysis.Start(new(asset.Id, asset.Streams.First(s => s.Kind == TrackKind.Video).Index, "proxy"));
                    return await engine.Analysis.WaitAsync(job, backgroundStopping.Token).ConfigureAwait(false);
                });
                phase = 2; break;
            case 3:
                if (seeks++ < 24)
                {
                    var position = new SequenceTime(guide!.Start.Ticks + (long)((decimal)(guide.Duration.Ticks - SequenceTime.TicksPerSecond / 10) * random.Next(100000) / 100000));
                    window.Seek(position);
                    break;
                }
                for (int i = 0; i < 100; i++)
                {
                    lastBurst = new(guide!.Start.Ticks + (long)((decimal)(guide.Duration.Ticks - SequenceTime.TicksPerSecond / 10) * i / 100));
                    window.Seek(lastBurst);
                }
                phase = 4; break;
            case 4:
                if (window.Playback?.Current?.Time.Ticks != lastBurst.Ticks) throw new InvalidOperationException("Obsolete seek frame replaced the latest request.");
                editor.SelectedAsset = editor.Session.Snapshot.Document.Assets[0].Id;
                operation = window.TranscribeSelected(); phase = 5; break;
            case 5:
                window.Capture(Path.Combine(directory, "transcript.png"));
                operation = AddCaptions(window); phase = 6; break;
            case 6:
                window.Seek(captionTime); phase = 7; break;
            case 7:
                window.Capture(Path.Combine(directory, "captioned.png"));
                window.Action("OutputProfile"); window.Action("OutputProfile"); window.Seek(captionTime); phase = 8; break;
            case 8:
                window.Capture(Path.Combine(directory, "vertical.png"));
                editor.SelectedProfile = null;
                editor.TranscriptMode = false;
                operation = window.AnalyzeSelected(); phase = 9; break;
            case 9:
                operation = window.ProposeRoughCut(); phase = 10; break;
            case 10:
                window.Capture(Path.Combine(directory, "analysis-preview.png"));
                if (Environment.GetEnvironmentVariable("VALENCIA_OBS_ENDPOINT") is null) { phase = 16; break; }
                editor.ObsMode = true;
                operation = window.ConnectObs(); phase = 11; break;
            case 11:
                window.Capture(Path.Combine(directory, "obs-connected.png"));
                operation = window.ObsAction("start"); phase = 12; break;
            case 12:
                recordingSince = elapsed.Elapsed.TotalSeconds; phase = 13; break;
            case 13:
                if (elapsed.Elapsed.TotalSeconds - recordingSince < 3) return;
                window.Capture(Path.Combine(directory, "obs-recording.png"));
                operation = MarkAndStop(window); phase = 14; break;
            case 14:
                operation = window.ObsAction("import"); phase = 15; break;
            case 15:
                window.Capture(Path.Combine(directory, "obs-ingested.png"));
                editor.ObsMode = false; phase = 16; break;
            case 16:
                operation = window.ExportQualification(Path.Combine(directory, "native-export.mp4")); phase = 17; break;
            case 17:
                File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
                {
                    outcome = PlaybackQualification.TimingPasses(window.MaximumAvSkew) && window.Graphics.ValidationErrorCount == 0 ? "passed" : "failed",
                    gpu = window.Graphics.GpuName, window = new { width = window.Graphics.Window.Size.X, height = window.Graphics.Window.Size.Y },
                    sourceVideo = editor.Sequence.Video, playbackSeconds, previewFps, playbackFrameMilliseconds = playbackFrameTimes,
                    presentationSkewSeconds = window.MaximumAvSkew, window.SeekRequests, window.StaleGenerationsRejected,
                    seekLatenciesMs = window.SeekLatenciesMs, playbackUnderrunFrames, finalUnderrunFrames = window.Playback?.UnderrunFrames,
                    latestSeekVerified = true, physicalPointerActionsTested = false, obs = window.Obs?.Recording.Snapshot.State,
                    elapsedSeconds = elapsed.Elapsed.TotalSeconds, sampleScope = "24 sequential seeks plus a 100-request coalesced burst; p99 latency unqualified",
                    backgroundProxy = proxyJob?.GetAwaiter().GetResult()
                }, new JsonSerializerOptions { WriteIndented = true }));
                PlaybackQualification.Require(window.MaximumAvSkew, window.Graphics.ValidationErrorCount);
                Finished = true; break;
        }
    }
    private async Task AddCaptions(EditorWindow window)
    {
        var editor = window.Editor;
        await using var engine = new EditingEngine(editor.Session);
        var transcript = editor.Transcript ?? throw new InvalidOperationException("Transcript missing.");
        var proposal = await engine.CreateCaptionsAsync(editor.SequenceId, transcript.Key);
        if (!engine.ApplyCreator(proposal.Key).Committed) throw new InvalidOperationException("Caption plan failed.");
        var settings = editor.Sequence.Creator! with
        {
            Profiles = [new(Guid.NewGuid(), "long-form", editor.Sequence.Video.Width, editor.Sequence.Video.Height, editor.Sequence.Video.FrameRate),
                new(Guid.NewGuid(), "vertical", 1080, 1920, editor.Sequence.Video.FrameRate, "fill", CaptionY: .78)]
        };
        var result = engine.Execute(new EditCommand(Guid.NewGuid(), 1, new SetCreatorSettings(editor.SequenceId, settings), engine.Session.Snapshot.Revision));
        if (!result.Success) throw new InvalidOperationException(result.Message);
        var word = settings.Words.First(w => w.Start.CompareTo(guide!.SourceIn) >= 0 && w.End.CompareTo(guide.SourceOut) < 0);
        captionTime = new(guide!.MapToTimeline(word.Start).Ticks + SequenceTime.TicksPerSecond / 60);
    }
    private static async Task MarkAndStop(EditorWindow window)
    {
        await window.ObsAction("marker");
        await window.ObsAction("stop");
    }
    public void Dispose()
    {
        backgroundStopping.Cancel();
        if (proxyJob is not null) try { proxyJob.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        backgroundStopping.Dispose();
    }
}
