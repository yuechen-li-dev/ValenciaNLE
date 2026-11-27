using Valencia.Core.Timeline;
using Valencia.Persistence;
using Xunit;
using static Valencia.Testing.TestProject;

namespace Valencia.Persistence.Tests;

public sealed class ProductPersistenceTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("Valencia-M3b-").FullName;

    [Fact]
    public async Task ProjectLeaseRejectsStaleAndIndependentWriters()
    {
        string path = Path.Combine(directory, "primary.valencia");
        var revision = Create().Snapshot;
        await new ProjectFile().SaveAsync(path, revision);
        var (_, first) = await ProjectWriteLease.OpenAsync(path);
        using (var writer = ProjectWriteLease.Acquire(path))
        {
            Assert.Throws<ProjectConflictException>(() => ProjectWriteLease.Acquire(path));
            var published = await writer.SaveAsync(revision with { Revision = revision.Revision + 1 }, first);
            Assert.NotEqual(first.Sha256, published.Sha256);
        }
        using var other = ProjectWriteLease.Acquire(path);
        await Assert.ThrowsAsync<ProjectConflictException>(() => other.SaveAsync(revision, first));
        await Assert.ThrowsAsync<ProjectConflictException>(() => other.SaveAsync(revision, expected: null));
        Assert.Equal(revision.Revision + 1, (await new ProjectFile().LoadAsync(path)).Snapshot.Revision);
    }

    [Fact]
    public async Task RecoveryValidatesMetadataFallsBackAndPreservesAnUnnamedDraft()
    {
        string root = Path.Combine(directory, "Recovery");
        var snapshot = Create().Snapshot;
        ArchivedProject previous;
        ArchivedProject latest;
        using (var run = new RecoveryRun(root))
        {
            previous = await run.CaptureAsync(snapshot, null);
            latest = await run.CaptureAsync(snapshot with { Revision = snapshot.Revision + 1 }, null);
            Assert.Empty(await RecoveryRun.ScanAsync(root));
            Assert.NotNull(latest.Integrity);
        }
        await File.WriteAllBytesAsync(latest.File, [1, 2, 3]);
        var found = await RecoveryRun.ScanAsync(root);
        Assert.Equal(2, found.Count);
        Assert.Contains(found, item => item.Id == latest.Id && item.Error is not null);
        var restored = await RecoveryRun.OpenAsync(root, previous.Id);
        Assert.Null(restored.Metadata?.OriginalPath);
        Assert.Equal(snapshot.Revision, restored.Snapshot.Revision);
        Assert.Equal(snapshot.Document.Id, restored.Metadata?.ProjectId);
        Assert.Single(restored.MissingMedia);
        Assert.True(DocumentSemantics.Equal(restored.Snapshot.Document, snapshot.Document));
        RecoveryRun.Discard(root, latest.Id);
        Assert.Single(await RecoveryRun.ScanAsync(root));
    }

    [Fact]
    public async Task NamedCheckpointPersistsAndRecoveryGenerationsAreBounded()
    {
        string root = Path.Combine(directory, "Recovery");
        var snapshot = Create().Snapshot;
        using (var run = new RecoveryRun(root))
        {
            for (int revision = 0; revision < 8; revision++)
                await run.CaptureAsync(snapshot with { Revision = revision }, null);
        }
        var recent = await RecoveryRun.ScanAsync(root);
        Assert.Equal(5, recent.Count);
        Assert.Equal(7, recent[0].Metadata?.Revision);
        string checkpoints = Path.Combine(directory, "Checkpoints");
        var store = new ProjectCheckpoints(checkpoints);
        await store.CreateAsync(snapshot, "First Cut", null);
        await Assert.ThrowsAsync<IOException>(() => store.CreateAsync(snapshot, "first cut", null));
        var reopened = new ProjectCheckpoints(checkpoints);
        var saved = await reopened.OpenAsync(snapshot.Document.Id, "FIRST CUT");
        Assert.Equal("First Cut", saved.Metadata?.Name);
        Assert.True(DocumentSemantics.Equal(saved.Snapshot.Document, snapshot.Document));
        reopened.Delete(snapshot.Document.Id, "First Cut");
        Assert.Empty(await reopened.ListAsync(snapshot.Document.Id));
    }

    [Fact]
    public async Task AutosaveDebouncesAndCapturesOnlyTheImmutableScheduledRevision()
    {
        string root = Path.Combine(directory, "Recovery");
        var clock = new ManualClock();
        var snapshot = Create().Snapshot;
        var autosave = new ProjectAutosave(root, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), clock);
        autosave.Observe(snapshot, true, null);
        clock.Advance(1);
        var second = snapshot with { Revision = snapshot.Revision + 1 };
        autosave.Observe(second, true, null);
        Assert.False(Directory.Exists(root));
        clock.Advance(2);
        autosave.Observe(second, true, null);
        var next = second with { Revision = second.Revision + 1 };
        autosave.Observe(next, true, null);
        autosave.Dispose();
        var captured = Assert.Single(await RecoveryRun.ScanAsync(root));
        Assert.Equal(second.Revision, captured.Metadata?.Revision);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
