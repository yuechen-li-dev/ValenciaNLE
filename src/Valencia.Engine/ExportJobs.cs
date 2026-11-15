using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.Engine;

public sealed record ExportStatus(Guid JobId, Guid ProjectId, string Revision, string State, long Frames, long TotalFrames,
    string Output, string? Error);

/// <summary>One export at a time, at most 32 retained job records. Independent of editing commits and playback.</summary>
public sealed class ExportJobs(EditingEngine engine) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Job> jobs = [];
    private readonly SemaphoreSlim encoder = new(1, 1);
    public Guid Start(Guid sequence, string path, Guid? profileId = null)
    {
        var snapshot = engine.Session.Snapshot;
        var document = new TimelineQueries(snapshot).Sequence(sequence);
        var profile = profileId is { } profileIdentity ? document.Creator?.Profiles.First(p => p.Id == profileIdentity)
            ?? throw new ArgumentException("Unknown output profile.") : null;
        string output = engine.OutputPath(path);
        lock (gate)
        {
            if (jobs.Values.Count(j => !j.Task.IsCompleted) >= 4) throw new InvalidOperationException("Export queue is full (four jobs).");
            if (jobs.Values.Any(j => !j.Task.IsCompleted && string.Equals(j.Status.Output, output, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("This destination already has a queued export.");
            while (jobs.Count >= 32)
            {
                var oldest = jobs.First(j => j.Value.Task.IsCompleted);
                oldest.Value.Cancellation.Dispose();
                jobs.Remove(oldest.Key);
            }
            var id = Guid.NewGuid();
            string state = jobs.Values.Any(j => !j.Task.IsCompleted) ? "queued" : "running";
            var job = new Job(new(id, snapshot.Document.Id, EngineJson.Exact(snapshot.Revision), state, 0, 0, output, null));
            jobs.Add(id, job);
            job.Task = Task.Run(async () =>
            {
                bool admitted = false;
                try
                {
                    await encoder.WaitAsync(job.Cancellation.Token).ConfigureAwait(false);
                    admitted = true;
                    lock (gate) job.Status = job.Status with { State = "running" };
                    await SequenceExport.RunAsync(snapshot, sequence, output, new ProgressSink(p =>
                    {
                        lock (gate) job.Status = job.Status with { Frames = p.Frames, TotalFrames = p.TotalFrames };
                    }), job.Cancellation.Token, profile).ConfigureAwait(false);
                    lock (gate) job.Status = job.Status with { State = "completed" };
                }
                catch (OperationCanceledException) { lock (gate) job.Status = job.Status with { State = "cancelled" }; }
                catch (Exception error) { lock (gate) job.Status = job.Status with { State = "failed", Error = error.Message }; }
                finally { if (admitted) encoder.Release(); }
            });
            return id;
        }
    }
    public ExportStatus Status(Guid id) { lock (gate) return jobs[id].Status; }
    public void Cancel(Guid id) { lock (gate) jobs[id].Cancellation.Cancel(); }
    public async Task<ExportStatus> WaitAsync(Guid id, CancellationToken cancellation = default)
    {
        Task work;
        lock (gate) work = jobs[id].Task;
        await work.WaitAsync(cancellation).ConfigureAwait(false);
        return Status(id);
    }
    public async ValueTask DisposeAsync()
    {
        Job[] remaining;
        lock (gate) { remaining = [.. jobs.Values]; foreach (var job in remaining) job.Cancellation.Cancel(); }
        await Task.WhenAll(remaining.Select(j => j.Task)).ConfigureAwait(false);
        foreach (var job in remaining) job.Cancellation.Dispose();
        encoder.Dispose();
    }
    private sealed class Job(ExportStatus status)
    {
        public ExportStatus Status = status;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task = Task.CompletedTask;
    }
    private sealed class ProgressSink(Action<ExportProgress> update) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => update(value);
    }
}
