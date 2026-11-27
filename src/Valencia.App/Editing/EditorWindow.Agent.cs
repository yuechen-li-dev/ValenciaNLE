using System.Text.Json;
using Valencia.Core.Editing;
using Valencia.Engine;

namespace Valencia.App.Editing;

internal sealed partial class EditorWindow
{
    private EditingEngine? agentProposalEngine;
    private (EditingSession Session, Guid Project, long Revision, string Candidate)? stagedAgentProposal;

    private async Task<EditingEngine> DesktopAgentEngineAsync()
    {
        if (agentProposalEngine is { } existing)
        {
            if (ReferenceEquals(existing.Session, Editor.Session)) return existing;
            agentProposalEngine = null;
            await existing.DisposeAsync();
        }
        return agentProposalEngine = new EditingEngine(Editor.Session, projectPath: Editor.ProjectPath);
    }

    internal async Task ReviewAgentProposal(string path)
    {
        RejectAgentProposal();
        if (new FileInfo(path).Length > 1_048_576) throw new InvalidDataException("Agent plan request exceeds 1 MiB.");
        CancellationToken token = jobCancellation?.Token ?? default;
        using var input = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
        if (input.RootElement.ValueKind != JsonValueKind.Object || !input.RootElement.TryGetProperty("action", out var action)
            || action.ValueKind != JsonValueKind.String || action.GetString() != "prepare")
            throw new InvalidDataException("An .agent.json file contains an agent_plan request with action: prepare.");
        var session = Editor.Session;
        var basis = session.Snapshot;
        if (input.RootElement.TryGetProperty("creatorKey", out _))
            throw new InvalidDataException("A desktop handoff requires an authored or canonical plan, not a headless session's creator key.");
        var engine = await DesktopAgentEngineAsync();
        var result = await engine.AgentAsync("agent_plan", input.RootElement, token);
        if (!result.Ok)
        {
            Editor.Status = AgentStatus(result.Issue!);
            return;
        }
        if (!ReferenceEquals(Editor.Session, session) || !ReferenceEquals(session.Snapshot, basis)) return;
        var data = JsonSerializer.SerializeToElement(result.Result, EngineJson.Options);
        string handle = data.GetProperty("candidate").GetString()!;
        string summary = data.GetProperty("summary").GetProperty("text").GetString()!;
        PauseTransport();
        CancelGesture();
        Editor.ProposedCut = null;
        Editor.ProposedTextCut = null;
        Editor.HasAgentProposal = true;
        Editor.Status = summary;
        stagedAgentProposal = (session, basis.Document.Id, basis.Revision, handle);
        ShowCard(new(TitleCardKind.Proposal, "AGENT PLAN", "REVIEW BEFORE APPLYING", summary,
            [$"BASE REV {basis.Revision}", "ONE UNDO STEP"], []), sticky: true);
    }

    internal async Task ApplyAgentProposal()
    {
        if (!Editor.HasAgentProposal || stagedAgentProposal is not { } candidate || agentProposalEngine is not { } engine) return;
        if (!ReferenceEquals(candidate.Session, Editor.Session))
        {
            RejectAgentProposal();
            Editor.Status = "The proposed edit belonged to another editor session.";
            return;
        }
        var request = JsonSerializer.SerializeToElement(new { projectId = candidate.Project, revision = candidate.Revision,
            action = "commit", candidate = candidate.Candidate }, EngineJson.Options);
        var reply = await engine.AgentAsync("agent_plan", request, jobCancellation?.Token ?? default);
        RejectAgentProposal();
        if (reply.Ok)
        {
            Seek(Editor.Playhead);
            Editor.Status = $"Agent plan applied at revision {reply.Revision}. Ctrl+Z undoes it.";
        }
        else Editor.Status = AgentStatus(reply.Issue!);
    }

    private void RejectAgentProposal()
    {
        var staged = stagedAgentProposal;
        stagedAgentProposal = null;
        if (staged is { } candidate && agentProposalEngine is { } engine && ReferenceEquals(engine.Session, candidate.Session))
        {
            var snapshot = candidate.Session.Snapshot;
            var command = JsonSerializer.SerializeToElement(new { projectId = snapshot.Document.Id, revision = snapshot.Revision,
                action = "discard", candidate = candidate.Candidate }, EngineJson.Options);
            _ = engine.AgentAsync("agent_plan", command).GetAwaiter().GetResult();
        }
        if (!Editor.HasAgentProposal) return;
        Editor.HasAgentProposal = false;
        Editor.Card = null;
        Editor.Status = "Agent plan rejected; no edits applied.";
        lastUi = -1;
    }

    internal async Task ShowAgentCheckpoints(bool create)
    {
        var engine = await DesktopAgentEngineAsync();
        var snapshot = engine.Session.Snapshot;
        string name = "Revision " + EngineJson.Exact(snapshot.Revision);
        var request = JsonSerializer.SerializeToElement(new { projectId = snapshot.Document.Id, snapshot.Revision,
            action = create ? "create" : "list", name = create ? name : null, durable = true }, EngineJson.Options);
        var reply = await engine.AgentAsync("agent_checkpoint", request, jobCancellation?.Token ?? default);
        if (!reply.Ok) { Editor.Status = AgentStatus(reply.Issue!); return; }
        if (create) { Editor.Status = $"{name} checkpoint created."; return; }
        var items = JsonSerializer.SerializeToElement(reply.Result, EngineJson.Options).EnumerateArray().ToArray();
        Editor.Status = items.Length == 0 ? "No checkpoints in this desktop session." :
            $"{items.Length} session checkpoint(s): " + string.Join(", ", items.Take(4).Select(item => item.GetProperty("name").GetString()));
    }

    private static string AgentStatus(AgentIssue issue) => $"{issue.Code}: {issue.Message}" +
        (issue.Suggestion is null ? "" : " " + issue.Suggestion);
}
