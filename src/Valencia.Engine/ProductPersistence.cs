using System.Text.Json;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Persistence;

namespace Valencia.Engine;

public sealed partial class EditingEngine
{
    private string ProductDataRoot => Policy is null ? ValenciaData.Root : Policy.Resolve(".valencia-data");

    private async Task<object> ManageDurableCheckpoint(ProjectSnapshot snapshot, JsonElement request, CancellationToken cancellation)
    {
        var store = new ProjectCheckpoints(Path.Combine(ProductDataRoot, "Checkpoints"));
        Guid project = snapshot.Document.Id;
        string action = Text(request, "action")!;
        if (action == "list")
        {
            var entries = await store.ListAsync(project, cancellation).ConfigureAwait(false);
            return entries.Select(e => new { name = e.Metadata?.Name, revision = e.Metadata?.Revision, e.Metadata?.CapturedUtc,
                e.Integrity, e.MissingMedia, e.Error, persistence = "durable" }).ToArray();
        }
        string name = RequiredText(request, "name");
        if (action == "create")
        {
            var entry = await store.CreateAsync(snapshot, name, ProjectPath, cancellation).ConfigureAwait(false);
            return new { name, revision = snapshot.Revision, entry.Integrity, entry.Metadata?.CapturedUtc, persistence = "durable" };
        }
        if (action == "delete")
        {
            store.Delete(project, name);
            return new { name, deleted = true };
        }
        var loaded = await store.OpenAsync(project, name, cancellation).ConfigureAwait(false);
        if (action == "inspect")
            return new { name, revision = loaded.Snapshot.Revision, loaded.Metadata?.CapturedUtc, loaded.Integrity,
                missingMedia = loaded.MissingMedia.Length, persistence = "durable" };
        CheckMedia(loaded.Snapshot);
        var applied = Session.CommitStagedEdit(snapshot, loaded.Snapshot.Document, Guid.NewGuid());
        if (!applied.Success) throw new AgentRequestException(ReportEditFailure(applied, snapshot.Revision));
        return new { name, checkpointRevision = loaded.Snapshot.Revision, priorRevision = snapshot.Revision, resultingRevision = applied.Revision };
    }

    private async Task<object> ManageRecoveryAsync(ProjectSnapshot snapshot, JsonElement request, CancellationToken cancellation)
    {
        string root = Path.Combine(ProductDataRoot, "Recovery");
        string action = Text(request, "action")!;
        var entries = await RecoveryRun.ScanAsync(root, cancellation).ConfigureAwait(false);
        if (action == "list")
            return entries.Where(e => e.Metadata?.ProjectId == snapshot.Document.Id).Take(32).ToArray();
        string id = RequiredText(request, "id");
        var entry = entries.FirstOrDefault(e => e.Id == id && e.Metadata?.ProjectId == snapshot.Document.Id)
            ?? throw AgentError("RECOVERY_NOT_FOUND", "No valid generation with that ID belongs to this project.");
        if (action == "inspect") return entry;
        if (action == "discard")
        {
            RecoveryRun.Discard(root, id);
            return new { id, discarded = true };
        }
        var loaded = await RecoveryRun.OpenAsync(root, id, cancellation).ConfigureAwait(false);
        CheckMedia(loaded.Snapshot);
        string output = OutputPath(RequiredText(request, "output"));
        using var lease = ProjectWriteLease.Acquire(output);
        await lease.SaveAsync(loaded.Snapshot, null, cancellation).ConfigureAwait(false);
        return new { id, revision = loaded.Snapshot.Revision, output };
    }
}
