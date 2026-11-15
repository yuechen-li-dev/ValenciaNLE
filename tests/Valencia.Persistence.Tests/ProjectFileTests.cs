using System.Collections.Immutable;
using Deliverance.Core;
using Deliverance.Core.Storage;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Xunit;
using static Valencia.Testing.TestProject;

namespace Valencia.Persistence.Tests;

public sealed class ProjectFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Valencia-M1a-tests", Guid.NewGuid().ToString("N"));
    private readonly ProjectFile files = new();
    public ProjectFileTests() => Directory.CreateDirectory(root);
    private string ProjectPath => Path.Combine(root, "edit.valencia");

    private EditingSession Session()
    {
        string source = Path.Combine(root, "media", "source.fixture");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "Headless metadata fixture, never sent to a decoder.");
        return Create(path: source);
    }

    [Fact]
    public async Task CreateEditUndoRedoSaveReopenEvaluateUsesRealDispatcherAndDeliverance()
    {
        var session = Session();
        Assert.True(Apply(session, new SplitClip(SequenceId, ClipId, new(SequenceTime.TicksPerSecond * 3 + 1), Id(8))).Success);
        Assert.True(Apply(session, new MoveClip(SequenceId, Id(8), Video2, T(5))).Success);
        var expected = session.Snapshot;
        Assert.True(session.Undo(Request(session)).Success);
        Assert.True(session.Redo(Request(session)).Success);
        Assert.True(DocumentSemantics.Equal(expected.Document, session.Snapshot.Document));
        var saved = session.Snapshot;
        await files.SaveAsync(ProjectPath, session);
        var loaded = await files.LoadAsync(ProjectPath);
        Assert.Empty(loaded.MissingMedia);
        Assert.True(DocumentSemantics.Equal(saved.Document, loaded.Snapshot.Document));
        Assert.Equal(saved.Revision, loaded.Snapshot.Revision);
        var reopened = new EditingSession(loaded.Snapshot);
        var active = Assert.Single(Query(reopened).Evaluate(SequenceId, T(6)));
        Assert.Equal(Id(8), active.Clip.Id);
        Assert.Equal(Video2, active.TrackId);
        Assert.Equal(90000, active.SourcePosition.Timestamp.Denominator);
        Assert.Equal(new SequenceTime(SequenceTime.TicksPerSecond * 4 + 1), active.SourcePosition.Since(S(0)));
        Assert.False(reopened.CanUndo); // history is session-local, not persisted
        var module = await Service().ExportChunkAsync("edit", ProjectFile.ModuleId);
        var disk = new ProjectJsonSerializer().Deserialize<ProjectSnapshot>(module);
        Assert.Equal("media/source.fixture", disk.Document.Assets[0].SourcePath);
        Assert.DoesNotContain("Frames", System.Text.Encoding.UTF8.GetString(module.Span));
        Assert.DoesNotContain("TotalSeconds", System.Text.Encoding.UTF8.GetString(module.Span));
    }

    [Fact]
    public async Task ExactLargeEpochAndOneTickOffsetsSurvivePersistence()
    {
        var session = Session();
        long value = long.MaxValue / 2;
        var timestamp = new MediaTimestamp(value, 1, 90001);
        var origin = new SourcePosition(timestamp, new(1));
        var asset = Asset(Path.Combine(root, "absent.fixture")) with
        {
            Id = Id(8), Streams = [new(0, TrackKind.Video, "fixture", 1, 90001)]
        };
        Assert.True(Apply(session, new AddAsset(asset)).Success);
        Assert.True(Apply(session, new InsertClip(SequenceId, Video2, Clip(Id(9)) with
        {
            AssetId = Id(8), SourceIn = origin, SourceOut = origin.Shift(T(10))
        })).Success);
        await files.SaveAsync(ProjectPath, session);
        var loaded = await files.LoadAsync(ProjectPath);
        Assert.True(DocumentSemantics.Equal(session.Snapshot.Document, loaded.Snapshot.Document));
        var clip = loaded.Snapshot.Document.Sequences[0].Tracks[1].Clips[0];
        Assert.Equal(timestamp, clip.SourceIn.Timestamp);
        Assert.Equal(1, clip.SourceIn.Offset.Ticks);
        Assert.Single(loaded.MissingMedia);
    }

    [Fact]
    public async Task RelocatedBundleResolvesRelativePathsAndMissingMediaIsReported()
    {
        var session = Session();
        await files.SaveAsync(ProjectPath, session);
        string relocated = Path.Combine(root, "relocated");
        Directory.CreateDirectory(Path.Combine(relocated, "media"));
        File.Copy(ProjectPath, Path.Combine(relocated, "edit.valencia"));
        File.Copy(session.Snapshot.Document.Assets[0].SourcePath, Path.Combine(relocated, "media", "source.fixture"));
        var loaded = await files.LoadAsync(Path.Combine(relocated, "edit.valencia"));
        Assert.Empty(loaded.MissingMedia);
        Assert.Equal(Path.Combine(relocated, "media", "source.fixture"), loaded.Snapshot.Document.Assets[0].SourcePath);
        File.Delete(Path.Combine(relocated, "media", "source.fixture"));
        var missing = Assert.Single((await files.LoadAsync(Path.Combine(relocated, "edit.valencia"))).MissingMedia);
        Assert.Equal(AssetId, missing.AssetId);
        Assert.Equal(loaded.Snapshot.Document.Assets[0].SourcePath, missing.ResolvedPath);
    }

    [Theory]
    [InlineData("module-schema")][InlineData("document-schema")][InlineData("negative-revision")]
    [InlineData("invalid-reference")][InlineData("corrupt")][InlineData("truncate")][InlineData("serializer")]
    [InlineData("missing-field")][InlineData("unknown-field")][InlineData("missing-project")]
    public async Task FailedLoadNeverReplacesCurrentSession(string fault)
    {
        var session = Session();
        await files.SaveAsync(ProjectPath, session);
        var before = session.Snapshot;
        if (fault == "missing-project") File.Delete(ProjectPath);
        else if (fault is "corrupt" or "truncate")
        {
            byte[] bytes = await File.ReadAllBytesAsync(ProjectPath);
            if (fault == "corrupt") bytes[^1] ^= 0x7f;
            else Array.Resize(ref bytes, bytes.Length / 2);
            await File.WriteAllBytesAsync(ProjectPath, bytes);
        }
        else
        {
            var invalid = before;
            if (fault == "document-schema") invalid = before with { Document = before.Document with { SchemaVersion = 2 } };
            if (fault == "negative-revision") invalid = before with { Revision = -1 };
            if (fault == "invalid-reference") invalid = before with { Document = before.Document with { Assets = [] } };
            byte[] payload = new ProjectJsonSerializer().Serialize(invalid);
            if (fault is "missing-field" or "unknown-field")
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(payload)!;
                if (fault == "missing-field") json["Document"]!.AsObject().Remove("Name");
                else json["Unexpected"] = true;
                payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(json);
            }
            await Service().SaveAsync("edit", new(new(ApplicationId: ProjectFile.ApplicationId, ApplicationSaveVersion: 1),
            [new(ProjectFile.ModuleId, fault == "module-schema" ? 2 : 1, Deliverance.Core.Modules.ModuleCriticality.Required,
                fault == "serializer" ? (byte)99 : (byte)42, 0, payload)]));
        }
        var failure = await Record.ExceptionAsync(() => files.LoadIntoAsync(ProjectPath, session, Request(session)));
        switch (fault)
        {
            case "module-schema": Assert.Equal(SaveDiagnosticCode.NewerModuleSchema, Assert.IsType<DeliveranceException>(failure).Code); break;
            case "corrupt": Assert.Equal(SaveDiagnosticCode.CorruptPayload, Assert.IsType<DeliveranceException>(failure).Code); break;
            case "truncate": Assert.Equal(SaveDiagnosticCode.InvalidContainer, Assert.IsType<DeliveranceException>(failure).Code); break;
            case "missing-field": case "unknown-field":
                Assert.Equal(SaveDiagnosticCode.SerializationFailed, Assert.IsType<DeliveranceException>(failure).Code); break;
            case "serializer": Assert.IsType<InvalidDataException>(failure); break;
            case "missing-project": Assert.IsType<FileNotFoundException>(failure); break;
            case "negative-revision": Assert.IsType<ArgumentOutOfRangeException>(failure); break;
            default: Assert.IsType<ArgumentException>(failure); break;
        }
        Assert.Same(before, session.Snapshot);
        Assert.True(session.CanUndo);
    }

    [Fact]
    public async Task SuccessfulReplacementAndStaleLoadHaveExplicitCommitSemantics()
    {
        var saved = Session();
        await files.SaveAsync(ProjectPath, saved);
        var session = new EditingSession(ProjectDocument.Create(Id(100), "Other"));
        var stale = Request(session);
        Assert.True(Apply(session, new AddAsset(Asset(saved.Snapshot.Document.Assets[0].SourcePath))).Success);
        var before = session.Snapshot;
        var rejected = await files.LoadIntoAsync(ProjectPath, session, stale);
        Assert.Equal(EditError.StaleRevision, rejected.Commit.Error);
        Assert.Same(before, session.Snapshot);
        var result = await files.LoadIntoAsync(ProjectPath, session, Request(session));
        Assert.True(result.Commit.Success);
        Assert.Equal(before.Revision + 1, result.Commit.Revision);
        Assert.True(DocumentSemantics.Equal(saved.Snapshot.Document, session.Snapshot.Document));
        Assert.False(session.CanUndo);
        Assert.False(session.CanRedo);
    }

    [Fact]
    public async Task PreCancelledSaveAndInvalidSnapshotLeavePreviousSaveByteIdentical()
    {
        var session = Session();
        await files.SaveAsync(ProjectPath, session);
        byte[] before = await File.ReadAllBytesAsync(ProjectPath);
        Assert.True(Apply(session, new DeleteClip(SequenceId, ClipId)).Success);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.SaveAsync(ProjectPath, session, cancelled.Token));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => files.SaveAsync(ProjectPath, session.Snapshot with
        {
            Document = session.Snapshot.Document with { Id = Guid.Empty }
        }));
        Assert.Equal(before, await File.ReadAllBytesAsync(ProjectPath));
        Assert.Single((await files.LoadAsync(ProjectPath)).Snapshot.Document.Sequences[0].Tracks[0].Clips);
        Assert.Empty(Directory.GetFiles(root, "*.tmp-*"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.LoadIntoAsync(ProjectPath, session, Request(session), cancelled.Token));
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task DeliverancePartialWriteCancellationOrFailurePreservesPreviousContainer(bool cancel)
    {
        var session = Session();
        await files.SaveAsync(ProjectPath, session);
        byte[] before = await File.ReadAllBytesAsync(ProjectPath);
        var store = new FileSaveStore(root, ProjectFile.Extension);
        using var cancellation = new CancellationTokenSource();
        using var source = new InterruptedStream(cancellation, cancel);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync("edit", source, 1, cancellation.Token));
        else await Assert.ThrowsAsync<DeliveranceException>(() => store.WriteAsync("edit", source, 1));
        Assert.True(source.ReadCount >= 2); // cancellation/failure occurs after temporary-file bytes have been written
        Assert.Equal(before, await File.ReadAllBytesAsync(ProjectPath));
        Assert.Empty(Directory.GetFiles(root, "*.tmp-*"));
        Assert.True(DocumentSemantics.Equal(session.Snapshot.Document, (await files.LoadAsync(ProjectPath)).Snapshot.Document));
    }

    [Fact]
    public async Task AtomicOverwriteProducesReopenableCurrentAndPreviousBackup()
    {
        var session = Session();
        var original = session.Snapshot.Document;
        await files.SaveAsync(ProjectPath, session);
        byte[] previous = await File.ReadAllBytesAsync(ProjectPath);
        Assert.True(Apply(session, new DeleteClip(SequenceId, ClipId)).Success);
        await files.SaveAsync(ProjectPath, session);
        Assert.Equal(previous, await File.ReadAllBytesAsync(ProjectPath + ".bak1"));
        Assert.True(DocumentSemantics.Equal(session.Snapshot.Document, (await files.LoadAsync(ProjectPath)).Snapshot.Document));
        string backup = Path.Combine(root, "previous.valencia");
        File.Copy(ProjectPath + ".bak1", backup);
        Assert.True(DocumentSemantics.Equal(original, (await files.LoadAsync(backup)).Snapshot.Document));
    }

    [Theory]
    [InlineData(17)][InlineData(103)][InlineData(2026)]
    public async Task SeededEditsRoundTripExactSemanticContent(int seed)
    {
        var session = Session();
        var random = new Random(seed);
        for (int i = 0; i < 40; i++)
        {
            var before = session.Snapshot;
            var result = Apply(session, new InsertClip(SequenceId, Video2, Clip(Id(100 + i), random.Next(40), 0, random.Next(1, 4))));
            Assert.Equal(before.Revision + (result.Success ? 1 : 0), result.Revision);
            if (!result.Success) Assert.Same(before, session.Snapshot);
        }
        await files.SaveAsync(ProjectPath, session);
        Assert.True(DocumentSemantics.Equal(session.Snapshot.Document, (await files.LoadAsync(ProjectPath)).Snapshot.Document));
    }

    private DeliveranceService Service() => new(new DeliveranceOptions
    {
        Store = new FileSaveStore(root, ProjectFile.Extension), Serializer = new ProjectJsonSerializer()
    });
    public void Dispose() => Directory.Delete(root, recursive: true); // root is exclusively created by this fixture

    private sealed class InterruptedStream(CancellationTokenSource cancellation, bool cancel) : Stream
    {
        public int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ReadCount++;
            if (ReadCount == 1) { buffer.Span.Fill(0x5a); return ValueTask.FromResult(buffer.Length); }
            if (!cancel) throw new IOException("Injected source read failure after partial write.");
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
