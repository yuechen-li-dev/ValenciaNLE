using System.Text.Json;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Scripting;
using Xunit;
using static Valencia.Testing.TestProject;

namespace Valencia.Programming.Tests;

public sealed class EngineAndScriptTests
{
    [Fact]
    public async Task TypedPlanDryRunCommitAndDirectCommandsHaveIdenticalEffectsAndUndo()
    {
        var session = Create();
        var before = session.Snapshot;
        await using var engine = new EditingEngine(session);
        var plan = new EditPlan(1, ProjectId, EngineJson.Exact(before.Revision), SequenceId,
            [new SplitPlan(ClipId, EngineJson.Exact(T(5).Ticks), Id(8)), new MovePlan(Id(8), Video2, EngineJson.Exact(T(7).Ticks))]);
        var wire = EngineJson.Write(plan);
        var parsed = EditPlanSchema.Parse(wire);
        var preview = engine.Plan(parsed, false);
        Assert.True(preview.Valid, string.Join(";", preview.Diagnostics));
        Assert.True(preview.CanCommit);
        Assert.Same(before, session.Snapshot);
        Assert.Contains(preview.Changes, c => c.Kind == "created" && c.ClipId == Id(8));
        var actual = engine.Plan(parsed, true);
        Assert.True(actual.Valid);
        Assert.Equal(EngineJson.Write(preview.Changes), EngineJson.Write(actual.Changes));
        var direct = new EditingSession(before);
        Assert.True(direct.Execute(new EditTransaction(Id(222), 1, before.Revision,
            [new(Id(223), 1, new SplitClip(SequenceId, ClipId, T(5), Id(8))), new(Id(224), 1, new MoveClip(SequenceId, Id(8), Video2, T(7)))] )).Success);
        Assert.True(DocumentSemantics.Equal(direct.Snapshot.Document, session.Snapshot.Document));
        Assert.False(engine.Plan(parsed, false).Valid);
        Assert.True(engine.Undo(session.Snapshot.Revision).Success);
        Assert.True(DocumentSemantics.Equal(before.Document, session.Snapshot.Document));
        Assert.True(engine.Redo(session.Snapshot.Revision).Success);
        Assert.True(DocumentSemantics.Equal(direct.Snapshot.Document, session.Snapshot.Document));
    }
    [Theory]
    [InlineData("overlap")][InlineData("identity")][InlineData("stale")][InlineData("rollback")][InlineData("overflow")]
    [InlineData("unknownTrack")][InlineData("unknownSequence")]
    public async Task RejectedPlansDoNotMutateOrConsumeHistory(string kind)
    {
        var session = Create();
        if (kind == "overflow") session = new(new ProjectSnapshot(session.Snapshot.Document, long.MaxValue));
        var before = session.Snapshot;
        await using var engine = new EditingEngine(session);
        PlanOperation operation = kind switch
        {
            "identity" => new DeletePlan(Id(999)),
            "unknownTrack" => new MovePlan(ClipId, Id(999), "0"),
            _ => new MovePlan(ClipId, Video1, "-1")
        };
        var plan = new EditPlan(1, ProjectId, kind == "stale" ? "0" : EngineJson.Exact(before.Revision), kind == "unknownSequence" ? Id(999) : SequenceId,
            kind == "rollback" ? [new SplitPlan(ClipId, EngineJson.Exact(T(5).Ticks), Id(8)), operation]
            : kind == "overlap" ? [new SplitPlan(ClipId, EngineJson.Exact(T(5).Ticks), Id(8)), new MovePlan(ClipId, Video1, EngineJson.Exact(T(6).Ticks))]
            : kind == "overflow" ? [new DeletePlan(ClipId)] : [operation]);
        Assert.False(engine.Plan(plan, false).Valid);
        Assert.False(engine.Plan(plan, true).Valid);
        Assert.Same(before, session.Snapshot);
    }
    [Fact]
    public async Task UiScopeSharesCurrentProjectPathAndRejectsProjectReplacement()
    {
        var session = Create();
        string path = Path.GetFullPath("ui-project.valencia");
        await using var engine = new EditingEngine(session, projectPath: path);
        using var host = new CopelandScriptHost();
        var result = await host.RunAsync("using Valencia.Scripting; function run(api: ScriptApi): string { return api.ProjectFile(); }",
            "ui-query.ts", engine, allowProjectReplacement: false);
        Assert.True(result.Success, EngineJson.Write(result.Diagnostics));
        Assert.Equal(path, result.ReturnValue);
        var before = session.Snapshot;
        var replace = await host.RunAsync("using Valencia.Scripting; function run(api: ScriptApi): string { api.OpenProject(api.ProjectFile()); return api.Revision(); }",
            "ui-replace.ts", engine, allowProjectReplacement: false);
        Assert.False(replace.Success);
        Assert.Same(before, session.Snapshot);
    }
    [Fact]
    public async Task NativeInt64ScriptCommitsExactEditsAndReportsOverflowWithoutMutation()
    {
        var session = Create();
        await using var engine = new EditingEngine(session);
        using var host = new CopelandScriptHost();
        var result = await host.RunAsync("""
            using Valencia.Scripting;
            function run(api: ScriptApi): string {
                const exact: long = api.AddTicks(9007199254740993L, 1L);
                const sequence = api.FirstSequence();
                const plan = api.Plan(sequence);
                const splitAt: long = api.Frames(150L, 30, 1);
                plan.Split(api.FirstClip(sequence), splitAt, "00000008-0000-0000-0000-000000000000");
                plan.Move("00000008-0000-0000-0000-000000000000", api.FirstTrack(sequence, "video"), 9007199254740993L);
                api.Apply(plan);
                api.Undo();
                api.Redo();
                return String.From(exact);
            }
            """, "native-int64.ts", engine);
        Assert.True(result.Success, EngineJson.Write(result.Diagnostics));
        Assert.Equal("9007199254740994", result.ReturnValue);
        Assert.Equal(2, engine.Query().Clips(SequenceId, Video1).Length);
        Assert.Equal(T(5), engine.Query().Clips(SequenceId, Video1)[0].Duration);
        Assert.Equal(9007199254740993L, engine.Query().Clips(SequenceId, Video1).Single(c => c.Id == Id(8)).Start.Ticks);
        var before = session.Snapshot;
        var failed = await host.RunAsync("""
            using Valencia.Scripting;
            function run(api: ScriptApi): string {
                const maximum: long = 9223372036854775807L;
                return String.From(api.AddTicks(maximum, 1L));
            }
            """, "native-overflow.ts", engine);
        Assert.False(failed.Success);
        Assert.Contains(failed.Diagnostics, d => d.Code == "SCRIPT_RUNTIME");
        Assert.Same(before, session.Snapshot);
    }

