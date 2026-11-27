using System.Collections.Immutable;
using Deliverance.Core;
using Deliverance.Core.Codecs;
using Deliverance.Core.Modules;
using Deliverance.Core.Storage;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;

namespace Valencia.Persistence;

public sealed record MissingMedia(Guid AssetId, string ResolvedPath);
public sealed record LoadedProject(ProjectSnapshot Snapshot, ImmutableArray<MissingMedia> MissingMedia,
    SnapshotMetadata? Metadata = null, string? Integrity = null);
public sealed record SnapshotMetadata(string Kind, Guid ProjectId, long Revision, int ProjectSchema,
    DateTimeOffset CapturedUtc, long Generation, string? OriginalPath = null, string? Name = null);
public sealed record LoadCommitResult(LoadedProject Candidate, EditResult Commit);

/// <summary>Captures immutable snapshots. Deliverance alone owns temporary files, flushing and atomic replacement.</summary>
public sealed class ProjectFile
{
    public const string ModuleId = "valencia.project";
    private const string SnapshotModuleId = "valencia.snapshot";
    public const string ApplicationId = "ValenciaNLE";
    public const string Extension = ".valencia";
    private static readonly ProjectJsonSerializer Serializer = new();

    public Task SaveAsync(string path, EditingSession session, CancellationToken cancellation = default) =>
        SaveAsync(path, session.Snapshot, cancellation);

    public async Task SaveAsync(string path, ProjectSnapshot snapshot, CancellationToken cancellation = default)
        => await WriteAsync(path, snapshot, null, cancellation).ConfigureAwait(false);

    internal async Task WriteAsync(string path, ProjectSnapshot snapshot, SnapshotMetadata? metadata, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshot.Revision);
        DocumentValidator.Validate(snapshot.Document);
        if (snapshot.Document.Assets.Any(a => string.Equals(a.SourcePath, Path.GetFullPath(path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            throw new ArgumentException("Project save cannot replace source media.", nameof(path));
        var (service, slot, directory) = Open(path);
        var stored = snapshot with { Document = snapshot.Document with
        {
            Assets = [.. snapshot.Document.Assets.Select(a => a with
            {
                SourcePath = Path.GetRelativePath(directory, a.SourcePath).Replace(Path.DirectorySeparatorChar, '/')
            })]
        }};
        var module = SaveModulePayload.Create(ModuleId, ProjectDocument.CurrentSchemaVersion, ModuleCriticality.Required,
            Serializer, new NoCompressionCodec(), stored);
        SaveModulePayload[] modules = metadata is null ? [module] : [module,
            SaveModulePayload.Create(SnapshotModuleId, 1, ModuleCriticality.Optional, Serializer, new NoCompressionCodec(), metadata)];
        await service.SaveAsync(slot, new SaveRequest(new(ApplicationId: ApplicationId, ApplicationVersion: "M3b",
            ApplicationSaveVersion: ProjectDocument.CurrentSchemaVersion), modules), cancellation).ConfigureAwait(false);
    }

    /// <summary>Loading has no session side effects. Missing media is reported, never decoded or silently removed.</summary>
    public async Task<LoadedProject> LoadAsync(string path, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var (service, slot, directory) = Open(path);
        var loaded = await service.LoadAsync(slot,
            [new SaveModuleDefinition(ModuleId, ProjectDocument.CurrentSchemaVersion, ModuleCriticality.Required),
             new SaveModuleDefinition(SnapshotModuleId, 1, ModuleCriticality.Optional)],
            new LoadCompatibility(ApplicationId: ApplicationId, ApplicationSaveVersion: ProjectDocument.CurrentSchemaVersion),
            cancellation).ConfigureAwait(false);
        if (loaded.GetModule(ModuleId).SerializerId != Serializer.Id) throw new InvalidDataException("Unsupported project serializer.");
        var stored = loaded.Deserialize<ProjectSnapshot>(ModuleId, service.Options.Serializers);
        if (stored.Document is null || stored.Document.Assets.IsDefault) throw new InvalidDataException("Missing project document/assets.");
        var snapshot = stored with { Document = stored.Document with
        {
            Assets = [.. stored.Document.Assets.Select(a => a is null ? throw new InvalidDataException("Null asset.") : a with
            {
                SourcePath = ResolvePath(directory, a.SourcePath)
            })]
        }};
        ArgumentOutOfRangeException.ThrowIfNegative(snapshot.Revision);
        // Projects saved while titles were a sequence layer open with those titles as clips on new tracks.
        snapshot = snapshot with { Document = TitleMigration.Upgrade(snapshot.Document) };
        DocumentValidator.Validate(snapshot.Document);
        var missing = ImmutableArray.CreateBuilder<MissingMedia>();
        foreach (var asset in snapshot.Document.Assets)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!File.Exists(asset.SourcePath)) missing.Add(new(asset.Id, asset.SourcePath));
        }
        cancellation.ThrowIfCancellationRequested();
        SnapshotMetadata? metadata = loaded.Modules.Any(module => module.Id == SnapshotModuleId)
            ? loaded.Deserialize<SnapshotMetadata>(SnapshotModuleId, service.Options.Serializers) : null;
        if (metadata is not null && (metadata.ProjectId != snapshot.Document.Id || metadata.Revision != snapshot.Revision ||
            metadata.ProjectSchema != snapshot.Document.SchemaVersion || metadata.Generation < 0 ||
            metadata.Kind is not ("autosave" or "checkpoint" or "export")))
            throw new InvalidDataException("Stored snapshot metadata does not match the project.");
        return new(snapshot, missing.ToImmutable(), metadata, loaded.GetModule(ModuleId).SemanticHash);
    }

    /// <summary>Concurrent editing wins: a stale expected revision rejects replacement after asynchronous loading.</summary>
    public async Task<LoadCommitResult> LoadIntoAsync(string path, EditingSession session, SessionRequest request,
        CancellationToken cancellation = default)
    {
        var candidate = await LoadAsync(path, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return new(candidate, session.ReplaceProject(candidate.Snapshot, request));
    }

    private static string ResolvePath(string directory, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Missing media reference path.");
        return Path.GetFullPath(path, directory);
    }

    private static (DeliveranceService Service, string Slot, string Directory) Open(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), Extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Project filenames must use {Extension}.", nameof(path));
        string directory = Path.GetDirectoryName(fullPath)!;
        string slot = Path.GetFileNameWithoutExtension(fullPath);
        var store = new FileSaveStore(directory, Path.GetExtension(fullPath));
        // FileSaveStore sanitizes slot names; never silently save to a different filename.
        if (!string.Equals(store.GetPathForSlot(slot), fullPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Project filename cannot be represented by the save store.", nameof(path));
        return (new(new DeliveranceOptions { Store = store, Serializer = Serializer, BackupCopiesToKeep = 1 }), slot, directory);
    }
}
