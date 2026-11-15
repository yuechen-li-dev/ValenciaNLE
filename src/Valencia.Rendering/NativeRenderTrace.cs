using System.Diagnostics;
using System.Text.Json;
using Aurelian.Graphics.Vulkan.Native2D;

namespace Valencia.Rendering;

/// <summary>Opt-in, window-thread diagnostics. Retains at most 4096 frames, never decoded media.</summary>
internal sealed class NativeRenderTrace(string path)
{
    private readonly List<object> frames = [];
    private readonly Dictionary<string, double> cpu = [];
    private long previousFrame;
    private int observedFrames;
    private readonly List<object> presentationClocks = [];
    public void ObservePresentation(ulong frameId, double frameSeconds, double clockSeconds)
    {
        if (presentationClocks.Count < 4096)
            presentationClocks.Add(new { frameId, observedHostTicks = Stopwatch.GetTimestamp(), frameSeconds, clockSeconds });
    }
    public object? Upload { get; set; }
    public object? MediaFrame { get; set; }
    public void Phase(string name, double milliseconds) => cpu[name] = milliseconds;
    public void Frame(ulong id, long started, Native2DPassResult[] passes, NativePresentTimings? presentation,
        int atlasUploads, long uploadedFrames)
    {
        observedFrames++;
        if (frames.Count < 4096)
        {
            long finished = Stopwatch.GetTimestamp();
            frames.Add(new { id, started, finished, monotonicFrequency = Stopwatch.Frequency,
                drawMs = Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds,
                cadenceMs = previousFrame == 0 ? 0 : Stopwatch.GetElapsedTime(previousFrame, started).TotalMilliseconds,
                cpu = new Dictionary<string, double>(cpu), mediaFrame = MediaFrame, upload = Upload, passes, presentation, atlasUploads, uploadedFrames,
                allocatedBytes = GC.GetTotalAllocatedBytes(false),
                gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
                totalGcPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds,
                workingSetBytes = Environment.WorkingSet });
        }
        previousFrame = started;
        cpu.Clear();
        Upload = null;
    }
    public void Save(object device, object validation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { device, validation, observedFrames, droppedTraceFrames = observedFrames - frames.Count, frames, presentationClocks }));
    }
}
