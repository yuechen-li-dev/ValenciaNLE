using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Valencia.Analysis;

public sealed record TranscriptionSettings(string Model = "tiny.en", string Language = "en", int ChunkSeconds = 30, int OverlapSeconds = 2, int Threads = 4, string Alignment = "native")
{
    public void Validate()
    {
        if (Model is not ("tiny.en" or "large-v3") || Alignment is not ("native" or "dtw") || Language.Length is < 2 or > 12 || !Language.All(c => char.IsAsciiLetter(c) || c == '-')
            || ChunkSeconds is < 5 or > 30 || OverlapSeconds is < 1 or > 5 || OverlapSeconds * 2 >= ChunkSeconds || Threads is < 1 or > 16)
            throw new ArgumentException("Invalid local transcription settings.");
        if (Model != "tiny.en" || Language != "en")
            throw new NotSupportedException("Aurelian Whisper currently supports the tiny.en English model.");
    }
}
public sealed record WhisperModel(string Id, string Revision, string Sha256, long Bytes, string Precision, string Path);
public sealed record TranscriptWord(Guid Id, string Text, SourceRange Range, long NativeStartTicks, long NativeEndTicks, double? DecoderProbability, bool TimingUsable = true,
    long? AlignedStartTicks = null, SourceRange? OriginalRange = null, string? OriginalText = null, long? DtwBoundaryTicks = null);
public sealed record TranscriptSegment(Guid Id, string Text, SourceRange Range, int FirstWord, int WordCount);
public sealed record TranscriptData(WhisperModel Model, string Language, string Provider, string Alignment, string Preprocessing,
    ImmutableArray<TranscriptSegment> Segments, ImmutableArray<TranscriptWord> Words, int CompletedChunks, int ReusedChunks);

public sealed record ModelAvailability(string Id, string Revision, string Sha256, long DownloadBytes, string Precision,
    bool SupportedByBackend, string State, string? Path);
public sealed record ModelDownloadProgress(long DownloadedBytes, long TotalBytes);

