using System.Text.Json;

namespace Valencia.Analysis;

public sealed record CachedAnalysis(string Key, Guid AssetId, int StreamIndex, string Kind, string Algorithm, string Fingerprint, string ContentVersion);

public static class AnalysisDiscovery
{
    /// <summary>Metadata hints only. The normal cache read verifies checksum, identity and completion before use.</summary>
    public static IEnumerable<CachedAnalysis> Enumerate(AnalysisCache cache, CancellationToken cancellation)
    {
        if (!Directory.Exists(cache.DirectoryPath)) yield break;
        int scanned = 0;
        foreach (string path in Directory.EnumerateFiles(cache.DirectoryPath, "*.vca"))
        {
            cancellation.ThrowIfCancellationRequested();
            if (++scanned > 8192) throw new InvalidOperationException("Analysis discovery exceeds 8192 cache entries; provide explicit artifact keys.");
            var metadata = Read(path);
            if (metadata is not null) yield return metadata;
        }
    }

    public static CachedAnalysis? Read(string path)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.Length != 64 || !name.All(char.IsAsciiHexDigit)) return null;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> start = stackalloc byte[4096];
            int length = file.ReadAtLeast(start, start.Length, false);
            if (length <= 40 || !start[..8].SequenceEqual("VANLY001"u8)) return null;
            var json = new Utf8JsonReader(start[40..length], isFinalBlock: false, state: default);
            string? key = null, kind = null, algorithm = null, fingerprint = null;
            Guid asset = default;
            int stream = -1;
            while (json.Read())
            {
                if (json.TokenType != JsonTokenType.PropertyName || json.CurrentDepth != 1) continue;
                string? prop = json.GetString();
                if (prop is not ("key" or "kind" or "algorithm" or "fingerprint" or "assetId" or "streamIndex")) continue;
                if (!json.Read()) return null;
                switch (prop)
                {
                    case "key": key = json.GetString(); break;
                    case "kind": kind = json.GetString(); break;
                    case "algorithm": algorithm = json.GetString(); break;
                    case "fingerprint": fingerprint = json.GetString(); break;
                    case "assetId": asset = json.GetGuid(); break;
                    case "streamIndex": stream = json.GetInt32(); break;
                }
                if (key == name && kind is not null && algorithm is not null && fingerprint is not null && asset != Guid.Empty && stream >= 0)
                    return new(key, asset, stream, kind, algorithm, fingerprint, Convert.ToHexString(start[8..40]));
            }
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException or InvalidOperationException or UnauthorizedAccessException) { }
        return null;
    }
}
