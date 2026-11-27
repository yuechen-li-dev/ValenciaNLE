using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Media;

public sealed record ExportProgress(long Frames, long TotalFrames);
public sealed record ExportResult(string Path, long Frames, long AudioSamples, SequenceTime Duration,
    string? Encoder = null, long? Bytes = null, double? ElapsedSeconds = null);

public static class SequenceExport
{
    /// <summary>Send the authoritative compositor and mix to the selected FFmpeg encoder through bounded pipes.</summary>
    public static async Task<ExportResult> RunAsync(ProjectSnapshot snapshot, Guid sequenceId, string path,
        IProgress<ExportProgress>? progress = null, CancellationToken cancellation = default, OutputProfile? profile = null,
        bool overwrite = true, (int Width, int Height)? encodeSize = null, (SequenceTime Start, SequenceTime End)? range = null,
        ExportEncoder? encoder = null, AudioFinish? finish = null, bool burnCaptions = true, int? quality = null, Guid? attempt = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("M1b export uses Windows named pipes.");
        DocumentValidator.Validate(snapshot.Document);
        var clock = Stopwatch.StartNew();
        string destination = Path.GetFullPath(path);
        encoder ??= ExportCatalog.ForDestination(destination);
        finish?.Validate();
        if (!Path.GetExtension(destination).Equals(encoder.Extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{encoder.Id} requires a {encoder.Extension} destination.");
        if (!overwrite && File.Exists(destination)) throw new IOException("Export destination exists. Choose a new file.");
        if (snapshot.Document.Assets.Any(a => string.Equals(a.SourcePath, destination, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Export cannot replace source media.");
        var sequence = new TimelineQueries(snapshot).Sequence(sequenceId);
        var rate = profile?.FrameRate ?? sequence.Video.FrameRate;
        int width = profile?.Width ?? sequence.Video.Width;
        int height = profile?.Height ?? sequence.Video.Height;
        var outputSize = encodeSize ?? (Width: width, Height: height);
        if (outputSize.Width > encoder.MaximumWidth || outputSize.Height > encoder.MaximumHeight ||
            (long)rate.Numerator > (long)encoder.MaximumFps * rate.Denominator)
            throw new ArgumentException("Output dimensions or rate exceed the Valencia encoder configuration.");
        var origin = range?.Start ?? profile?.Start ?? default;
        var end = new SequenceTime(Math.Min(range?.End.Ticks ?? profile?.End?.Ticks ?? sequence.Duration.Ticks, sequence.Duration.Ticks));
        if (end.Ticks <= origin.Ticks) throw new ArgumentException("Output profile has no media range.");
        long frames = TimelineTime.FrameIndex(new(end.Ticks - origin.Ticks), rate, TimeRounding.Ceiling);
        if (frames == 0) throw new ArgumentException("Cannot export an empty sequence.");
        var duration = TimelineTime.FromFrames(frames, rate);
        long samples = TimelineTime.SampleIndex(duration, sequence.AudioSampleRate, TimeRounding.Ceiling);
        long selectedSamples = TimelineTime.SampleIndex(new(end.Ticks - origin.Ticks), sequence.AudioSampleRate, TimeRounding.Ceiling);
        string[] finishingArguments = [];
        if (finish is not null)
        {
            // Loudnorm can flush lookahead past the input tail. Preserve the exact authored render window.
            long deliverySamples = TimelineTime.SampleIndex(duration, 48_000, TimeRounding.Ceiling);
            string count = deliverySamples.ToString(CultureInfo.InvariantCulture);
            finishingArguments = ["-af", $"{finish.FilterFor(encoder.AudioCodec)},aresample=48000,apad=whole_len={count},atrim=end_sample={count},asetpts=N/SR/TB"];
        }
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string temporary = PartialPath(destination, attempt ?? Guid.NewGuid());
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        string videoName = "valencia-v-" + Guid.NewGuid().ToString("N"), audioName = "valencia-a-" + Guid.NewGuid().ToString("N");
        await using var videoPipe = new NamedPipeServerStream(videoName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var audioPipe = new NamedPipeServerStream(audioName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var start = new ProcessStartInfo(encoder.Executable)
        {
            UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true
        };
        string frameRate = $"{rate.Numerator}/{rate.Denominator}";
        string[] args = ["-hide_banner", "-loglevel", "error", "-y", "-thread_queue_size", "8", "-f", "rawvideo", "-pixel_format", "rgba",
            "-video_size", $"{width}x{height}", "-framerate", frameRate, "-i", @"\\.\pipe\" + videoName,
            "-thread_queue_size", "8", "-f", "f32le", "-ar", sequence.AudioSampleRate.ToString(CultureInfo.InvariantCulture), "-ac", "2", "-i", @"\\.\pipe\" + audioName,
            "-map", "0:v:0", "-map", "1:a:0",
            .. (encodeSize is { } size ? new[] { "-vf", $"scale={size.Width}:{size.Height}:flags=bilinear,setsar=1" } : []),
            .. ExportCatalog.EncoderArguments(encoder, quality),
            .. finishingArguments,
            .. (finish is not null || encoder.AudioCodec == "opus" ? new[] { "-ar", "48000" } : []),
            .. (encoder.Container == "mp4" ? new[] { "-movflags", "+faststart" } : []),
            "-f", encoder.Container == "mkv" ? "matroska" : encoder.Container, temporary];
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg encoder process could not start.");
        MediaProcess.SetBackgroundPriority(process);
        using var registration = stopping.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        Task<string> errors = process.StandardError.ReadToEndAsync();
        Task video = Task.Run(async () =>
        {
            try
            {
                using var evaluator = new SequenceEvaluator(snapshot, sequenceId, profile, burnCaptions: burnCaptions);
                await videoPipe.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                for (long i = 0; i < frames; i++)
                {
                    var frame = evaluator.VideoForTransport(new(checked(origin.Ticks + TimelineTime.FromFrames(i, rate).Ticks)), stopping.Token);
                    await videoPipe.WriteAsync(frame.Rgba, stopping.Token).ConfigureAwait(false);
                    progress?.Report(new(i + 1, frames));
                }
                videoPipe.Disconnect();
            }
            catch { await stopping.CancelAsync(); throw; }
        });
        Task audio = Task.Run(async () =>
        {
            try
            {
                using var evaluator = new SequenceEvaluator(snapshot, sequenceId);
                await audioPipe.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                for (long i = 0; i < samples; i += AudioStreamBuffer.BlockFrames)
                {
                    int count = (int)Math.Min(AudioStreamBuffer.BlockFrames, samples - i);
                    float[] block = new float[count * 2];
                    int selected = (int)Math.Min(count, Math.Max(0, selectedSamples - i));
                    if (selected > 0) evaluator.Audio(origin, i, selected, stopping.Token).CopyTo(block, 0);
                    byte[] bytes = MemoryMarshal.AsBytes(block.AsSpan()).ToArray();
                    await audioPipe.WriteAsync(bytes, stopping.Token).ConfigureAwait(false);
                }
                audioPipe.Disconnect();
            }
            catch { await stopping.CancelAsync(); throw; }
        });
        Task exit = Task.Run(async () =>
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0) { await stopping.CancelAsync(); throw new InvalidOperationException($"{encoder.Id} export failed: " + await errors.ConfigureAwait(false)); }
        });
        try
        {
            await Task.WhenAll(video, audio, exit).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite);
            return new(destination, frames, samples, duration, encoder.Id, new FileInfo(destination).Length, clock.Elapsed.TotalSeconds);
        }
        catch
        {
            await stopping.CancelAsync();
            try { await Task.WhenAll(video, audio, exit).ConfigureAwait(false); } catch { }
            cancellation.ThrowIfCancellationRequested();
            // Preserve native encoder diagnostics if cancellation was caused by its failure.
            string detail = await errors.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(detail)) throw new InvalidOperationException($"{encoder.Id} encoder unavailable or failed. Inspect encoder capabilities or select another provider. " + detail);
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string PartialPath(string destination, Guid attempt) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!, $".valencia-export-{attempt:N}.partial");
}