public static class WhisperModels
{
    public const string Revision = "5359861c739e955e79d9a303bcbc70fb988958b1";
    private sealed record Specification(string Id, long Bytes, string Hash, bool Supported);
    private static readonly Specification[] Catalog =
    [
        new("tiny.en", 77704715L, "921E4CF8686FDD993DCD081A5DA5B6C365BFDE1162E72B08D75AC75289920B1F", true),
        new("large-v3", 3095033483L, "64D182B440B98D5203C4F9BD541544D84C605196C4F7B845DFA11FB23594D1E2", false)
    ];
    private static readonly ConcurrentDictionary<string, string> verified = new();
    private static readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(2) };
    public static string ManagedDirectory => Path.Combine(Valencia.Core.ValenciaData.Root, "Models");

    public static ModelAvailability[] List()
    {
        return Catalog.Select(spec =>
        {
            string? path = Find(spec);
            string state = "not-installed";
            if (path is not null)
            {
                var file = new FileInfo(path);
                state = file.Length != spec.Bytes ? "incorrect-size" : verified.ContainsKey(Identity(file)) ? "verified" : "present";
            }
            return new ModelAvailability(spec.Id, Revision, spec.Hash, spec.Bytes, "GGML mixed F16/F32", spec.Supported, state, path);
        }).ToArray();
    }

    public static WhisperModel Resolve(string id)
    {
        var spec = Get(id);
        string path = Find(spec) ?? throw new FileNotFoundException($"Download {id} explicitly with 'models download {id}' or configure VALENCIA_MODEL_DIR.",
            Path.Combine(ManagedDirectory, FileName(spec)));
        var file = new FileInfo(path);
        if (file.Length != spec.Bytes) throw new InvalidDataException("Whisper model size mismatch.");
        _ = verified.GetOrAdd(Identity(file), _ =>
        {
            using var stream = file.OpenRead();
            string hash = Convert.ToHexString(SHA256.HashData(stream));
            if (hash != spec.Hash) throw new InvalidDataException("Whisper model SHA-256 mismatch.");
            return hash;
        });
        return new(id, Revision, spec.Hash, file.Length, "GGML mixed F16/F32", file.FullName);
    }

    public static async Task<WhisperModel> DownloadAsync(string id, IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellation = default)
    {
        var spec = Get(id);
        if (!spec.Supported) throw new NotSupportedException("The current transcription backend supports tiny.en. The large-v3 identity is retained for compatibility.");
        if (Find(spec) is not null)
        {
            try { return await Task.Run(() => Resolve(id), cancellation).ConfigureAwait(false); }
            catch (InvalidDataException) { /* Replace only the managed destination after successful verification. */ }
        }
        Directory.CreateDirectory(ManagedDirectory);
        string output = Path.Combine(ManagedDirectory, FileName(spec));
        using var lease = new FileStream(output + ".download-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string temporary = output + ".partial";
        try
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            try
            {
                using var response = await client.GetAsync($"https://huggingface.co/ggerganov/whisper.cpp/resolve/{Revision}/{FileName(spec)}",
                    HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length != spec.Bytes)
                    throw new InvalidDataException("Optional model server returned an unexpected file size.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total = 0, last = 0;
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    byte[] buffer = new byte[256 * 1024];
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > spec.Bytes) throw new InvalidDataException("Model response exceeded the pinned size.");
                        digest.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                        if (total - last >= 1024 * 1024 || total == spec.Bytes)
                        {
                            progress?.Report(new(total, spec.Bytes));
                            last = total;
                        }
                    }
                    if (total != spec.Bytes || Convert.ToHexString(digest.GetHashAndReset()) != spec.Hash)
                        throw new InvalidDataException("Optional model failed its size or SHA-256 check.");
                    file.Flush(flushToDisk: true);
                }
                cancellation.ThrowIfCancellationRequested();
                File.Move(temporary, output, overwrite: true);
                var published = new FileInfo(output);
                verified[Identity(published)] = spec.Hash;
                return new(spec.Id, Revision, spec.Hash, spec.Bytes, "GGML mixed F16/F32", output);
            }
            catch (HttpRequestException error)
            {
                throw new IOException("Could not connect to the pinned model server. Check connectivity and retry when online.", error);
            }
            catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
            {
                throw new IOException("The optional model server did not respond in time. Retry when online.", error);
            }
        }
        finally { File.Delete(temporary); }
    }

    public static bool RemoveManaged(string id)
    {
        var spec = Get(id);
        if (!Directory.Exists(ManagedDirectory)) return false;
        string path = Path.Combine(ManagedDirectory, FileName(spec));
        using var lease = new FileStream(path + ".download-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private static Specification Get(string id) => Catalog.FirstOrDefault(item => item.Id == id)
        ?? throw new ArgumentException("Unknown pinned Whisper model.", nameof(id));
    private static string FileName(Specification spec) => "ggml-" + spec.Id + ".bin";
    private static string Identity(FileInfo file) => $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
    private static string? Find(Specification spec)
    {
        string? configured = Environment.GetEnvironmentVariable("VALENCIA_MODEL_DIR");
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured))
        {
            string explicitPath = Path.Combine(configured, FileName(spec));
            if (File.Exists(explicitPath) && new FileInfo(explicitPath).Length == spec.Bytes) return explicitPath;
        }
        string managed = Path.Combine(ManagedDirectory, FileName(spec));
        if (File.Exists(managed)) return managed;
        string bundled = Path.Combine(AppContext.BaseDirectory, "models", FileName(spec));
        if (File.Exists(bundled)) return bundled;
        return !string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured) && File.Exists(Path.Combine(configured, FileName(spec)))
            ? Path.Combine(configured, FileName(spec)) : null;
    }
}
