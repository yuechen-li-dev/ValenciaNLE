using System.Diagnostics;
using System.Text.Json;
using Valencia.Engine;
using Valencia.Persistence;
using Xunit;

namespace Valencia.Programming.Tests;

public sealed partial class TranscriptionTests
{
    [Fact]
    public async Task ActualMcpCreatorPlanningCommitRevisionAndProfileExport()
    {
        var snapshot = (await new ProjectFile().LoadAsync(project)).Snapshot;
        await using var engine = new EditingEngine(new(snapshot), new WorkspacePolicy(root), Path.GetFileName(project));
        var transcript = await Transcribe(engine, new(Alignment: "dtw"));
        var audio = await engine.AnalyzeAsync(new(transcript.AssetId, transcript.StreamIndex, "audio"));
        var request = ThreeParts(engine, transcript, audio);
        var expected = await engine.CreateCreatorAsync(request);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { typeof(Valencia.Cli.Program).Assembly.Location, "mcp", "--root", root, "--project", Path.GetFileName(project) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        int id = 0;
        async Task<JsonElement> Rpc(string method, object parameters)
        {
            await process.StandardInput.WriteLineAsync(EngineJson.Write(new { jsonrpc = "2.0", id = ++id, method, @params = parameters }));
            await process.StandardInput.FlushAsync();
            string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(line);
            return JsonDocument.Parse(line).RootElement.Clone();
        }
        Task<JsonElement> Tool(string name, object arguments) => Rpc("tools/call", new { name, arguments });
        JsonElement Data(JsonElement m) => m.GetProperty("result").GetProperty("structuredContent").GetProperty("result");
        Guid projectId = snapshot.Document.Id;
        string revision = EngineJson.Exact(snapshot.Revision);
        try
        {
            await Rpc("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "M2d protocol test", version = "1" } });
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            Assert.Equal(35, (await Rpc("tools/list", new { })).GetProperty("result").GetProperty("tools").GetArrayLength());
            var previewPlan = new EditPlan(1, projectId, revision, request.SequenceId,
                [new TitlePlan(Guid.NewGuid(), "PROPOSED", 0, Valencia.Core.SequenceTime.TicksPerSecond)]);
            var review = Data(await Tool("review_timeline", new
            {
                projectId, revision, sequenceId = request.SequenceId, plan = previewPlan,
                selection = new { kind = "frame", atTicks = new[] { "0" } }, output = "mcp-review.png"
            }));
            Assert.Equal("plan", review.GetProperty("candidate").GetString());
            Assert.Equal(revision, review.GetProperty("currentRevision").GetString());
            Assert.True(review.GetProperty("baseStillCurrent").GetBoolean());
            Assert.True(File.Exists(Path.Combine(root, "mcp-review.png")));
            var submitted = Data(await Tool("start_creator", new { projectId, revision, request }));
            Guid jobId = submitted.GetProperty("jobId").GetGuid();
            JsonElement status;
            do
            {
                status = Data(await Tool("creator_status", new { projectId, jobId }));
                await Task.Delay(10);
            } while (status.GetProperty("status").GetProperty("state").GetString() == "running");
            Assert.Equal("completed", status.GetProperty("status").GetProperty("state").GetString());
            string key = status.GetProperty("status").GetProperty("planKey").GetString()!;
            Assert.Equal(expected.Key, key);
            Assert.True(status.GetProperty("proposal").GetProperty("valid").GetBoolean());
            Assert.True(status.GetRawText().Length < 20000);
            var committed = Data(await Tool("apply_creator", new { projectId, revision, key }));
            Assert.True(committed.GetProperty("committed").GetBoolean());
            var compact = Data(await Tool("project_info", new { projectId, compact = true }));
            Assert.True(compact.GetProperty("compact").GetBoolean());
            Assert.True(compact.GetRawText().Length < 20000);
            var stale = await Tool("apply_creator", new { projectId, revision, key });
            Assert.False(Data(stale).GetProperty("valid").GetBoolean());
            Guid sequenceId = request.SequenceId, profileId = expected.Outputs[1].Id;
            Guid export = Data(await Tool("run_export", new { projectId, revision = committed.GetProperty("resultingRevision").GetString(), sequenceId, profileId, output = "mcp-short.mp4" })).GetProperty("jobId").GetGuid();
            JsonElement exported;
            do
            {
                exported = Data(await Tool("export_status", new { projectId, jobId = export }));
                await Task.Delay(10);
            } while (exported.GetProperty("state").GetString() is "running" or "queued");
            Assert.Equal("completed", exported.GetProperty("state").GetString());
            using var probe = JsonDocument.Parse(await AnalysisFixture.Run("ffprobe", "-v", "error", "-show_entries", "stream=width,height,codec_name", "-of", "json", Path.Combine(root, "mcp-short.mp4")));
            Assert.Contains(probe.RootElement.GetProperty("streams").EnumerateArray(), s => s.GetProperty("codec_name").GetString() == "h264" && s.GetProperty("height").GetInt32() == 320);
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("REQUEST_FAILED", await stderr);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}
