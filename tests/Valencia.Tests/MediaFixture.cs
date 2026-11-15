using System.Diagnostics;
using Xunit;

namespace Valencia.Tests;

public sealed class MediaFixture : IAsyncLifetime
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "Valencia.Tests", Guid.NewGuid().ToString("N"));
    public string Mp4 => Path.Combine(DirectoryPath, "av.mp4");
    public string Mkv => Path.Combine(DirectoryPath, "av.mkv");
    public string Silent => Path.Combine(DirectoryPath, "silent.mp4");
    public string Offset => Path.Combine(DirectoryPath, "offset.mkv");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(DirectoryPath);
        await RunAsync("-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30000/1001:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=2", "-c:v", "libx264", "-pix_fmt", "yuv420p",
            "-g", "30", "-bf", "2", "-c:a", "aac", Mp4);
        await RunAsync("-i", Mp4, "-c", "copy", Mkv);
        await RunAsync("-i", Mp4, "-an", "-c:v", "copy", Silent);
        await RunAsync("-i", Mp4, "-c", "copy", "-output_ts_offset", "12", Offset);
    }

    private static async Task RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg could not start.");
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        Assert.True(process.ExitCode == 0, await errors);
    }

    public Task DisposeAsync()
    {
        Directory.Delete(DirectoryPath, recursive: true);
        return Task.CompletedTask;
    }
}
