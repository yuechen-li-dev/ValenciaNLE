using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Valencia.Analysis;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Media;
using Valencia.Obs;

namespace Valencia.Engine;

public sealed record ObsConnectRequest(string RecordingRoot, string Manifest, string? Endpoint = null);

public sealed record RecordingImportResult(bool Success, bool AlreadyImported, string Revision, RecordingManifest Manifest,
    ImmutableArray<Guid> AssetIds, ImmutableArray<string> Diagnostics);

public sealed partial class EditingEngine
{
    public ObsWorkflow? Obs { get; private set; }

    public async Task<ObsWorkflow> ConnectObsAsync(ObsConnectionOptions options, string? password, string manifestPath,
        string recordingRoot, bool allowRecordingControl = false, CancellationToken cancellation = default)
    {
        if (Obs is not null) throw new InvalidOperationException("Disconnect the current OBS scope before connecting another.");
        var workflow = new ObsWorkflow(this, options, password, OutputPath(manifestPath), OutputPath(recordingRoot), allowRecordingControl);
        try
        {
            await workflow.Recording.ConnectAsync(cancellation).ConfigureAwait(false);
            Obs = workflow;
            return workflow;
        }
        catch { await workflow.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public async Task DisconnectObsAsync()
    {
        if (Obs is not { } workflow) return;
        Obs = null;
        await workflow.DisposeAsync().ConfigureAwait(false);
    }

    public async Task<RecordingImportResult> ImportRecordingAsync(RecordingManifest manifest, string recordingRoot,
        long expectedRevision, CancellationToken cancellation = default)
    {
        ObsManifestFile.Validate(manifest);
        if (manifest.Files.IsEmpty || manifest.Files.Any(f => !f.Completed))
            throw new ArgumentException("Only completion-proven recording files can be ingested. A changing button or stable size is not completion proof.");
        string root = Path.TrimEndingDirectorySeparator(Policy is not null && Path.IsPathFullyQualified(recordingRoot)
            ? Policy.Check(recordingRoot) : OutputPath(recordingRoot));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var imports = new List<ImportedAsset>();
        var files = manifest.Files.ToBuilder();
        for (int index = 0; index < files.Count; index++)
        {
            string path = Path.GetFullPath(files[index].Path);
            // Completion events are external input, not unrestricted filesystem authority.
            new WorkspacePolicy(root).Check(path);
            Policy?.Check(path);
            if (Path.GetExtension(path).ToLowerInvariant() is not (".mkv" or ".mp4")) throw new NotSupportedException("OBS ingestion currently accepts completed MKV/MP4 files.");
            await WaitForFinalizedFileAsync(path, cancellation).ConfigureAwait(false);
            string fingerprint = AnalysisCache.Fingerprint(path);
            var identity = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("obs-source-v1|" + path.ToUpperInvariant() + "|" + fingerprint))[..16]);
            var imported = await Task.Run(() => MediaAssetProbe.Import(identity, path), cancellation).ConfigureAwait(false);
            imports.Add(imported);
            files[index] = files[index] with { AssetId = identity, Fingerprint = fingerprint, DurationMs = (decimal)imported.Duration.Ticks * 1000 / Valencia.Core.SequenceTime.TicksPerSecond,
                Streams = JsonSerializer.SerializeToElement(imported.Asset.Streams, EngineJson.Options) };
        }
        var existing = Session.Snapshot.Document.Assets;
        foreach (var source in imports)
            if (existing.Any(a => string.Equals(a.SourcePath, source.Asset.SourcePath, StringComparison.OrdinalIgnoreCase) && a.Id != source.Asset.Id))
                throw new InvalidOperationException("This recording path is already registered with a different source identity; resolve it explicitly rather than replacing media silently.");
        var added = imports.Where(i => !existing.Any(a => a.Id == i.Asset.Id)).DistinctBy(i => i.Asset.Id).ToArray();
        if (expectedRevision != Session.Snapshot.Revision) throw new ArgumentException("Project revision is stale.");
        if (added.Length != 0)
        {
            var result = Execute(new EditTransaction(Guid.NewGuid(), 1, expectedRevision,
                [.. added.Select(i => new EditCommand(Guid.NewGuid(), 1, new AddAsset(i.Asset)))]), cancellation);
            if (!result.Success) throw new InvalidOperationException(result.Message);
        }
        return new(true, added.Length == 0, EngineJson.Exact(Session.Snapshot.Revision), manifest with { Files = files.ToImmutable() },
            [.. imports.Select(i => i.Asset.Id).Distinct()], []);
    }

    private static async Task WaitForFinalizedFileAsync(string path, CancellationToken cancellation)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        long priorLength = -1;
        DateTime priorWrite = default;
        int stable = 0;
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                // Deny sharing with an existing writer, while permitting ordinary readers.
                using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length > 0 && file.Length == priorLength && info.LastWriteTimeUtc == priorWrite) stable++;
                else stable = 0;
                priorLength = file.Length;
                priorWrite = info.LastWriteTimeUtc;
                if (stable >= 2) return;
            }
            catch (IOException) { stable = 0; }
            await Task.Delay(250, cancellation).ConfigureAwait(false);
        }
        throw new IOException("Completed OBS file did not become stable/readable within 15 seconds: " + Path.GetFileName(path));
    }
}

