using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Valencia.Core.Timeline;

namespace Valencia.Persistence;

public sealed record ArchivedProject(string Id, string File, SnapshotMetadata? Metadata, string? Integrity,
    int MissingMedia, string? Error);

/// <summary>A private run directory stays leased until process exit. Each published generation is an atomic Deliverance file.</summary>
public sealed class RecoveryRun : IDisposable
{
    private const int RetainedGenerations = 5;
    private readonly FileStream lease;
    private readonly string directory;
    private long generation;
    private bool closed;

    public RecoveryRun(string root)
    {
        Directory.CreateDirectory(root);
        string[] existing = Directory.GetDirectories(root);
        if (existing.Length >= 64)
            throw new IOException("The recovery area contains 64 sessions. Review or discard interrupted sessions before creating more.");
        directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        lease = ArchiveLease.Open(Path.Combine(directory, "active.lock"));
    }

    public async Task<ArchivedProject> CaptureAsync(ProjectSnapshot snapshot, string? originalPath, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(closed, this);
        long next = checked(generation + 1);
        var time = DateTimeOffset.UtcNow;
        string path = Path.Combine(directory, $"generation-{next:D12}.valencia");
        var metadata = new SnapshotMetadata("autosave", snapshot.Document.Id, snapshot.Revision,
            snapshot.Document.SchemaVersion, time, next, originalPath);
        await new ProjectFile().WriteAsync(path, snapshot, metadata, cancellation).ConfigureAwait(false);
        generation = next;
        foreach (string old in Directory.EnumerateFiles(directory, "generation-*.valencia").OrderDescending().Skip(RetainedGenerations))
            File.Delete(old);
        return await ReadAsync(path, "autosave", CancellationToken.None).ConfigureAwait(false);
    }

    public void CloseCleanly()
    {
        ObjectDisposedException.ThrowIf(closed, this);
        File.WriteAllText(Path.Combine(directory, "clean"), DateTimeOffset.UtcNow.ToString("O"));
        foreach (string generationFile in Directory.EnumerateFiles(directory, "generation-*.valencia"))
            File.Delete(generationFile);
        Dispose();
        RemoveClosedDirectory(directory);
    }

    public void Dispose()
    {
        if (closed) return;
        closed = true;
        lease.Dispose();
    }

