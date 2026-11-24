using System.Runtime.InteropServices;
using System.Text.Json;
using SkiaSharp;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Media;
using Valencia.Persistence;
using Valencia.Rendering;

namespace Valencia.App;

/// <summary>Compares actual sequence samples on both backends, then presents the Vulkan result.</summary>
internal static class EffectSmoke
{
    public static void Run(string path, string directory)
    {
        Directory.CreateDirectory(directory);
        var snapshot = new ProjectFile().LoadAsync(path).GetAwaiter().GetResult().Snapshot;
        var sequence = snapshot.Document.Sequences[0];
        var profile = sequence.Creator?.Profiles.FirstOrDefault();
        var clips = sequence.Tracks.Where(t => t.Kind == TrackKind.Video).SelectMany(t => t.Clips)
            .Where(c => c.Properties.Video is { } v && (v.Region is not null || v.Exposure != 0 || v.Contrast != 1 || v.Saturation != 1 || v.Temperature != 0))
            .OrderBy(c => c.Start.Ticks).Take(4).ToArray();
        if (clips.Length == 0) throw new ArgumentException("Effect smoke requires a sequence with affected video clips.");
        using var cpu = new SequenceEvaluator(snapshot, sequence.Id, profile, useVulkanEffects: false);
        using var gpu = new SequenceEvaluator(snapshot, sequence.Id, profile, useVulkanEffects: true);
        using var graphics = new NativeVideoPresenter { VideoBehindUi = true };
        graphics.ProgramMonitor = new(0, 0, NativeVideoPresenter.LogicalWidth, NativeVideoPresenter.LogicalHeight);
        var comparisons = new List<object>();
        bool passed = true;
        for (int clip = 0; clip < clips.Length; clip++)
        {
            long start = clips[clip].Start.Ticks + clips[clip].Duration.Ticks / 3;
            int max = 0;
            long sum = 0, samples = 0;
            var softwareMs = new List<double>();
            var vulkanMs = new List<double>();
            for (int i = 0; i < 12; i++)
            {
                long step = TimelineTime.FromFrames(i, profile?.FrameRate ?? sequence.Video.FrameRate).Ticks;
                var time = new SequenceTime(Math.Min(start + step, clips[clip].End.Ticks - 1));
                graphics.Window.DoEvents();
                var expected = cpu.Video(time, default);
                var actual = gpu.Video(time, default);
                softwareMs.Add(expected.EvaluationMilliseconds);
                vulkanMs.Add(actual.EvaluationMilliseconds);
                for (int channel = 0; channel < actual.Rgba.Length; channel++)
                {
                    int delta = Math.Abs(expected.Rgba[channel] - actual.Rgba[channel]);
                    max = Math.Max(max, delta);
                    sum += delta;
                }
                samples += actual.Rgba.Length;
                if (i != 0) continue;
                Save(actual.Rgba, actual.Width, actual.Height, Path.Combine(directory, $"sample-{clip + 1}.png"));
                graphics.UpdateVideo(actual.Width, actual.Height, actual.Rgba);
                byte[] presented = graphics.Draw(true) ?? throw new InvalidOperationException("Effect preview readback failed.");
                Save(presented, graphics.Window.FramebufferSize.X, graphics.Window.FramebufferSize.Y,
                    Path.Combine(directory, $"monitor-{clip + 1}.png"));
            }
            passed &= max <= 1;
            comparisons.Add(new { mode = clips[clip].Properties.Video!.Region?.Mode ?? "color", sampledAtSeconds = new SequenceTime(start).TotalSeconds,
                maximumChannelDelta = max, meanChannelDelta = sum / (double)samples, frames = vulkanMs.Count,
                firstSoftwareMs = softwareMs[0], firstVulkanMs = vulkanMs[0],
                softwareMedianMs = softwareMs.Order().Skip(5).Take(2).Average(), vulkanMedianMs = vulkanMs.Order().Skip(5).Take(2).Average() });
        }
        passed &= gpu.EffectGpu is not null && gpu.EffectValidationErrors == 0 && graphics.ValidationErrorCount == 0;
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            outcome = passed ? "passed" : "failed", gpu = gpu.EffectGpu, gpu.EffectValidationAvailable, gpu.EffectValidationErrors,
            graphics.ValidationAvailable, graphics.ValidationErrorCount, comparisons
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (!passed) throw new InvalidOperationException("Native Clip Effects qualification failed. See result.json.");
    }

    private static void Save(byte[] rgba, int width, int height, string output)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(output);
        data.SaveTo(stream);
    }
}
