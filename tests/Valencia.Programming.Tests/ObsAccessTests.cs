using System.Diagnostics;
using System.Text.Json;
using Valencia.Core.Editing;
using Valencia.Engine;
using Valencia.Obs;
using Valencia.Scripting;
using Xunit;

namespace Valencia.Programming.Tests;

[Collection("Whisper")]
public sealed class ObsAccessTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Valencia-obs-access", Guid.NewGuid().ToString("N"));
    private string project = "", media = "";
    private Guid projectId;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(root, "recordings"));
        media = Path.Combine(root, "recordings", "completed.mp4");
        await AnalysisFixture.Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10:duration=1", "-c:v", "libx264", media);
        await using var engine = EditingEngine.Create(Guid.NewGuid(), "OBS protocol access");
        projectId = engine.Session.Snapshot.Document.Id;
        Assert.True(engine.Execute(new EditCommand(Guid.NewGuid(), 1, new CreateSequence(Guid.NewGuid(), "Main", new(64, 64, new(10, 1)), 48000), 0)).Success);
        project = Path.Combine(root, "input.valencia");
        await engine.SaveAsync(project);
    }
    public Task DisposeAsync() { Directory.Delete(root, true); return Task.CompletedTask; }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualMcpObsProtocolHonorsLocalControlAuthorizationAndImports(bool authorize)
    {
        await using var peer = new ObsProtocolFixture { Output = media, StopDrainMs = 200 };
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { typeof(Valencia.Cli.Program).Assembly.Location, "mcp", "--root", root, "--project", "input.valencia" }) start.ArgumentList.Add(argument);
        if (authorize) start.ArgumentList.Add("--allow-obs-control");
        start.Environment["VALENCIA_OBS_PASSWORD"] = peer.Password;
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
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
        JsonElement Data(JsonElement message) => message.GetProperty("result").GetProperty("structuredContent").GetProperty("result");
        try
        {
            await Rpc("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "OBS protocol qualification", version = "1" } });
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            Assert.Equal(26, (await Rpc("tools/list", new { })).GetProperty("result").GetProperty("tools").GetArrayLength());
            var connected = await Tool("obs_connect", new { projectId, request = new { endpoint = peer.Endpoint.ToString(), recordingRoot = "recordings", manifest = "session.json" } });
            Assert.False(connected.GetProperty("result").GetProperty("isError").GetBoolean(), connected.GetRawText());
            var started = await Tool("obs_record", new { projectId, action = "start" });
            Assert.Equal(!authorize, started.GetProperty("result").GetProperty("isError").GetBoolean());
            if (authorize)
            {
                var marker = Data(await Tool("obs_marker", new { projectId, note = "Explanation" }));
                Assert.Equal(2500, marker.GetProperty("outputDurationMs").GetDecimal());
                await Tool("obs_record", new { projectId, action = "pause" });
                await Tool("obs_record", new { projectId, action = "resume" });
                await Tool("obs_record", new { projectId, action = "stop" });
                var imported = Data(await Tool("obs_import", new { projectId, revision = "1" }));
                Assert.True(imported.GetProperty("success").GetBoolean());
                Assert.Single(imported.GetProperty("assetIds").EnumerateArray());
                var status = Data(await Tool("obs_status", new { projectId }));
                Assert.True(status.GetProperty("files")[0].TryGetProperty("assetId", out _));
                var stale = await Tool("obs_import", new { projectId, revision = "1" });
                Assert.True(stale.GetProperty("result").GetProperty("isError").GetBoolean());
            }
            else Assert.False(peer.Active);
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain(peer.Password, await errors);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task ActualCopelandRecordingControlAndIngestionRetainMetadataAfterDisposal()
    {
        await using var peer = new ObsProtocolFixture { Output = media, StopDrainMs = 100 };
        string? original = Environment.GetEnvironmentVariable("VALENCIA_OBS_PASSWORD");
        Environment.SetEnvironmentVariable("VALENCIA_OBS_PASSWORD", peer.Password);
        try
        {
            await using var engine = await EditingEngine.OpenAsync(project);
            using var host = new CopelandScriptHost();
            string manifest = Path.Combine(root, "script-manifest.json");
            var result = await host.RunAsync($$"""
                using Valencia.Scripting;
                function run(api: ScriptApi): string {
                    api.ObsConnect("{{peer.Endpoint}}", "{{Path.Combine(root, "recordings").Replace('\\', '/')}}", "{{manifest.Replace('\\', '/')}}", true);
                    api.ObsStart();
                    api.ObsMarker("Important explanation");
                    api.ObsPause(true);
                    api.ObsPause(false);
                    api.ObsStop();
                    const imported = api.ObsImport();
                    api.Save("{{Path.Combine(root, "saved.valencia").Replace('\\', '/')}}");
                    api.ObsDisconnect();
                    return imported;
                }
                """, Path.Combine(root, "obs.ts"), engine);
            Assert.True(result.Success, EngineJson.Write(result.Diagnostics));
            var recorded = ObsManifestFile.Load(manifest);
            Assert.NotNull(recorded.Files[0].AssetId);
            Assert.NotNull(recorded.Files[0].Streams);
            Assert.Equal(engine.Query().Assets[0].Id, recorded.Files[0].AssetId);
            Assert.Single(recorded.Markers);
            Assert.DoesNotContain(peer.Password, await File.ReadAllTextAsync(manifest));
        }
        finally { Environment.SetEnvironmentVariable("VALENCIA_OBS_PASSWORD", original); }
    }
}