    [Fact]
    public async Task RealCopelandCSharpRoslynCallsFacadePreservesInt64AndUsesCompilationCache()
    {
        await using var engine = new EditingEngine(Create());
        using var host = new CopelandScriptHost();
        const string source = """
            type CopelandProfile = "Dotnet";
            using Valencia.Scripting;
            function run(api: ScriptApi): string {
                return api.AddTicks("9007199254740993", "1");
            }
            """;
        var first = await host.RunAsync(source, Path.GetFullPath("ticks.ts"), engine);
        Assert.True(first.Success, EngineJson.Write(first.Diagnostics));
        Assert.Equal("9007199254740994", first.ReturnValue);
        var second = await host.RunAsync(source, Path.GetFullPath("ticks.ts"), engine);
        Assert.True(second.Success);
        Assert.True(second.CacheHit);
    }
    [Fact]
    public async Task RealScriptBuildsValidatesCommitsAndUndoesAPlan()
    {
        var session = Create();
        await using var engine = new EditingEngine(session);
        using var host = new CopelandScriptHost();
        var source = """
            using Valencia.Scripting;
            function run(api: ScriptApi): string {
                const sequence = api.FirstSequence();
                const plan = api.Plan(sequence);
                plan.Split(api.FirstClip(sequence), api.Frames("150", 30, 1), "00000008-0000-0000-0000-000000000000");
                const preview = api.Validate(plan);
                api.Apply(plan);
                api.Undo();
                api.Redo();
                return preview;
            }
            """;
        var result = await host.RunAsync(source, Path.GetFullPath("edit.ts"), engine);
        Assert.True(result.Success, EngineJson.Write(result.Diagnostics));
        Assert.Equal(2, engine.Query().Clips(SequenceId, Video1).Length);
        Assert.True(JsonDocument.Parse(result.ReturnValue!).RootElement.GetProperty("valid").GetBoolean());
    }
    [Fact]
    public async Task CompilerAndRuntimeDiagnosticsAndCancellationAreStructured()
    {
        var session = Create();
        var before = session.Snapshot;
        await using var engine = new EditingEngine(session);
        using var host = new CopelandScriptHost();
        var invalid = await host.RunAsync("using Valencia.Scripting;\nfunction run(api: ScriptApi): string { return api.NoSuchMethod(); }", "bad.ts", engine);
        Assert.False(invalid.Success);
        Assert.Contains(invalid.Diagnostics, d => d.Path == "bad.ts" && d.Line == 2);
        var overflow = await host.RunAsync("using Valencia.Scripting; function run(api: ScriptApi): string { return api.AddTicks(\"9223372036854775807\", \"1\"); }", "overflow.ts", engine);
        Assert.False(overflow.Success);
        Assert.Contains(overflow.Diagnostics, d => d.Code == "SCRIPT_RUNTIME");
        var signature = await host.RunAsync("function run(): string { return \"wrong entry\"; }", "signature.ts", engine);
        Assert.False(signature.Success);
        Assert.Contains(signature.Diagnostics, d => d.Code == "SCRIPT_ENTRY");
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        var cancelled = await host.RunAsync("", "cancel.ts", engine, cancellation: stopping.Token);
        Assert.Contains(cancelled.Diagnostics, d => d.Code == "SCRIPT_CANCELLED");
        Assert.Same(before, session.Snapshot);
    }
    [Fact]
    public void TypedObjectAuthoringAndJsonUseOneCompilerOwnedSchema()
    {
        string source = $$"""
            const $value: EditPlan = { schemaVersion: 1, projectId: "{{ProjectId}}", projectRevision: "2", sequenceId: "{{SequenceId}}", operations: [{ operation: "delete", clipId: "{{ClipId}}" }] };
            """;
        var typed = EditPlanSchema.Parse(source, "delete.obj.ts");
        Assert.Equal(ClipId, Assert.IsType<DeletePlan>(Assert.Single(typed.Operations)).ClipId);
        Assert.Equal(EngineJson.Write(typed), EngineJson.Write(EditPlanSchema.Parse(EngineJson.Write(typed))));
        Assert.ThrowsAny<Exception>(() => EditPlanSchema.Parse(source.Replace("projectRevision: \"2\"", "projectRevision: 2"), "bad.obj.ts"));
        Assert.ThrowsAny<Exception>(() => EngineJson.Int64("01"));
        Assert.Throws<OverflowException>(() => EngineJson.Int64("9223372036854775808"));
    }
}
