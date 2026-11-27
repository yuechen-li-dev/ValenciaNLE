using Valencia.Core.Timeline;

namespace Valencia.Persistence;

public sealed record AutosaveStatus(long Revision, DateTimeOffset? CapturedUtc, string? Error);

/// <summary>The UI supplies committed snapshots. All serialization and publication run on a single worker.</summary>
public sealed class ProjectAutosave(string root, TimeSpan interval, TimeSpan debounce, TimeProvider? clock = null) : IDisposable
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private RecoveryRun? run;
    private Task<AutosaveStatus>? writing;
    private ProjectSnapshot? pending;
    private ProjectSnapshot? lastObserved;
    private string? originalPath;
    private DateTimeOffset changedAt;
    private DateTimeOffset firstPendingAt;
    private DateTimeOffset retryAt;

    public bool HasPendingWrite => writing is not null || pending is not null;

    public AutosaveStatus? Observe(ProjectSnapshot snapshot, bool dirty, string? projectPath)
    {
        var now = time.GetUtcNow();
        AutosaveStatus? status = null;
        if (writing?.IsCompleted == true)
        {
            status = writing.GetAwaiter().GetResult();
            writing = null;
            if (status.Error is not null && ReferenceEquals(snapshot, lastObserved) && pending is null)
            {
                pending = snapshot;
                retryAt = now + interval;
            }
        }
        if (!dirty)
        {
            pending = null;
            lastObserved = null;
            retryAt = default;
            return status;
        }
        if (!ReferenceEquals(snapshot, lastObserved))
        {
            if (pending is null) firstPendingAt = now;
            pending = lastObserved = snapshot;
            changedAt = now;
            retryAt = default;
        }
        originalPath = projectPath;
        if (pending is { } captured && writing is null && now >= retryAt &&
            (now - changedAt >= debounce || now - firstPendingAt >= interval))
        {
            string? path = originalPath;
            pending = null;
            writing = Task.Run(() => Capture(captured, path));
        }
        return status;
    }

    private async Task<AutosaveStatus> Capture(ProjectSnapshot snapshot, string? path)
    {
        try
        {
            run ??= new RecoveryRun(root);
            var entry = await run.CaptureAsync(snapshot, path).ConfigureAwait(false);
            return new(snapshot.Revision, entry.Metadata?.CapturedUtc, entry.Error);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(snapshot.Revision, null, exception.Message);
        }
    }

    // Only the application owner calls this during an explicit close or project switch.
    public void CloseCleanly()
    {
        if (writing is not null) writing.GetAwaiter().GetResult();
        run?.CloseCleanly();
        Dispose();
    }

    public void Dispose()
    {
        if (writing is not null) writing.GetAwaiter().GetResult();
        writing = null;
        run?.Dispose();
        run = null;
        pending = lastObserved = null;
    }
}
