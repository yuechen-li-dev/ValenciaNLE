using System.Text.Json;
using System.Text.Json.Nodes;
using Dominatus.Core.Transitions;
using Dominatus.OptFlow;
using Valencia.Engine;
using Valencia.Scripting;

if (args is not ([_, _] or [_, _, "--apply", _]))
{
    Console.Error.WriteLine("Usage: Valencia.DominatusAgent <project.valencia> <review.json> [--apply <new.valencia>]");
    return 2;
}
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
await using var engine = await EditingEngine.OpenAsync(args[0], cancellation: cancel.Token);
using var input = JsonDocument.Parse(await File.ReadAllTextAsync(args[1], cancel.Token));
var root = input.RootElement;
string? workflow = root.TryGetProperty("workflow", out var namedWorkflow) ? namedWorkflow.GetString() : null;
JsonElement? authoredPlan = root.TryGetProperty("plan", out var planInput) ? planInput : null;
if ((workflow is null) == (authoredPlan is null)) throw new ArgumentException("Supply one authored plan or a registered workflow.");
var context = new RunnerInput(EngineJson.Int64(input.RootElement.GetProperty("revision").GetString()!),
    authoredPlan, workflow, root.TryGetProperty("settings", out var settings) ? settings : null,
    root.GetProperty("observation"), args.Length == 4 ? args[3] : null);
var flow = Transition.For<Phase, Signal, RunnerInput, Effect>();
var machine = flow.Define(
[
    flow.On<Discovering, Start>("discover", static (s, _, _) => new(s, [new("session_info", new { })])),
    flow.On<Discovering, Completed>("prepare", static (_, done, ctx) =>
    {
        var result = Read(done.Reply);
        var project = result.GetProperty("projectId").GetString()!;
        Effect prepare = ctx.Workflow is null
            ? new("agent_plan", new { projectId = project, revision = ctx.ExpectedRevision, action = "prepare", plan = ctx.Plan })
            : new(EditorialWorkflows.Method, new { projectId = project, revision = ctx.ExpectedRevision, action = "prepare",
                workflow = ctx.Workflow, settings = ctx.Settings });
        return new(new Preparing(project, done.Reply.Revision), [prepare]);
    }),
    flow.On<Preparing, Completed>("render", static (state, done, ctx) =>
    {
        var data = Read(done.Reply);
        if (ctx.Workflow is not null && !data.TryGetProperty("preparation", out data))
            return new(new Failed("NO_EDITORIAL_PLAN"));
        var candidate = data.GetProperty("candidate").GetString()!;
        var observation = JsonNode.Parse(ctx.Observation.GetRawText())!.AsObject();
        observation["projectId"] = state.Project;
        observation["revision"] = EngineJson.Exact(state.Revision);
        observation["candidate"] = candidate;
        observation["mode"] = "paired";
        return new(new Reviewing(state.Project, state.Revision, candidate), [new("observe", observation)]);
    }),
    flow.On<Reviewing, Completed>("await-selection", static (state, _, _) =>
        new(new AwaitingSelection(state.Project, state.Revision, state.Candidate))),
    flow.On<AwaitingSelection, Selected>("apply", static (state, _, _) => new(new Applying(state.Project),
        [new("agent_plan", new { projectId = state.Project, revision = state.Revision, action = "commit", candidate = state.Candidate })])),
    flow.On<Applying, Completed>("save", static (state, done, ctx) => new(new Saving(),
        [new("agent_history", new { projectId = state.Project, revision = done.Reply.Revision, action = "save", output = ctx.SaveAs })])),
    flow.On<Saving, Completed>("done", static (_, _, _) => new(new Done())),
    flow.On<Phase, Rejected>("stop-on-engine-error", static (_, error, _) => new(new Failed(error.Issue.Code)))
], unmatched: UnmatchedEventBehavior.Reject);

Phase phase = new Discovering();
Signal signal = new Start();
var trace = new List<object>();
var responses = new List<AgentReply>();
for (int step = 0; step < 8; step++)
{
    cancel.Token.ThrowIfCancellationRequested();
    var result = machine.Dispatch(phase, signal, context);
    trace.Add(new { rule = result.Inspection.SelectedRuleId, state = phase.GetType().Name,
        next = result.NextState.GetType().Name, result.Inspection.Status, effects = result.Effects.Select(e => e.Method).ToArray() });
    if (!result.IsAccepted) throw new InvalidOperationException("State machine did not accept " + signal.GetType().Name);
    phase = result.NextState;
    if (phase is Done or Failed || phase is AwaitingSelection && context.SaveAs is null) break;
    if (phase is AwaitingSelection) { signal = new Selected(); continue; } // Only an explicit --apply supplies this input.
    Effect effect = result.Effects.Single();
    var request = JsonSerializer.SerializeToElement(effect.Request, EngineJson.Options);
    var reply = effect.Method == EditorialWorkflows.Method
        ? await EditorialWorkflows.InvokeAsync(engine, request, cancel.Token)
        : await engine.AgentAsync(effect.Method, request, cancel.Token);
    responses.Add(reply);
    signal = reply.Ok ? new Completed(reply) : new Rejected(reply.Issue!);
}
Console.WriteLine(EngineJson.Write(new { state = phase.GetType().Name, trace, responses }));
return phase is Done or AwaitingSelection ? 0 : 2;

static JsonElement Read(AgentReply reply) => JsonSerializer.SerializeToElement(reply.Result, EngineJson.Options);

sealed record RunnerInput(long ExpectedRevision, JsonElement? Plan, string? Workflow, JsonElement? Settings,
    JsonElement Observation, string? SaveAs);
sealed record Effect(string Method, object Request);
abstract record Phase;
sealed record Discovering : Phase;
sealed record Preparing(string Project, long Revision) : Phase;
sealed record Reviewing(string Project, long Revision, string Candidate) : Phase;
sealed record AwaitingSelection(string Project, long Revision, string Candidate) : Phase;
sealed record Applying(string Project) : Phase;
sealed record Saving : Phase;
sealed record Done : Phase;
sealed record Failed(string Code) : Phase;
abstract record Signal;
sealed record Start : Signal;
sealed record Completed(AgentReply Reply) : Signal;
sealed record Selected : Signal;
sealed record Rejected(AgentIssue Issue) : Signal;
