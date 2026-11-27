using System.Text.Json;
using System.Text.Json.Serialization;
using Valencia.Core.Timeline;
using Valencia.Media;
using Valencia.Persistence;

namespace Valencia.Engine;

public sealed record RenderSpecification(Guid SequenceId, string Output, Guid? ProfileId, string Encoder,
    ExportPreset? Preset, AudioFinish? Finish, bool BurnCaptions, int? Quality);
internal sealed record QueuedExport(int Version, ExportStatus Status, RenderSpecification Specification,
    DateTimeOffset CreatedUtc, IReadOnlyDictionary<Guid, string> SourceFingerprints);

internal sealed class ExportQueueStore(string root)
{
    // Durable records must round-trip required nullable fields in nested contracts.
    private static readonly JsonSerializerOptions StorageJson = new(EngineJson.Options)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public async Task CreateAsync(QueuedExport record, ProjectSnapshot snapshot, CancellationToken cancellation)
    {
        string folder = Folder(record.Status.ProjectId);
        Directory.CreateDirectory(folder);
        using var index = new FileStream(Path.Combine(folder, "queue.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var previous = Entries(record.Status.ProjectId).OrderBy(r => r.CreatedUtc).ToArray();
        int toRemove = Math.Max(0, previous.Length - 31);
        var terminal = previous.Where(r => r.Status.State is "completed" or "failed" or "cancelled").Take(toRemove).ToArray();
        if (terminal.Length < toRemove) throw new IOException("The render history has reached 32 records. Resume or clear interrupted jobs first.");
        foreach (var old in terminal)
        {
            File.Delete(RecordPath(old.Status.ProjectId, old.Status.JobId));
            File.Delete(SnapshotPath(old.Status.ProjectId, old.Status.JobId));
        }
        string target = SnapshotPath(record.Status.ProjectId, record.Status.JobId);
        await new ProjectFile().SaveAsync(target, snapshot, cancellation).ConfigureAwait(false);
        try { await WriteAsync(record, cancellation).ConfigureAwait(false); }
        catch { File.Delete(target); throw; }
    }

    public IEnumerable<QueuedExport> Entries(Guid project)
    {
        var folder = Folder(project);
        if (!Directory.Exists(folder)) yield break;
        foreach (string file in Directory.EnumerateFiles(folder, "*.job.json").Take(32))
        {
            string fileId = Path.GetFileName(file)[..^".job.json".Length];
            if (!Guid.TryParseExact(fileId, "N", out var id)) continue;
            yield return Read(project, id);
        }
    }

    public QueuedExport Read(Guid project, Guid id)
    {
        string path = RecordPath(project, id);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > 256 * 1024) throw new InvalidDataException("Render queue record exceeds 256 KiB.");
        var record = JsonSerializer.Deserialize<QueuedExport>(input, StorageJson);
        if (record is null || record.Version != 1 || record.Status.JobId != id || record.Status.ProjectId != project)
            throw new InvalidDataException("Render queue record identity is invalid.");
        return record;
    }

    public ExportStatus RecoverStatus(QueuedExport record)
    {
        var status = record.Status;
        if (status.State is not ("running" or "queued" or "checking")) return status;
        bool live;
        try
        {
            using var ignored = Hold(record.Status.ProjectId, record.Status.JobId);
            live = false;
        }
        catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33) { live = true; }
        if (live) return status;
        string partial = SequenceExport.PartialPath(status.Output, status.JobId);
        long? bytes = File.Exists(partial) ? new FileInfo(partial).Length : null;
        return status with { State = "interrupted", PartialBytes = bytes,
            Error = File.Exists(status.Output) ? "The final path exists. Inspect it or retry to a new destination."
                : "The previous process exited before reporting completion. Retry restarts encoding from the captured revision." };
    }

    public async Task WriteAsync(QueuedExport record, CancellationToken cancellation = default)
    {
        string destination = RecordPath(record.Status.ProjectId, record.Status.JobId);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(output, record, StorageJson, cancellation).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            cancellation.ThrowIfCancellationRequested();
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(destination)) File.Replace(temporary, destination, destinationBackupFileName: null);
                    else File.Move(temporary, destination);
                    break;
                }
                // Windows can briefly deny replacement while another process is reading the previous record.
                catch (Exception error) when (attempt < 5 && OperatingSystem.IsWindows() &&
                    (error is UnauthorizedAccessException || error is IOException && (error.HResult & 0xFFFF) is 5 or 32 or 33))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20 << attempt), cancellation).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Could not publish render queue record {record.Status.JobId}: {error.Message}", error);
        }
        finally { File.Delete(temporary); }
    }

    public FileStream Hold(Guid project, Guid id)
    {
        Directory.CreateDirectory(Folder(project));
        return new(Path.Combine(Folder(project), $"{id:N}.job.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public async Task<ProjectSnapshot> LoadSnapshotAsync(QueuedExport record, CancellationToken cancellation)
    {
        var saved = await new ProjectFile().LoadAsync(SnapshotPath(record.Status.ProjectId, record.Status.JobId), cancellation).ConfigureAwait(false);
        if (saved.Snapshot.Document.Id != record.Status.ProjectId || EngineJson.Exact(saved.Snapshot.Revision) != record.Status.Revision)
            throw new InvalidDataException("Stored render snapshot identity or revision is invalid.");
        return saved.Snapshot;
    }

    private string Folder(Guid project) => Path.Combine(root, project.ToString("N"));
    private string RecordPath(Guid project, Guid id) => Path.Combine(Folder(project), $"{id:N}.job.json");
    private string SnapshotPath(Guid project, Guid id) => Path.Combine(Folder(project), $"{id:N}.valencia");
}