public sealed class ObsWorkflow : IAsyncDisposable
{
    private readonly EditingEngine engine;
    private readonly bool allowControl;
    public string ManifestPath { get; }
    public string RecordingRoot { get; }
    public ObsRecordingSession Recording { get; }

    public ObsWorkflow(EditingEngine engine, ObsConnectionOptions options, string? password, string manifestPath, string recordingRoot, bool allowControl)
    {
        this.engine = engine;
        this.allowControl = allowControl;
        ManifestPath = manifestPath;
        RecordingRoot = recordingRoot;
        Recording = new(new ObsClient(options, password), manifestPath);
    }
    public Task<ObsDiscovery> DiscoverAsync(CancellationToken cancellation = default) => ObsDiscoveryService.ReadAsync(Recording.Client, cancellation);
    public async Task StartAsync(CancellationToken cancellation = default)
    {
        RequireControl();
        var directory = await Recording.Client.RequestAsync("GetRecordDirectory", cancellation: cancellation).ConfigureAwait(false);
        string actual = directory.GetProperty("recordDirectory").GetString()!;
        if (!Directory.Exists(actual)) throw new DirectoryNotFoundException("OBS recording directory is not accessible.");
        new WorkspacePolicy(RecordingRoot).Check(Path.Combine(actual, ".valencia-directory-boundary"));
        await Recording.StartAsync(cancellation).ConfigureAwait(false);
    }
    public Task StopAsync(CancellationToken cancellation = default) { RequireControl(); return Recording.StopAsync(cancellation); }
    public Task PauseAsync(bool pause, CancellationToken cancellation = default) { RequireControl(); return Recording.PauseAsync(pause, cancellation); }
    public Task<RecordingMarker> MarkAsync(string note, string kind = "highlight", CancellationToken cancellation = default)
    {
        RequireControl();
        return Recording.MarkAsync(note, kind, cancellation);
    }
    private void RequireControl()
    {
        if (!allowControl) throw new UnauthorizedAccessException("This OBS scope is read-only. Recording/marker control requires explicit local authorization.");
    }
    public Task<RecordingImportResult> ImportAsync(long expectedRevision, CancellationToken cancellation = default)
        => ImportIntoAsync(engine, expectedRevision, cancellation);

    public async Task<RecordingImportResult> ImportIntoAsync(EditingEngine destination, long expectedRevision, CancellationToken cancellation = default)
    {
        var status = await ObsDiscoveryService.RecordStatusAsync(Recording.Client, cancellation).ConfigureAwait(false);
        // OBS 32's frontend active flag can lag its output STOPPED event by a UI turn.
        // Completion proof is still mandatory; only then tolerate a bounded stale status window.
        for (int retry = 0; status.Active && Recording.Snapshot.State == "stopped" && retry < 40; retry++)
        {
            await Task.Delay(50, cancellation).ConfigureAwait(false);
            status = await ObsDiscoveryService.RecordStatusAsync(Recording.Client, cancellation).ConfigureAwait(false);
        }
        if (status.Active) throw new InvalidOperationException("OBS is still recording; live-growing file ingestion is not supported.");
        var result = await destination.ImportRecordingAsync(Recording.Snapshot, RecordingRoot, expectedRevision, cancellation).ConfigureAwait(false);
        await Recording.AttachMediaMetadataAsync(result.Manifest.Files, cancellation).ConfigureAwait(false);
        return result with { Manifest = Recording.Snapshot };
    }

    public static ImmutableArray<SourceMarker> PlannerMarkers(RecordingManifest manifest, Guid assetId, bool allowApproximate)
    {
        var file = manifest.Files.First(f => f.AssetId == assetId);
        if (file.Kind == RecordingFileKind.Replay)
            throw new NotSupportedException("Recording-clock markers cannot be mapped to a replay buffer without an explicit replay start anchor.");
        if (!allowApproximate && manifest.Markers.Length != 0) throw new ArgumentException("Recording markers have observational timing uncertainty. Explicit approximate-marker policy or manual refinement is required.");
        decimal offset = 0;
        foreach (var part in manifest.Files)
        {
            if (part.AssetId == assetId) break;
            if (part.Kind == RecordingFileKind.Recording) offset += part.DurationMs ?? throw new ArgumentException("Split-file metadata is incomplete.");
        }
        decimal duration = file.DurationMs ?? throw new ArgumentException("Ingest/probe recording files before mapping markers.");
        return [.. manifest.Markers.Where(m => m.ContinuousObservation && m.Kind is "highlight" or "explanation" or "chapter")
            .Select(m => (Marker: m, At: m.OutputDurationMs - offset)).Where(m => m.At >= 0 && m.At < duration)
            .Select(m => new SourceMarker(Ticks(Math.Max(0, m.At - 2000)), Ticks(Math.Min(duration, m.At + 4000)), "Approximate recording " + m.Marker.Kind + ": " + m.Marker.Note))];
        static string Ticks(decimal ms) => EngineJson.Exact(checked((long)decimal.Round(ms * (Valencia.Core.SequenceTime.TicksPerSecond / 1000), 0, MidpointRounding.AwayFromZero)));
    }
    public ValueTask DisposeAsync() => Recording.DisposeAsync();
}
