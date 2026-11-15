using System.Diagnostics;
using System.Text.Json;
using Valencia.Engine;
using Valencia.Persistence;
using Xunit;

namespace Valencia.Programming.Tests;

public sealed partial class MediaAnalysisTests
{
    [Fact]
    public async Task ActualMcpAnalyzesPagesPlansCommitsExportsAndRejectsConflicts()
    {
        var snapshot = (await new ProjectFile().LoadAsync(project)).Snapshot;
        Guid projectId = snapshot.Document.Id, asset = snapshot.Document.Assets[0].Id, sequenceId = snapshot.Document.Sequences[0].Id;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { typeof(Valencia.Cli.Program).Assembly.Location, "mcp", "--root", root, "--project", "input.valencia" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        int id = 0;
        async Task<JsonElement> Rpc(string method, object parameters)
        {
            await process.StandardInput.WriteLineAsync(EngineJson.Write(new { jsonrpc = "2.0", id = ++id, method, @params = parameters }));
            await process.StandardInput.FlushAsync();
            string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.NotNull(line);
            return JsonDocument.Parse(line).RootElement.Clone();
        }
        Task<JsonElement> Tool(string name, object arguments) => Rpc("tools/call", new { name, arguments });
        JsonElement Data(JsonElement response) => response.GetProperty("result").GetProperty("structuredContent").GetProperty("result");
        try
        {
            await Rpc("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "analysis test", version = "1" } });
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var discovery = await Rpc("tools/list", new { });
            Assert.Contains("start_analysis", discovery.GetRawText());
            var started = Data(await Tool("start_analysis", new { projectId, request = new AnalysisRequest(asset, 1, "audio") }));
            Guid jobId = started.GetProperty("jobId").GetGuid();
            JsonElement state;
            do
            {
                state = Data(await Tool("analysis_status", new { projectId, jobId }));
                if (state.GetProperty("state").GetString() != "running") break;
                await Task.Delay(10);
            } while (true);
            Assert.Equal("completed", state.GetProperty("state").GetString());
            string key = state.GetProperty("key").GetString()!;
            var page = Data(await Tool("analysis_result", new { projectId, key, kind = "silence", limit = 1 }));
            Assert.Single(page.GetProperty("records").EnumerateArray());
            var rejected = await Tool("analysis_result", new { projectId, key, kind = "waveform", limit = 10000 });
            Assert.True(rejected.GetProperty("result").GetProperty("isError").GetBoolean());
            string revision = EngineJson.Exact(snapshot.Revision);
            var proposal = Data(await Tool("create_roughcut", new { projectId, revision, sequenceId, key }));
            Assert.True(proposal.GetProperty("valid").GetBoolean());
            var plan = proposal.GetProperty("plan");
            var preview = Data(await Tool("validate_edit_plan", new { projectId, revision, plan }));
            var committed = Data(await Tool("apply_edit_plan", new { projectId, revision, plan }));
            Assert.Equal(preview.GetProperty("changes").GetRawText(), committed.GetProperty("changes").GetRawText());
            var stale = await Tool("create_roughcut", new { projectId, revision, sequenceId, key });
            Assert.True(stale.GetProperty("result").GetProperty("isError").GetBoolean());
            var exported = Data(await Tool("run_export", new { projectId, revision = committed.GetProperty("resultingRevision").GetString(), sequenceId, output = "mcp-cut.mp4" }));
            jobId = exported.GetProperty("jobId").GetGuid();
            do
            {
                state = Data(await Tool("export_status", new { projectId, jobId }));
                if (state.GetProperty("state").GetString() != "running") break;
                await Task.Delay(20);
            } while (true);
            Assert.Equal("completed", state.GetProperty("state").GetString());
            await VerifyExport(Path.Combine(root, "mcp-cut.mp4"), 4.2, "4.3");
            Assert.Equal(snapshot.Revision, (await new ProjectFile().LoadAsync(project)).Snapshot.Revision);
            var stopping = Data(await Tool("start_analysis", new { projectId, request = new AnalysisRequest(asset, 1, "audio", new(WindowMs: 1)) }));
            jobId = stopping.GetProperty("jobId").GetGuid();
            await Tool("cancel_analysis", new { projectId, jobId });
            do
            {
                state = Data(await Tool("analysis_status", new { projectId, jobId }));
                if (state.GetProperty("state").GetString() != "running") break;
                await Task.Delay(10);
            } while (true);
            Assert.Equal("cancelled", state.GetProperty("state").GetString());
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            string stderr = await errors;
            // Native FFmpeg may report Matroska seek-index diagnostics on stderr.
            // Protocol output and independently decoded media remain the acceptance surfaces.
            Assert.DoesNotContain("REQUEST_FAILED", stderr);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        }
    }
}
