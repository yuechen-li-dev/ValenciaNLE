using System.Collections.Immutable;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;

namespace Valencia.Scripting;

public sealed record WorkflowOptions(Guid? SequenceId = null, long? ExpectedRevision = null,
    IReadOnlyDictionary<string, string>? Arguments = null, string EntryPoint = "run", bool Commit = false);

public sealed record WorkflowResult(bool Success, bool Changed, bool Committed, Guid ProjectId,
    string ExpectedRevision, string CurrentRevision, ScriptResult Script);

public sealed partial class CopelandScriptHost
{
    private int runningWorkflow;

    /// <summary>Run trusted Copeland code against a private session and optionally publish one edit.</summary>
    public async Task<WorkflowResult> RunWorkflowFileAsync(string path, EditingEngine engine,
        WorkflowOptions? options = null, CancellationToken cancellation = default)
    {
        options ??= new();
        var original = engine.Session.Snapshot;
        long expected = options.ExpectedRevision ?? original.Revision;
        WorkflowResult Report(ScriptResult script, bool changed = false, bool committed = false) =>
            new(script.Success, changed, committed, original.Document.Id, EngineJson.Exact(expected),
                EngineJson.Exact(engine.Session.Snapshot.Revision), script);
        WorkflowResult Failure(string code, string message) =>
            Report(new(false, null, [new(code, message, path, 0, 0)], false, 0));

        if (Interlocked.CompareExchange(ref runningWorkflow, 1, 0) != 0)
            return Failure("WORKFLOW_BUSY", "This host already has a workflow running.");
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (original.Revision != expected)
                return Failure("WORKFLOW_STALE", "The project revision no longer matches the workflow request.");
            Guid? sequence = options.SequenceId ?? original.Document.Sequences.FirstOrDefault()?.Id;
            if (sequence is { } sequenceId && !original.Document.Sequences.Any(item => item.Id == sequenceId))
                return Failure("WORKFLOW_SEQUENCE", "The requested sequence is not in this project.");
            var arguments = options.Arguments?.ToImmutableDictionary(StringComparer.Ordinal)
                ?? ImmutableDictionary<string, string>.Empty;
            if (arguments.Count > 64 || arguments.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                || pair.Key.Length > 128 || pair.Value is null || pair.Value.Length > 16384))
                return Failure("WORKFLOW_ARGUMENTS", "Use at most 64 named string arguments within the size limits.");

            string resolved = Path.GetFullPath(path);
            string source;
            using (var file = File.OpenRead(resolved))
            {
                if (file.Length > 1_048_576)
                    return Failure("WORKFLOW_SOURCE", "Workflow file exceeds 1 MiB.");
                using var reader = new StreamReader(file);
                source = await reader.ReadToEndAsync(cancellation).ConfigureAwait(false);
            }
            await using var staged = new EditingEngine(new EditingSession(original), engine.Policy, engine.ProjectPath,
                engine.Analysis.Cache.DirectoryPath);
            var api = new ScriptApi(staged, cancellation, allowProjectReplacement: false,
                workflow: new(sequence, arguments));
            var script = await InvokeAsync(source, resolved, options.EntryPoint, api, cancellation).ConfigureAwait(false);
            if (!script.Success) return Report(script);
            cancellation.ThrowIfCancellationRequested();
            var candidate = staged.Session.Snapshot.Document;
            bool changed = !DocumentSemantics.Equal(original.Document, candidate);
            if (!options.Commit) return Report(script, changed);

            var result = engine.Session.CommitStagedEdit(original, candidate, Guid.NewGuid());
            if (!result.Success)
                return Report(script with
                {
                    Success = false,
                    Diagnostics = [new(result.Error == EditError.StaleRevision ? "WORKFLOW_STALE" : "WORKFLOW_COMMIT",
                        result.Message ?? result.Error.ToString(), resolved, 0, 0)]
                }, changed);
            return Report(script, changed, committed: changed);
        }
        catch (OperationCanceledException error) { return Failure("SCRIPT_CANCELLED", error.Message); }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Failure("WORKFLOW_INPUT", error.Message);
        }
        finally { Volatile.Write(ref runningWorkflow, 0); }
    }
}

internal sealed record WorkflowContext(Guid? SequenceId, ImmutableDictionary<string, string> Arguments);
