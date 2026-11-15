using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Valencia.Core.Timeline;

namespace Valencia.Analysis;

public static class AnalysisJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { IgnoreReadOnlyProperties = true, MaxDepth = 64 };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>Local regenerable cache: SHA-256 checked payload, same-directory atomic publication, LRU disk budget.</summary>
public sealed class AnalysisCache(string directory, long budgetBytes = 512 * 1024 * 1024)
{
    public const long MaximumArtifactBytes = 64 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public static string Fingerprint(string path)
    {
        var info = new FileInfo(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"quick-v1|{info.Length}|{info.LastWriteTimeUtc.Ticks}|"));
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] block = new byte[65536];
        foreach (long position in new[] { 0L, Math.Max(0, info.Length / 2 - 32768), Math.Max(0, info.Length - block.Length) })
        {
            file.Position = position;
            int total = 0, read;
            while (total < block.Length && (read = file.Read(block, total, block.Length - total)) > 0) total += read;
            hash.AppendData(block.AsSpan(0, total));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public static string Key(Guid asset, string fingerprint, int stream, string algorithm, string parameters) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"1|{asset}|{fingerprint}|{stream}|{algorithm}|{MediaAnalyzer.DecoderPolicy}|{parameters}")));

    private string PathFor(string key)
    {
        if (key.Length != 64 || key.Any(c => !char.IsAsciiHexDigit(c))) throw new ArgumentException("Invalid analysis key.");
        return Path.Combine(DirectoryPath, key + ".vca");
    }

    public async Task<AnalysisArtifact?> ReadAsync(string key, CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            string path = PathFor(key);
            if (!File.Exists(path)) return null;
            try
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, true);
                if (file.Length is < 40 or > MaximumArtifactBytes) throw new InvalidDataException("Invalid artifact length.");
                byte[] header = new byte[40];
                await file.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
                if (!header.AsSpan(0, 8).SequenceEqual("VANLY001"u8)) throw new InvalidDataException("Unknown artifact version.");
                byte[] checksum = await SHA256.HashDataAsync(file, cancellation).ConfigureAwait(false);
                if (!checksum.AsSpan().SequenceEqual(header.AsSpan(8))) throw new InvalidDataException("Artifact checksum mismatch.");
                file.Position = 40;
                var artifact = await JsonSerializer.DeserializeAsync<AnalysisArtifact>(file, AnalysisJson.Options, cancellation).ConfigureAwait(false);
                if (artifact is null || artifact.Key != key || artifact.SchemaVersion != 1 || artifact.Status != "complete")
                    throw new InvalidDataException("Invalid artifact identity/completion.");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return artifact;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or NotSupportedException)
            {
                File.Delete(path);
                return null;
            }
        }
        finally { gate.Release(); }
    }

    public async Task PublishAsync(AnalysisArtifact artifact, CancellationToken cancellation)
    {
        if (artifact.Status != "complete" || artifact.SchemaVersion != 1) throw new ArgumentException("Only completed versioned artifacts can be cached.");
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            await using var writerLease = await AcquireWriter(cancellation).ConfigureAwait(false);
            // Exclusive publication lease means these are abandoned by earlier processes,
            // never another active writer's temporary output.
            foreach (string abandoned in Directory.EnumerateFiles(DirectoryPath, ".analysis-*.partial")) File.Delete(abandoned);
            string destination = PathFor(artifact.Key);
            temporary = Path.Combine(DirectoryPath, ".analysis-" + Guid.NewGuid().ToString("N") + ".partial");
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true))
            {
                await file.WriteAsync(new byte[40], cancellation).ConfigureAwait(false);
                using (var bounded = new LimitedWriteStream(file, Math.Min(MaximumArtifactBytes, budgetBytes)))
                    await JsonSerializer.SerializeAsync(bounded, artifact, AnalysisJson.Options, cancellation).ConfigureAwait(false);
                if (file.Length > MaximumArtifactBytes || file.Length > budgetBytes) throw new NotSupportedException("Analysis artifact exceeds cache budget.");
                await file.FlushAsync(cancellation).ConfigureAwait(false);
                file.Position = 40;
                byte[] checksum = await SHA256.HashDataAsync(file, cancellation).ConfigureAwait(false);
                file.Position = 0;
                await file.WriteAsync("VANLY001"u8.ToArray(), cancellation).ConfigureAwait(false);
                await file.WriteAsync(checksum, cancellation).ConfigureAwait(false);
                file.Flush(true);
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
            temporary = null;
            long total = 0;
            foreach (var file in new DirectoryInfo(DirectoryPath).EnumerateFiles("*.vca").OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal))
            {
                total = checked(total + file.Length);
                if (total > budgetBytes) file.Delete();
            }
        }
        finally
        {
            if (temporary is not null) File.Delete(temporary);
            gate.Release();
        }
    }
    private async Task<FileStream> AcquireWriter(CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(DirectoryPath, ".write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
            {
                await Task.Delay(25, cancellation).ConfigureAwait(false);
            }
        }
    }
    private sealed class LimitedWriteStream(Stream output, long maximumLength) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => output.Length;
        public override long Position { get => output.Position; set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);
        private void Check(int count)
        {
            if (count > maximumLength - output.Position) throw new NotSupportedException("Analysis artifact exceeds cache budget.");
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            output.Write(buffer);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Check(buffer.Length);
            return output.WriteAsync(buffer, cancellationToken);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
