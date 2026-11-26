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

public static class WhisperModels
{
    public const string Revision = "5359861c739e955e79d9a303bcbc70fb988958b1";
    private static readonly ConcurrentDictionary<string, WhisperModel> verified = new();
    public static WhisperModel Resolve(string id)
    {
        var specification = id switch
        {
            "tiny.en" => (Bytes: 77704715L, Hash: "921E4CF8686FDD993DCD081A5DA5B6C365BFDE1162E72B08D75AC75289920B1F"),
            "large-v3" => (Bytes: 3095033483L, Hash: "64D182B440B98D5203C4F9BD541544D84C605196C4F7B845DFA11FB23594D1E2"),
            _ => throw new ArgumentException("Unknown configured Whisper model.")
        };
        string root = Environment.GetEnvironmentVariable("VALENCIA_MODEL_DIR") ?? Path.Combine(AppContext.BaseDirectory, "models");
        string path = Path.GetFullPath(Path.Combine(root, "ggml-" + id + ".bin"));
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Configure VALENCIA_MODEL_DIR with the pinned model; automatic model downloads are disabled.", path);
        if (info.Length != specification.Bytes) throw new InvalidDataException("Whisper model size mismatch.");
        string identity = path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        return verified.GetOrAdd(identity, _ =>
        {
            using var file = File.OpenRead(path);
            string hash = Convert.ToHexString(SHA256.HashData(file));
            if (hash != specification.Hash) throw new InvalidDataException("Whisper model SHA-256 mismatch.");
            return new(id, Revision, hash, info.Length, "GGML mixed F16/F32", path);
        });
    }
}
