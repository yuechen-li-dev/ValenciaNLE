using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Persistence;
using Valencia.Scripting;
using Xunit;
using static Valencia.Testing.TestProject;

namespace Valencia.Programming.Tests;

public sealed class CopelandWorkflowTests
{
    [Fact]
    public async Task RealWorkflowPreviewsAndCommitsMarkersAndPersistsOneUndoableEdit()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "chapter-markers.workflow.ts");
        var session = Create();
        var before = session.Snapshot;
        await using var engine = new EditingEngine(session);
        using var host = new CopelandScriptHost();
        var options = new WorkflowOptions(SequenceId, before.Revision, new Dictionary<string, string>
        {
            ["mode"] = "short",
            ["label"] = "Beat"
        });
        var preview = await host.RunWorkflowFileAsync(path, engine, options);
        Assert.True(preview.Success, EngineJson.Write(preview.Script.Diagnostics));
        Assert.True(preview.Changed);
        Assert.False(preview.Committed);
        Assert.Equal("Created 2 markers", preview.Script.ReturnValue);
        Assert.Same(before, session.Snapshot);

        var committed = await host.RunWorkflowFileAsync(path, engine, options with { Commit = true });
        Assert.True(committed.Success, EngineJson.Write(committed.Script.Diagnostics));
        Assert.True(committed.Script.CacheHit);
        Assert.True(committed.Committed);
        Assert.Equal(before.Revision + 1, session.Snapshot.Revision);
        var markers = engine.Query().Sequence(SequenceId).Markers;
        Assert.Equal(new[] { T(0), T(5) }, markers.Select(marker => marker.At));
        Assert.Equal(new[] { "Beat 1", "Beat 2" }, markers.Select(marker => marker.Label));
        var committedSnapshot = session.Snapshot;
        Assert.True(engine.Undo(committedSnapshot.Revision).Success);
        Assert.True(DocumentSemantics.Equal(before.Document, session.Snapshot.Document));
        Assert.True(engine.Redo(session.Snapshot.Revision).Success);
        Assert.Equal(markers, engine.Query().Sequence(SequenceId).Markers);

        using var directory = new TemporaryDirectory();
        string project = Path.Combine(directory.Path, "markers.valencia");
        await engine.SaveAsync(project);
        var loaded = await new ProjectFile().LoadAsync(project);
        Assert.True(DocumentSemantics.Equal(session.Snapshot.Document, loaded.Snapshot.Document));
    }

    [Theory]
    [InlineData("api.AddMarker(sequence, -1L, \"Invalid\");")]
    [InlineData("api.Save(api.ProjectFile());")]
    public async Task FailureAfterStagedEditLeavesLiveProjectUntouched(string fail)
    {
        using var directory = new TemporaryDirectory();
        string project = Path.Combine(directory.Path, "never-written.valencia");
        string path = Path.Combine(directory.Path, "failed.workflow.tsx");
        await File.WriteAllTextAsync(path, $$"""
            using Valencia.Scripting;
            function run(api: ScriptApi): string {
                const sequence = api.SelectedSequence();
                api.AddMarker(sequence, 0L, "Staged");
                {{fail}}
                return "Done";
            }
            """);
        var session = Create();
        var before = session.Snapshot;
        await using var engine = new EditingEngine(session, projectPath: project);
        using var host = new CopelandScriptHost();
        var outcome = await host.RunWorkflowFileAsync(path, engine, new(Commit: true));
        Assert.False(outcome.Success);
        Assert.Contains(outcome.Script.Diagnostics, diagnostic => diagnostic.Code == "SCRIPT_RUNTIME");
        Assert.Same(before, session.Snapshot);
        Assert.False(File.Exists(project));
    }

    [Fact]
    public async Task WorkflowStagesNewSequenceAndRejectsStaleOrCancelledRequests()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "make-sequence.ts");
        await File.WriteAllTextAsync(path, """
            using Valencia.Scripting;
            function build(api: ScriptApi): string {
                const sequence = api.CreateSequence(api.Argument("name", "Social"), 1080, 1920, 30, 1, 48000);
                api.AddMarker(sequence, 0L, "First edit");
                return sequence;
            }
            """);
        var session = Create();
        var before = session.Snapshot;
        await using var engine = new EditingEngine(session);
        using var host = new CopelandScriptHost();
        var options = new WorkflowOptions(ExpectedRevision: before.Revision, EntryPoint: "build", Commit: true);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var cancelled = await host.RunWorkflowFileAsync(path, engine, options, stop.Token);
        Assert.Contains(cancelled.Script.Diagnostics, diagnostic => diagnostic.Code == "SCRIPT_CANCELLED");
        Assert.Same(before, session.Snapshot);
        var result = await host.RunWorkflowFileAsync(path, engine, options);
        Assert.True(result.Success, EngineJson.Write(result.Script.Diagnostics));
        var created = engine.Query().Sequence(Guid.Parse(result.Script.ReturnValue!));
        Assert.Equal(1920, created.Video.Height);
        Assert.Single(created.Markers);
        var after = session.Snapshot;
        var stale = await host.RunWorkflowFileAsync(path, engine, options);
        Assert.Contains(stale.Script.Diagnostics, diagnostic => diagnostic.Code == "WORKFLOW_STALE");
        Assert.Same(after, session.Snapshot);
        var rejected = session.CommitStagedEdit(before, before.Document, Id(55));
        Assert.Equal(EditError.StaleRevision, rejected.Error);
        Assert.Same(after, session.Snapshot);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"valencia-workflow-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
