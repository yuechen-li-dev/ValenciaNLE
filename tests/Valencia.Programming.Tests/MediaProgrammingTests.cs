using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Valencia.Engine;
using Valencia.Persistence;
using Valencia.Core;
using Xunit;

namespace Valencia.Programming.Tests;

public sealed class MediaProgrammingTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Valencia-programmed-media", Guid.NewGuid().ToString("N"));
    private string Project => Path.Combine(root, "input.valencia");
    private string Source => Path.Combine(root, "source.mp4");
    private static string CliDll => typeof(Valencia.Cli.Program).Assembly.Location;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        await ProcessRun("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30:duration=4",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=4", "-vf",
            "drawtext=fontfile='C\\:/Windows/Fonts/consola.ttf':text='%{n}':x=4:y=4:fontsize=16:fontcolor=white:box=1:boxcolor=black",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", Source);
        await ProcessRun("dotnet", CliDll, "project", "create", Project, "--media", Source);
    }
    public Task DisposeAsync() { Directory.Delete(root, true); return Task.CompletedTask; }

    private static async Task<byte[]> ProcessRun(string exe, params string[] arguments)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        using var memory = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(memory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); await copy; }
        catch { process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await errors);
        return memory.ToArray();
    }
    [Fact]
    public async Task HeadlessCopelandOpenQueryPlanUndoSaveReopenAndExportIsIndependentlyVerified()
    {
        string repo = FindRepository();
        byte[] previous = await File.ReadAllBytesAsync(Project);
        byte[] output = await ProcessRun("dotnet", CliDll, "script", "run", Path.Combine(repo, "examples", "rough-cut.ts"), "--project", Project);
        using var result = JsonDocument.Parse(output);
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        using var preview = JsonDocument.Parse(result.RootElement.GetProperty("returnValue").GetString()!);
        Assert.True(preview.RootElement.GetProperty("valid").GetBoolean());
        string saved = Project + ".edited.valencia";
        var reopened = await new ProjectFile().LoadAsync(saved);
        Assert.Equal(5, reopened.Snapshot.Document.Sequences[0].Duration.TotalSeconds);
        Assert.Empty(reopened.MissingMedia);
        Assert.Equal(previous, await File.ReadAllBytesAsync(Project));
        string movie = saved + ".mp4";
        async Task<byte[]> Frame(string file, string time) => await ProcessRun("ffmpeg", "-v", "error", "-ss", time, "-i", file,
            "-frames:v", "1", "-vf", "scale=32:18:flags=area", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1");
        byte[] gap = await Frame(movie, "2.5");
        Assert.All(gap, b => Assert.InRange(b, (byte)0, (byte)2));
        var expected = await Frame(Source, "3");
        var actual = await Frame(movie, "4");
        Assert.Equal(expected.Length, actual.Length);
        Assert.InRange(actual.Zip(expected, (a, b) => Math.Abs(a - b)).Average(), 0, 8);
        byte[] audio = await ProcessRun("ffmpeg", "-v", "error", "-ss", "2.2", "-i", movie, "-t", "0.5", "-vn", "-f", "f32le", "-ac", "1", "pipe:1");
        Assert.InRange(MemoryMarshal.Cast<byte, float>(audio).ToArray().Average(s => Math.Abs(s)), 0, .001);
        byte[] probe = await ProcessRun("ffprobe", "-v", "error", "-show_entries", "format=duration:stream=codec_name", "-of", "json", movie);
        using var metadata = JsonDocument.Parse(probe);
        Assert.InRange(double.Parse(metadata.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture), 4.99, 5.04);
        Assert.Contains(metadata.RootElement.GetProperty("streams").EnumerateArray(), s => s.GetProperty("codec_name").GetString() == "h264");
        Assert.Contains(metadata.RootElement.GetProperty("streams").EnumerateArray(), s => s.GetProperty("codec_name").GetString() == "aac");
        // The standalone CLI export consumes exactly the same reopened snapshot and backend.
        string cliMovie = Path.Combine(root, "cli.mp4");
        await ProcessRun("dotnet", CliDll, "export", saved, "--sequence", reopened.Snapshot.Document.Sequences[0].Id.ToString(), "--output", cliMovie);
        Assert.Equal(await Frame(movie, "4"), await Frame(cliMovie, "4"));
    }
    [Fact]
    public async Task ActualMcpExportCancelsCleansUpThenExportsAValidatedEditedPlan()
    {
        var loaded = await new ProjectFile().LoadAsync(Project);
        var snapshot = loaded.Snapshot;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in new[] { CliDll, "mcp", "--root", root, "--project", "input.valencia" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            int nextId = 0;
            async Task<JsonElement> Rpc(string method, object parameters)
            {
                await process.StandardInput.WriteLineAsync(EngineJson.Write(new { jsonrpc = "2.0", id = ++nextId, method, @params = parameters }));
                await process.StandardInput.FlushAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                string? line = await process.StandardOutput.ReadLineAsync(deadline.Token);
                Assert.NotNull(line);
                return JsonDocument.Parse(line).RootElement.Clone();
            }
            JsonElement Data(JsonElement message) => message.GetProperty("result").GetProperty("structuredContent").GetProperty("result");
            await Rpc("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "job test", version = "1" } });
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var started = Data(await Rpc("tools/call", new { name = "run_export", arguments = new { projectId = snapshot.Document.Id, revision = EngineJson.Exact(snapshot.Revision), sequenceId = snapshot.Document.Sequences[0].Id, output = "cancelled.mp4" } }));
            Guid jobId = started.GetProperty("jobId").GetGuid();
            var status = Data(await Rpc("tools/call", new { name = "export_status", arguments = new { projectId = snapshot.Document.Id, jobId } }));
            Assert.Equal("running", status.GetProperty("state").GetString());
            await Rpc("tools/call", new { name = "cancel_export", arguments = new { projectId = snapshot.Document.Id, jobId } });
            for (int attempt = 0; attempt < 100; attempt++)
            {
                status = Data(await Rpc("tools/call", new { name = "export_status", arguments = new { projectId = snapshot.Document.Id, jobId } }));
                if (status.GetProperty("state").GetString() != "running") break;
                await Task.Delay(20);
            }
            Assert.Equal("cancelled", status.GetProperty("state").GetString());
            Assert.False(File.Exists(Path.Combine(root, "cancelled.mp4")));
            Assert.Empty(Directory.GetFiles(root, "*.partial"));

            var operations = new List<PlanOperation>();
            var sequence = snapshot.Document.Sequences[0];
            foreach (var track in sequence.Tracks)
            {
                Guid right = Guid.NewGuid();
                operations.Add(new SplitPlan(track.Clips[0].Id, EngineJson.Exact(2 * SequenceTime.TicksPerSecond), right));
                operations.Add(new MovePlan(right, track.Id, EngineJson.Exact(3 * SequenceTime.TicksPerSecond)));
            }
            var plan = new EditPlan(1, snapshot.Document.Id, EngineJson.Exact(snapshot.Revision), sequence.Id, [.. operations]);
            object planArguments = new { projectId = snapshot.Document.Id, revision = plan.ProjectRevision, plan };
            var preview = Data(await Rpc("tools/call", new { name = "validate_edit_plan", arguments = planArguments }));
            var commit = Data(await Rpc("tools/call", new { name = "apply_edit_plan", arguments = planArguments }));
            Assert.True(preview.GetProperty("valid").GetBoolean());
            Assert.True(commit.GetProperty("committed").GetBoolean());
            Assert.Equal(preview.GetProperty("changes").GetRawText(), commit.GetProperty("changes").GetRawText());
            var export = Data(await Rpc("tools/call", new
            {
                name = "run_export",
                arguments = new
                {
                    projectId = snapshot.Document.Id,
                    revision = commit.GetProperty("resultingRevision").GetString(),
                    sequenceId = sequence.Id,
                    output = "mcp-edited.mp4"
                }
            }));
            jobId = export.GetProperty("jobId").GetGuid();
            for (int attempt = 0; attempt < 1000; attempt++)
            {
                status = Data(await Rpc("tools/call", new { name = "export_status", arguments = new { projectId = snapshot.Document.Id, jobId } }));
                if (status.GetProperty("state").GetString() != "running") break;
                await Task.Delay(20);
            }
            Assert.Equal("completed", status.GetProperty("state").GetString());
            Assert.Equal("150", status.GetProperty("frames").GetString());
            string movie = Path.Combine(root, "mcp-edited.mp4");
            async Task<byte[]> Frame(string file, string time) => await ProcessRun("ffmpeg", "-v", "error", "-ss", time, "-i", file,
                "-frames:v", "1", "-vf", "scale=32:18:flags=area", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1");
            Assert.All(await Frame(movie, "2.5"), b => Assert.InRange(b, (byte)0, (byte)2));
            byte[] expected = await Frame(Source, "3");
            byte[] actual = await Frame(movie, "4");
            Assert.Equal(expected.Length, actual.Length);
            Assert.InRange(actual.Zip(expected, (a, b) => Math.Abs(a - b)).Average(), 0, 8);
            byte[] gapAudio = await ProcessRun("ffmpeg", "-v", "error", "-ss", "2.2", "-i", movie, "-t", "0.2", "-vn", "-f", "f32le", "-ac", "1", "pipe:1");
            Assert.All(MemoryMarshal.Cast<byte, float>(gapAudio).ToArray(), sample => Assert.InRange(Math.Abs(sample), 0, .001));
            using var probe = JsonDocument.Parse(await ProcessRun("ffprobe", "-v", "error", "-show_entries", "format=duration:stream=codec_name", "-of", "json", movie));
            var codecs = probe.RootElement.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_name").GetString()).ToArray();
            Assert.Contains("h264", codecs);
            Assert.Contains("aac", codecs);
            Assert.InRange(double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture), 4.99, 5.04);
            Assert.Equal(snapshot.Revision, (await new ProjectFile().LoadAsync(Project)).Snapshot.Revision);
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            _ = await errors;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync();
            }
        }
    }
    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Valencia.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Valencia repository not found.");
    }
}
