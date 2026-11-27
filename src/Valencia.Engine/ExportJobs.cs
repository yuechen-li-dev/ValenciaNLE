using System.Diagnostics;
using Valencia.Analysis;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.Engine;

public sealed record ExportStatus(Guid JobId, Guid ProjectId, string Revision, string State, long Frames, long TotalFrames,
    string Output, string? Error, string? Encoder = null, double? RemainingSeconds = null, double? ElapsedSeconds = null,
    long? Bytes = null, long? PartialBytes = null, ExportLintReport? Lint = null)
{
    public AgentIssue? Issue => State == "interrupted" ? new("EXPORT_INTERRUPTED", Error ?? "Export was interrupted.") :
        EditingEngine.JobIssue(State, Error, JobId);
}

/// <summary>Revision-pinned, FIFO export owner. Durable jobs reuse the same render and cancellation path as legacy jobs.</summary>
public sealed class ExportJobs(EditingEngine engine) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim submission = new(1);
    private readonly Dictionary<Guid, Job> jobs = [];
    private Task tail = Task.CompletedTask;
    private bool disposed;
    private ExportQueueStore Store => new(engine.ExportQueueRoot);

    public Guid Start(Guid sequence, string path, Guid? profileId = null)
    {
        var snapshot = engine.Session.Snapshot;
        string destination = engine.OutputPath(path);
        var encoder = ExportCatalog.ForDestination(destination);
        var spec = new RenderSpecification(sequence, destination, profileId, encoder.Id, null, null, true, null);
        _ = SelectProfile(snapshot, spec);
        lock (gate) return Schedule(snapshot, spec, Guid.NewGuid(), null, null).Status.JobId;
    }

    public async Task<ExportStatus> EnqueueAsync(ProjectSnapshot snapshot, RenderSpecification spec, CancellationToken cancellation = default)
    {
        await submission.WaitAsync(cancellation).ConfigureAwait(false);
        FileStream? owner = null;
        try
        {
            var encoder = ExportCatalog.Encoder(spec.Encoder);
            _ = SelectProfile(snapshot, spec);
            spec.Finish?.Validate();
            _ = ExportCatalog.EncoderArguments(encoder, spec.Quality);
            if (!Path.GetExtension(spec.Output).Equals(encoder.Extension, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"{encoder.Id} requires a {encoder.Extension} destination.");
            if (File.Exists(spec.Output)) throw new IOException("The final output already exists. Choose another destination.");
            lock (gate) CheckCapacity(spec.Output);
            Guid id = Guid.NewGuid();
            var fingerprints = await Task.Run(() => CaptureSources(snapshot, spec, cancellation), cancellation).ConfigureAwait(false);
            var status = new ExportStatus(id, snapshot.Document.Id, EngineJson.Exact(snapshot.Revision), "queued", 0, 0,
                spec.Output, null, spec.Encoder);
            var record = new QueuedExport(1, status, spec, DateTimeOffset.UtcNow, fingerprints);
            owner = Store.Hold(status.ProjectId, status.JobId);
            await Store.CreateAsync(record, snapshot, cancellation).ConfigureAwait(false);
            lock (gate) Schedule(snapshot, spec, id, record, owner);
            owner = null; // the worker owns the durable record until it publishes its terminal state
            return status;
        }
        finally { owner?.Dispose(); submission.Release(); }
    }

    public async Task<ExportStatus> RetryAsync(Guid project, Guid id, string? output, CancellationToken cancellation = default)
    {
        var record = ReadStored(project, id);
        if (Status(id).State is not ("interrupted" or "failed" or "cancelled"))
            throw new InvalidOperationException("Only interrupted, failed, or cancelled exports can be retried.");
        var captured = await Store.LoadSnapshotAsync(record, cancellation).ConfigureAwait(false);
        engine.CheckExportMedia(captured);
        CheckSources(captured, record, cancellation);
        var request = record.Specification with { Output = output ?? record.Specification.Output };
        engine.CheckExportOutput(request.Output);
        return await EnqueueAsync(captured, request, cancellation).ConfigureAwait(false);
    }

    public ExportStatus[] List()
    {
        var projectId = engine.Session.Snapshot.Document.Id;
        var persisted = Store.Entries(projectId).OrderByDescending(r => r.CreatedUtc).Select(r => Store.RecoverStatus(r)).ToArray();
        lock (gate)
        {
            var same = jobs.Values.Select(job => job.Status).Where(status => status.ProjectId == projectId).ToArray();
            return [.. same, .. persisted.Where(status => !same.Any(current => current.JobId == status.JobId))];
        }
    }

    public ExportStatus Status(Guid id)
    {
        Guid project = engine.Session.Snapshot.Document.Id;
        lock (gate) if (jobs.TryGetValue(id, out var job) && job.Status.ProjectId == project) return job.Status;
        return Store.RecoverStatus(ReadStored(project, id));
    }

    public ExportStatus[] ActiveJobs()
    {
        lock (gate) return jobs.Values.Select(j => j.Status).Where(s => s.State is "running" or "queued" or "checking").ToArray();
    }

    public void Cancel(Guid id)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var job) || job.Status.ProjectId != engine.Session.Snapshot.Document.Id)
                throw new KeyNotFoundException("Only the running owner's render queue can cancel this job.");
            job.Cancellation.Cancel();
        }
    }

    public async Task<ExportStatus> WaitAsync(Guid id, CancellationToken cancellation = default)
    {
        Task work;
        lock (gate) work = jobs.TryGetValue(id, out var job) ? job.Task : Task.CompletedTask;
        await work.WaitAsync(cancellation).ConfigureAwait(false);
        return Status(id);
    }

    public async Task<ExportLintReport> LintAsync(Guid id, CancellationToken cancellation)
    {
        Guid project = engine.Session.Snapshot.Document.Id;
        var record = ReadStored(project, id);
        var snapshot = await Store.LoadSnapshotAsync(record, cancellation).ConfigureAwait(false);
        var specification = record.Specification;
        engine.CheckExportOutput(specification.Output);
        return await ExportLint.InspectAsync(specification.Output, snapshot, specification.SequenceId, SelectProfile(snapshot, specification),
            ExportCatalog.Encoder(specification.Encoder), specification.Finish, specification.BurnCaptions, cancellation).ConfigureAwait(false);
    }

    private QueuedExport ReadStored(Guid project, Guid id)
    {
        try { return Store.Read(project, id); }
        catch (FileNotFoundException) { throw new KeyNotFoundException("Export job is unknown in this project."); }
        catch (DirectoryNotFoundException) { throw new KeyNotFoundException("Export job is unknown in this project."); }
    }

    private void CheckCapacity(string destination)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (jobs.Values.Count(j => !j.Task.IsCompleted) >= 4) throw new InvalidOperationException("Export queue is full (four jobs).");
        if (jobs.Values.Any(j => !j.Task.IsCompleted && string.Equals(j.Status.Output, destination, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This destination already has a queued export.");
    }

    private Job Schedule(ProjectSnapshot snapshot, RenderSpecification spec, Guid id, QueuedExport? durable, FileStream? owner)
    {
        CheckCapacity(spec.Output);
        while (jobs.Count >= 32)
        {
            var oldest = jobs.First(j => j.Value.Task.IsCompleted);
            oldest.Value.Cancellation.Dispose();
            jobs.Remove(oldest.Key);
        }
        var job = new Job(durable?.Status ?? new(id, snapshot.Document.Id, EngineJson.Exact(snapshot.Revision),
            tail.IsCompleted ? "running" : "queued", 0, 0, spec.Output, null, spec.Encoder));
        jobs.Add(id, job);
        var preceding = tail;
        // Submission order determines execution order, including when the worker is starting on another CPU.
        tail = job.Task = Task.Run(async () =>
        {
            using (owner)
            {
                await preceding.ConfigureAwait(false);
                var token = job.Cancellation.Token;
                var clock = Stopwatch.StartNew();
                try
                {
                    token.ThrowIfCancellationRequested();
                    engine.CheckExportOutput(spec.Output);
                    engine.CheckExportMedia(snapshot);
                    if (durable is not null) CheckSources(snapshot, durable, token);
                    var profile = SelectProfile(snapshot, spec);
                    var encoder = ExportCatalog.Encoder(spec.Encoder);
                    await Transition(job.Status with { State = "running" }).ConfigureAwait(false);
                    var exported = await SequenceExport.RunAsync(snapshot, spec.SequenceId, spec.Output, new ProgressSink(item =>
                    {
                        double? remaining = item.Frames < 4 ? null : clock.Elapsed.TotalSeconds / item.Frames * (item.TotalFrames - item.Frames);
                        lock (gate) job.Status = job.Status with { Frames = item.Frames, TotalFrames = item.TotalFrames, RemainingSeconds = remaining };
                    }), token, profile, overwrite: false, encoder: encoder, finish: spec.Finish, burnCaptions: spec.BurnCaptions,
                        quality: spec.Quality, attempt: durable is null ? null : id).ConfigureAwait(false);
                    ExportLintReport? report = null;
                    if (durable is not null)
                    {
                        await Transition(job.Status with { State = "checking", RemainingSeconds = null }).ConfigureAwait(false);
                        report = await ExportLint.InspectAsync(spec.Output, snapshot, spec.SequenceId, profile, encoder,
                            spec.Finish, spec.BurnCaptions, token).ConfigureAwait(false);
                    }
                    await Transition(job.Status with { State = report?.HasErrors == true ? "failed" : "completed",
                        Error = report?.HasErrors == true ? "Export verification found fatal discrepancies. See the lint report." : null,
                        RemainingSeconds = null, ElapsedSeconds = exported.ElapsedSeconds, Bytes = exported.Bytes, Lint = report }).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await PersistTerminal(job.Status with { State = job.Shutdown && durable is not null ? "interrupted" : "cancelled",
                        RemainingSeconds = null, Error = job.Shutdown && durable is not null ? "The owning session closed. Retry restarts this pinned export." : null }).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    await PersistTerminal(job.Status with { State = "failed", Error = error.Message, RemainingSeconds = null }).ConfigureAwait(false);
                }
            }

            async Task Transition(ExportStatus status)
            {
                if (durable is not null) await Store.WriteAsync(durable with { Status = status }).ConfigureAwait(false);
                lock (gate) job.Status = status;
            }

            async Task PersistTerminal(ExportStatus status)
            {
                try { await Transition(status).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    lock (gate) job.Status = status with { State = "failed", Error = $"{status.Error} Could not persist job state. {error.Message}" };
                }
            }
        });
        return job;
    }

    internal static OutputProfile? SelectProfile(ProjectSnapshot snapshot, RenderSpecification spec)
    {
        var sequence = new TimelineQueries(snapshot).Sequence(spec.SequenceId);
        OutputProfile? profile = spec.ProfileId is { } id
            ? sequence.Creator?.Profiles.FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException("Unknown output profile.")
            : null;
        return spec.Preset is null ? profile : spec.Preset.Apply(sequence, profile);
    }

    private static Dictionary<Guid, string> CaptureSources(ProjectSnapshot snapshot, RenderSpecification spec, CancellationToken token)
    {
        var sequence = new TimelineQueries(snapshot).Sequence(spec.SequenceId);
        var sources = sequence.Tracks.SelectMany(track => track.Clips).Where(clip => clip.Title is null)
            .Select(clip => clip.AssetId).ToHashSet();
        var fingerprints = new Dictionary<Guid, string>();
        foreach (var asset in snapshot.Document.Assets.Where(asset => sources.Contains(asset.Id)))
        {
            token.ThrowIfCancellationRequested();
            fingerprints[asset.Id] = AnalysisCache.Fingerprint(asset.SourcePath);
        }
        return fingerprints;
    }

    private static void CheckSources(ProjectSnapshot snapshot, QueuedExport record, CancellationToken token)
    {
        var current = CaptureSources(snapshot, record.Specification, token);
        if (current.Count != record.SourceFingerprints.Count || current.Any(p => !record.SourceFingerprints.TryGetValue(p.Key, out var old) || old != p.Value))
            throw new InvalidDataException("Referenced media changed since the export was queued. Relink or create a new render request.");
    }

    public async ValueTask DisposeAsync()
    {
        await submission.WaitAsync().ConfigureAwait(false);
        Job[] remaining;
        try
        {
            lock (gate)
            {
                disposed = true;
                remaining = [.. jobs.Values];
                foreach (var job in remaining)
                {
                    job.Shutdown = true;
                    job.Cancellation.Cancel();
                }
            }
        }
        finally { submission.Release(); }
        await Task.WhenAll(remaining.Select(j => j.Task)).ConfigureAwait(false);
        foreach (var job in remaining) job.Cancellation.Dispose();
        submission.Dispose();
    }

    private sealed class Job(ExportStatus status)
    {
        public ExportStatus Status = status;
        public bool Shutdown;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task = Task.CompletedTask;
    }

    private sealed class ProgressSink(Action<ExportProgress> update) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => update(value);
    }
}
