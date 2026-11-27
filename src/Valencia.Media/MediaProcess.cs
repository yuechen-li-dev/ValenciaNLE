using System.Diagnostics;
using System.ComponentModel;
using System.Text;

namespace Valencia.Media;

/// <summary>FFmpeg worker process with bounded diagnostic retention and cooperative process-tree cancellation.</summary>
public static class MediaProcess
{
    public static async Task<string> WithInputAsync(IEnumerable<string> arguments, Func<Stream, Task> feed, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(FfmpegRuntime.Executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg could not start.");
        using var registration = cancellation.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var error = ReadTail(process.StandardError); var output = ReadTail(process.StandardOutput);
        try
        {
            await feed(process.StandardInput.BaseStream).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            string text = await error.ConfigureAwait(false); await output.ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException("FFmpeg failed: " + text);
            return text;
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await error.ConfigureAwait(false); await output.ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            throw;
        }
    }
    public static async Task<string> RunAsync(IEnumerable<string> arguments, CancellationToken cancellation)
        => (await CaptureAsync(arguments, cancellation).ConfigureAwait(false)).Error;

    internal static async Task RunBackgroundAsync(IEnumerable<string> arguments, CancellationToken cancellation)
        => _ = await CaptureAsync(FfmpegRuntime.Executable, arguments, cancellation, background: true).ConfigureAwait(false);

    public static Task<(string Output, string Error)> CaptureAsync(IEnumerable<string> arguments, CancellationToken cancellation)
        => CaptureAsync(FfmpegRuntime.Executable, arguments, cancellation);

    public static async Task<(string Output, string Error)> CaptureAsync(string executable, IEnumerable<string> arguments,
        CancellationToken cancellation, bool background = false)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg could not start.");
        if (background) SetBackgroundPriority(process);
        using var registration = cancellation.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var error = ReadTail(process.StandardError);
        var output = ReadTail(process.StandardOutput);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        string text = await error.ConfigureAwait(false);
        string stdout = await output.ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException("FFmpeg failed: " + text);
        return (stdout, text);
    }

    internal static void SetBackgroundPriority(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
    }
    private static async Task<string> ReadTail(StreamReader reader)
    {
        var text = new StringBuilder();
        char[] block = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(block).ConfigureAwait(false)) > 0)
        {
            text.Append(block, 0, count);
            if (text.Length > 65536) text.Remove(0, text.Length - 65536);
        }
        return text.ToString();
    }
}
