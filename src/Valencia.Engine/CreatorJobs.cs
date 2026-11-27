namespace Valencia.Engine;

public sealed record CreatorJobStatus(Guid JobId, string State, string Phase, string? PlanKey, string? Error)
{
    public AgentIssue? Issue => EditingEngine.JobIssue(State, Error, JobId);
}

/// <summary>One creator-planning/normalization worker and sixteen retained results. Planning never commits.</summary>
public sealed class CreatorJobs(EditingEngine engine) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Job> jobs = [];
    public Guid Start(CreatorRequest request, long revision)
    {
        lock (gate)
        {
            if (revision != engine.Session.Snapshot.Revision) throw new ArgumentException("Stale creator revision.");
            if (jobs.Values.Any(j => !j.Task.IsCompleted)) throw new InvalidOperationException("A creator job is already running.");
            while (jobs.Count >= 16) { var first = jobs.First(); first.Value.Cancellation.Dispose(); jobs.Remove(first.Key); }
            Guid id = Guid.NewGuid();
            var job = new Job(new(id, "running", "analysis and planning; normalization may decode the proposed sequence", null, null));
            jobs[id] = job;
            job.Task = Task.Run(async () =>
            {
                try
                {
                    var result = await engine.CreateCreatorAsync(request, job.Cancellation.Token, revision).ConfigureAwait(false);
                    lock (gate) job.Status = job.Status with { State = "completed", Phase = "ready for explicit commit", PlanKey = result.Key };
                }
                catch (OperationCanceledException) { lock (gate) job.Status = job.Status with { State = "cancelled" }; }
                catch (Exception e) { lock (gate) job.Status = job.Status with { State = "failed", Error = e.Message }; }
            });
            return id;
        }
    }
    public CreatorJobStatus Status(Guid id) { lock (gate) return jobs[id].Status; }
    public CreatorJobStatus[] ActiveJobs() { lock (gate) return jobs.Values.Select(j => j.Status).Where(s => s.State == "running").ToArray(); }
    public void Cancel(Guid id) { lock (gate) jobs[id].Cancellation.Cancel(); }
    public async Task<CreatorJobStatus> WaitAsync(Guid id, CancellationToken cancellation = default)
    {
        Task task; lock (gate) task = jobs[id].Task;
        await task.WaitAsync(cancellation).ConfigureAwait(false);
        return Status(id);
    }
    public async ValueTask DisposeAsync()
    {
        Job[] remaining;
        lock (gate) { remaining = [.. jobs.Values]; foreach (var job in remaining) job.Cancellation.Cancel(); }
        await Task.WhenAll(remaining.Select(j => j.Task)).ConfigureAwait(false);
        foreach (var job in remaining) job.Cancellation.Dispose();
    }
    private sealed class Job(CreatorJobStatus status)
    {
        public CreatorJobStatus Status = status;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task = Task.CompletedTask;
    }
}
