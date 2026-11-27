using System.Security.Cryptography;
using Valencia.Core.Timeline;

namespace Valencia.Persistence;

public sealed record ProjectDiskVersion(string Path, string Sha256);
public sealed class ProjectConflictException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>Serializes cooperating writers, including desktop sessions holding a file open for edits.</summary>
public sealed class ProjectWriteLease : IDisposable
{
    private readonly SemaphoreSlim writer = new(1);
    private readonly FileStream ownership;

    private ProjectWriteLease(string path, FileStream ownership)
    {
        Path = path;
        this.ownership = ownership;
    }

    public string Path { get; }

    public static ProjectWriteLease Acquire(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        try
        {
            string lockPath = full + ".write-lock";
            if (File.Exists(lockPath) && File.GetAttributes(lockPath).HasFlag(FileAttributes.ReparsePoint))
                throw new ProjectConflictException("The project write-lock path is a link. Save As to a normal directory.");
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new(full, stream);
        }
        catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
        {
            throw new ProjectConflictException("Another Valencia session owns this project for writing. Use Save As, or close the other session and reopen the project.", error);
        }
    }

    public bool Matches(string path) => string.Equals(Path, System.IO.Path.GetFullPath(path),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static async Task<(LoadedProject Project, ProjectDiskVersion Version)> OpenAsync(string path, CancellationToken cancellation = default)
    {
        string full = System.IO.Path.GetFullPath(path);
        var before = await FingerprintAsync(full, cancellation).ConfigureAwait(false);
        var loaded = await new ProjectFile().LoadAsync(full, cancellation).ConfigureAwait(false);
        var after = await FingerprintAsync(full, cancellation).ConfigureAwait(false);
        if (before is null || before != after)
            throw new ProjectConflictException("The project changed while it was opening. Reopen it to establish the current saved revision.");
        return (loaded, new(full, before));
    }

    public async Task<ProjectDiskVersion> SaveAsync(ProjectSnapshot snapshot, ProjectDiskVersion? expected,
        CancellationToken cancellation = default)
    {
        await writer.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (expected is not null && !Matches(expected.Path)) throw new ArgumentException("The saved version belongs to another path.");
            string? actual = await FingerprintAsync(Path, cancellation).ConfigureAwait(false);
            if (actual != expected?.Sha256)
                throw new ProjectConflictException("The destination differs from the saved version this session opened. Reopen it or choose a new Save As path.");
            await new ProjectFile().SaveAsync(Path, snapshot, cancellation).ConfigureAwait(false);
            // A save that already published must establish its new version even if the caller has just cancelled.
            string published = (await FingerprintAsync(Path, CancellationToken.None).ConfigureAwait(false))
                ?? throw new IOException("The saved file disappeared after publication.");
            return new(Path, published);
        }
        finally { writer.Release(); }
    }

    private static async Task<string?> FingerprintAsync(string path, CancellationToken cancellation)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false));
        }
        catch (FileNotFoundException) { return null; }
    }

    public void Dispose()
    {
        ownership.Dispose();
        writer.Dispose();
    }
}
