using System.Diagnostics;
using System.Text.Json;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.App.Editing;

internal sealed class CreatorSmoke(string directory)
{
    private readonly Stopwatch timer = Stopwatch.StartNew();
    private int phase;
    private double playingSince;
    private SequenceTime captionTime;
    public bool Finished { get; private set; }
    public void Tick(EditorWindow window)
    {
        if (timer.Elapsed.TotalSeconds > 60) throw new TimeoutException("Creator native smoke timed out at phase " + phase);
        if (phase == 4)
        {
            if (timer.Elapsed.TotalSeconds - playingSince < 5) return;
            window.Playback!.Pause(); phase = 5;
        }
        if (window.Busy) return;
        var editor = window.Editor;
        var creator = editor.Sequence.Creator ?? throw new InvalidOperationException("Open a generated creator project for this smoke.");
        Directory.CreateDirectory(directory);
        switch (phase)
        {
            case 0:
                var clip = editor.Sequence.Tracks.Where(t => t.Kind == TrackKind.Video).SelectMany(t => t.Clips).First();
                var word = creator.Words.First(w => w.AssetId == clip.AssetId && w.Start.CompareTo(clip.SourceIn) >= 0 && w.End.CompareTo(clip.SourceOut) < 0);
                captionTime = new(checked(clip.MapToTimeline(word.Start).Ticks + SequenceTime.TicksPerSecond / 30));
                window.Action("OutputProfile"); window.Seek(captionTime); phase++; break;
            case 1:
                if (window.Playback?.Current is not { Width: 320, Height: 180 }) throw new InvalidOperationException("Long-form monitor dimensions differ.");
                window.Capture(Path.Combine(directory, "long-form.png"));
                window.Action("OutputProfile"); window.Seek(captionTime); phase++; break;
            case 2:
                if (window.Playback?.Current is not { Width: 180, Height: 320 }) throw new InvalidOperationException("Vertical monitor dimensions differ.");
                window.Capture(Path.Combine(directory, "vertical.png"));
                var cut = editor.Sequence.Tracks.First(t => t.Kind == TrackKind.Video).Clips[1].Start;
                window.Seek(new(Math.Max(0, cut.Ticks - SequenceTime.TicksPerSecond)), true); phase++; break;
            case 3:
                playingSince = timer.Elapsed.TotalSeconds; phase++; break;
            case 5:
                window.Capture(Path.Combine(directory, "playing-across-cut.png"));
                File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
                {
                    outcome = PlaybackQualification.TimingPasses(window.MaximumAvSkew) && window.Graphics.ValidationErrorCount == 0 ? "passed" : "failed",
                    gpu = window.Graphics.GpuName, elapsedSeconds = timer.Elapsed.TotalSeconds,
                    actualPlaybackSeconds = timer.Elapsed.TotalSeconds - playingSince,
                    maximumObservedPresentationSkewSeconds = window.MaximumAvSkew,
                    underrunFrames = window.Playback!.UnderrunFrames, seekLatenciesMs = window.SeekLatenciesMs,
                    captionPreview = true, verticalPreview = true,
                    scope = "Automated native editor, Vulkan GPU presentation/readback and NAudio device playback across one cut; not human usability approval"
                }, new JsonSerializerOptions { WriteIndented = true }));
                PlaybackQualification.Require(window.MaximumAvSkew, window.Graphics.ValidationErrorCount);
                Finished = true; break;
        }
    }
}