    public static async Task<IReadOnlyList<ArchivedProject>> ScanAsync(string root, CancellationToken cancellation = default)
    {
        if (!Directory.Exists(root)) return [];
        var entries = new List<ArchivedProject>();
        foreach (string folder in Directory.EnumerateDirectories(root).Take(64))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!IsRun(folder)) continue;
            using var held = ArchiveLease.TryOpen(Path.Combine(folder, "active.lock"));
            if (held is null || File.Exists(Path.Combine(folder, "clean"))) continue;
            foreach (string file in Directory.EnumerateFiles(folder, "generation-*.valencia").OrderDescending().Take(RetainedGenerations))
                entries.Add(await ReadAsync(file, "autosave", cancellation).ConfigureAwait(false));
        }
        return entries.OrderByDescending(item => item.Metadata?.CapturedUtc).ThenByDescending(item => item.Id).ToArray();
    }

    public static async Task<LoadedProject> OpenAsync(string root, string id, CancellationToken cancellation = default)
    {
        string path = ResolveId(root, id);
        var loaded = await new ProjectFile().LoadAsync(path, cancellation).ConfigureAwait(false);
        if (loaded.Metadata?.Kind != "autosave") throw new InvalidDataException("The file is not an autosave generation.");
        return loaded;
    }

    public static void Discard(string root, string id)
    {
        string path = ResolveId(root, id);
        string folder = Path.GetDirectoryName(path)!;
        using (ArchiveLease.Open(Path.Combine(folder, "active.lock")))
        {
            File.Delete(path);
            if (Directory.EnumerateFiles(folder, "generation-*.valencia").Any()) return;
            File.WriteAllText(Path.Combine(folder, "clean"), DateTimeOffset.UtcNow.ToString("O"));
            foreach (string temporary in Directory.EnumerateFiles(folder, "generation-*.valencia.tmp-*"))
                File.Delete(temporary);
        }
        RemoveClosedDirectory(folder);
    }

    internal static async Task<ArchivedProject> ReadAsync(string file, string kind, CancellationToken cancellation)
    {
        var id = kind == "autosave" ? Path.GetFileName(Path.GetDirectoryName(file)) + "/" + Path.GetFileNameWithoutExtension(file)
            : Path.GetFileNameWithoutExtension(file);
        try
        {
            var entry = await new ProjectFile().LoadAsync(file, cancellation).ConfigureAwait(false);
            if (entry.Metadata?.Kind != kind) throw new InvalidDataException($"Expected a {kind} file.");
            return new(id, file, entry.Metadata, entry.Integrity, entry.MissingMedia.Length, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            return new(id, file, null, null, 0, error.Message);
        }
    }

    private static string ResolveId(string root, string id)
    {
        string[] parts = id.Split('/');
        const string prefix = "generation-";
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out _) ||
            !parts[1].StartsWith(prefix, StringComparison.Ordinal) ||
            !long.TryParse(parts[1].AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException("Use a recovery generation ID from the recovery list.", nameof(id));
        var folder = Path.Combine(root, parts[0]);
        if (!IsRun(folder)) throw new DirectoryNotFoundException("The recovery session is unavailable.");
        return Path.Combine(folder, parts[1] + ".valencia");
    }

    private static bool IsRun(string folder) => Guid.TryParseExact(Path.GetFileName(folder), "N", out _) &&
        Directory.Exists(folder) && !File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint);

    private static void RemoveClosedDirectory(string folder)
    {
        try
        {
            // A concurrent scanner may have opened this lock. A clean marker still distinguishes this directory.
            File.Delete(Path.Combine(folder, "active.lock"));
            File.Delete(Path.Combine(folder, "clean"));
            Directory.Delete(folder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class ArchiveLease
{
    public static FileStream Open(string path) => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    public static FileStream? TryOpen(string path)
    {
        try { return Open(path); }
        catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33 or 3) { return null; }
    }
}

public sealed class ProjectCheckpoints(string root)
{
    public async Task<ArchivedProject> CreateAsync(ProjectSnapshot snapshot, string name, string? originalPath,
        CancellationToken cancellation = default)
    {
        ValidateName(name);
        string directory = DirectoryFor(snapshot.Document.Id);
        Directory.CreateDirectory(directory);
        using var held = ArchiveLease.Open(Path.Combine(directory, "checkpoints.lock"));
        if (Directory.EnumerateFiles(directory, "*.valencia").Take(32).Count() >= 32)
            throw new IOException("The checkpoint limit is 32 per project. Delete one or save a separate project first.");
        string path = FileFor(snapshot.Document.Id, name);
        if (File.Exists(path)) throw new IOException("A checkpoint with that name already exists.");
        var metadata = new SnapshotMetadata("checkpoint", snapshot.Document.Id, snapshot.Revision, snapshot.Document.SchemaVersion,
            DateTimeOffset.UtcNow, snapshot.Revision, originalPath, name);
        await new ProjectFile().WriteAsync(path, snapshot, metadata, cancellation).ConfigureAwait(false);
        return await RecoveryRun.ReadAsync(path, "checkpoint", CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ArchivedProject>> ListAsync(Guid project, CancellationToken cancellation = default)
    {
        var entries = new List<ArchivedProject>();
        string directory = DirectoryFor(project);
        if (!Directory.Exists(directory)) return entries;
        foreach (string file in Directory.EnumerateFiles(directory, "*.valencia").Take(32))
            entries.Add(await RecoveryRun.ReadAsync(file, "checkpoint", cancellation).ConfigureAwait(false));
        return entries.OrderBy(item => item.Metadata?.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<LoadedProject> OpenAsync(Guid project, string name, CancellationToken cancellation = default)
    {
        string path = FileFor(project, name);
        var entry = await new ProjectFile().LoadAsync(path, cancellation).ConfigureAwait(false);
        if (entry.Metadata?.Kind != "checkpoint" || entry.Snapshot.Document.Id != project ||
            !string.Equals(entry.Metadata.Name, name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The checkpoint identity does not match this project and name.");
        return entry;
    }

    public void Delete(Guid project, string name)
    {
        string path = FileFor(project, name);
        using var held = ArchiveLease.Open(Path.Combine(DirectoryFor(project), "checkpoints.lock"));
        if (!File.Exists(path)) throw new FileNotFoundException("Checkpoint does not exist.");
        File.Delete(path);
    }

    private string DirectoryFor(Guid project) => Path.Combine(root, project.ToString("N"));

    private string FileFor(Guid project, string name)
    {
        ValidateName(name);
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant())));
        return Path.Combine(DirectoryFor(project), digest + ".valencia");
    }

    private static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 80 || name != name.Trim() || name.Any(char.IsControl))
            throw new ArgumentException("Checkpoint names must contain 1 to 80 printable characters.");
    }
}
