using System.Diagnostics;
using System.Text.Json;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Persistence;
using Xunit;
using static Valencia.Testing.TestProject;

namespace Valencia.Programming.Tests;

public sealed class HeadlessProtocolTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Valencia-M2a", Guid.NewGuid().ToString("N"));
    private string ProjectPath => Path.Combine(root, "project.valencia");
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        return Task.CompletedTask;
    }
    public Task DisposeAsync() { Directory.Delete(root, true); return Task.CompletedTask; }
    private static string CliAssembly => typeof(Valencia.Cli.Program).Assembly.Location;
    private static Process Start(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(CliAssembly);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }
    private static async Task<(int Exit, string Output, string Error)> Cli(params string[] arguments)
    {
        using var process = Start(arguments);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        return (process.ExitCode, await output, await error);
    }
    [Fact]
    public async Task ActualCliCompilesScriptAppliesTypedPlanAndRequiresExplicitSave()
    {
        var initial = Create();
        await new ProjectFile().SaveAsync(ProjectPath, initial);
        byte[] original = await File.ReadAllBytesAsync(ProjectPath);
        string script = Path.Combine(root, "query.ts");
        await File.WriteAllTextAsync(script, "using Valencia.Scripting; function run(api: ScriptApi): string { return api.AddTicks(\"9007199254740993\", \"1\"); }");
        var execution = await Cli("script", "run", script, "--project", ProjectPath);
        Assert.Equal(0, execution.Exit);
        Assert.Equal("9007199254740994", JsonDocument.Parse(execution.Output).RootElement.GetProperty("returnValue").GetString());
        var plan = new EditPlan(1, ProjectId, EngineJson.Exact(initial.Snapshot.Revision), SequenceId, [new DeletePlan(ClipId)]);
        string planPath = Path.Combine(root, "delete.obj.ts");
        await File.WriteAllTextAsync(planPath, $$"""
            const $value: EditPlan = { schemaVersion: 1, projectId: "{{ProjectId}}", projectRevision: "{{initial.Snapshot.Revision}}", sequenceId: "{{SequenceId}}", operations: [{ operation: "delete", clipId: "{{ClipId}}" }] };
            """);
        Assert.Equal(0, (await Cli("plan", "validate", planPath, "--project", ProjectPath)).Exit);
        Assert.Equal(0, (await Cli("plan", "apply", planPath, "--project", ProjectPath)).Exit);
        Assert.Equal(original, await File.ReadAllBytesAsync(ProjectPath));
        string saved = Path.Combine(root, "saved.valencia");
        var applied = await Cli("plan", "apply", planPath, "--project", ProjectPath, "--save-as", saved);
        Assert.Equal(0, applied.Exit);
        Assert.Empty((await new ProjectFile().LoadAsync(saved)).Snapshot.Document.Sequences[0].Tracks[0].Clips);
        Assert.Equal(original, await File.ReadAllBytesAsync(ProjectPath));
        var stale = await Cli("plan", "apply", planPath, "--project", saved);
        Assert.Equal(2, stale.Exit);
        Assert.False(JsonDocument.Parse(stale.Output).RootElement.GetProperty("valid").GetBoolean());
    }
    [Fact]
    public async Task ActualStdioInitializeDiscoveryQueryDryRunCommitConflictAndPathPolicy()
    {
        var initial = Create();
        // MCP workspace policy rejects external references, including missing ones.
        var local = initial.Snapshot with { Document = initial.Snapshot.Document with
        { Assets = [Asset(Path.Combine(root, "media.fixture"))] } };
        await new ProjectFile().SaveAsync(ProjectPath, local);
        using var process = Start("mcp", "--root", root, "--project", "project.valencia");
        Task<string> errors = process.StandardError.ReadToEndAsync();
        int id = 0;
        async Task<JsonElement> Request(string method, object parameters)
        {
            await process.StandardInput.WriteLineAsync(EngineJson.Write(new { jsonrpc = "2.0", id = ++id, method, @params = parameters }));
            await process.StandardInput.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string? line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.NotNull(line);
            return JsonDocument.Parse(line).RootElement.Clone();
        }
        JsonElement Value(JsonElement response) => response.GetProperty("result").GetProperty("structuredContent").GetProperty("result");
        var initialization = await Request("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "Valencia test", version = "1" } });
        Assert.Equal("2025-11-25", initialization.GetProperty("result").GetProperty("protocolVersion").GetString());
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        var tools = await Request("tools/list", new { });
        Assert.Equal(28, tools.GetProperty("result").GetProperty("tools").GetArrayLength());
        var effects = Value(await Tool("effects_list", new { projectId = ProjectId }));
        Assert.Equal("basic-color", effects.GetProperty("effects")[0].GetProperty("id").GetString());
        Assert.Equal("cover", effects.GetProperty("effects")[1].GetProperty("modes")[2].GetString());
        Assert.DoesNotContain("run_script", tools.GetRawText());
        async Task<JsonElement> Tool(string name, object arguments) => await Request("tools/call", new { name, arguments });
        var info = Value(await Tool("project_info", new { projectId = ProjectId }));
        Assert.Equal(ProjectId.ToString(), info.GetProperty("snapshot").GetProperty("document").GetProperty("id").GetString());
        var plan = new EditPlan(1, ProjectId, EngineJson.Exact(local.Revision), SequenceId, [new DeletePlan(ClipId)]);
        var preview = Value(await Tool("validate_edit_plan", new { projectId = ProjectId, revision = plan.ProjectRevision, plan }));
        Assert.True(preview.GetProperty("valid").GetBoolean());
        Assert.False(preview.GetProperty("committed").GetBoolean());
        var deniedReview = await Tool("review_timeline", new { projectId = ProjectId, revision = plan.ProjectRevision, sequenceId = SequenceId,
            selection = new { kind = "frame", atTicks = new[] { "0" } }, output = "../escaped.png" });
        Assert.Contains("Path escapes", Value(deniedReview).GetProperty("message").GetString());
        var committed = Value(await Tool("apply_edit_plan", new { projectId = ProjectId, revision = plan.ProjectRevision, plan }));
        Assert.True(committed.GetProperty("committed").GetBoolean());
        Assert.Equal(preview.GetProperty("changes").GetRawText(), committed.GetProperty("changes").GetRawText());
        var conflict = await Tool("apply_edit_plan", new { projectId = ProjectId, revision = plan.ProjectRevision, plan });
        Assert.True(conflict.GetProperty("result").GetProperty("isError").GetBoolean());
        var denied = await Tool("run_export", new { projectId = ProjectId, revision = committed.GetProperty("currentRevision").GetString(), sequenceId = SequenceId, output = "../escape.mp4" });
        Assert.True(denied.GetProperty("result").GetProperty("isError").GetBoolean());
        var wrongProject = await Tool("project_info", new { projectId = Guid.NewGuid() });
        Assert.True(wrongProject.GetProperty("result").GetProperty("isError").GetBoolean());
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(await errors));
        Assert.Single((await new ProjectFile().LoadAsync(ProjectPath)).Snapshot.Document.Sequences[0].Tracks[0].Clips);
    }
    [Fact]
    public void WorkspaceRejectsTraversalRootedPathsAndSourceAliases()
    {
        var policy = new WorkspacePolicy(root);
        Assert.Throws<UnauthorizedAccessException>(() => policy.Resolve("../outside"));
        Assert.Throws<UnauthorizedAccessException>(() => policy.Resolve(ProjectPath));
        Assert.Equal(ProjectPath, policy.Resolve("project.valencia"));
    }
}
