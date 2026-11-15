using System.Text.Json;
using Copeland.TS.Tson;
using Valencia.Analysis;

namespace Valencia.Engine;

public static class AnalysisContracts
{
    public static TsonCatalog Catalog { get; } = Load();
    private static TsonCatalog Load()
    {
        using var input = typeof(AnalysisContracts).Assembly.GetManifestResourceStream("Valencia.Engine.Contracts.analysis.obj.ts")!;
        using var reader = new StreamReader(input);
        var result = TypeProfileCompiler.Compile(reader.ReadToEnd(), "analysis.obj.ts", "copeland://valencia/analysis/v1");
        return result.Catalog ?? throw new InvalidOperationException(string.Join("\n", result.Diagnostics));
    }
    public static T Parse<T>(string source, string rootType, string? path = null)
    {
        if (source.Length > 1_048_576) throw new ArgumentException("Analysis request exceeds source limit.");
        JsonElement value;
        if (path?.EndsWith(".obj.ts", StringComparison.OrdinalIgnoreCase) == true)
            value = TypeProfileCompiler.ReadTypedValue(source, path, Catalog, rootType);
        else
        {
            using var json = JsonDocument.Parse(source);
            value = json.RootElement.Clone();
        }
        var diagnostics = TypeProfileCompiler.Validate(Catalog, rootType, value);
        if (diagnostics.Count != 0) throw new ArgumentException(string.Join("\n", diagnostics));
        return EngineJson.Read<T>(value.GetRawText());
    }
    public static object Page(AnalysisArtifact artifact, string kind = "summary", int offset = 0, int limit = 64, int level = 0)
    {
        if (offset < 0 || limit is < 1 or > 128) throw new ArgumentException("Analysis page requires offset >= 0, limit 1..128.");
        object records;
        int total;
        switch (kind)
        {
            case "words": total = artifact.Transcript?.Words.Length ?? 0; records = artifact.Transcript?.Words.Skip(offset).Take(limit).ToArray() ?? []; break;
            case "segments": total = artifact.Transcript?.Segments.Length ?? 0; records = artifact.Transcript?.Segments.Skip(offset).Take(limit).ToArray() ?? []; break;
            case "silence": total = artifact.Silence.Length; records = artifact.Silence.Skip(offset).Take(limit).ToArray(); break;
            case "activity": total = artifact.Activity.Length; records = artifact.Activity.Skip(offset).Take(limit).ToArray(); break;
            case "scenes": total = artifact.Scenes.Length; records = artifact.Scenes.Skip(offset).Take(limit).ToArray(); break;
            case "waveform":
                if (level < 0 || level >= artifact.Waveform.Length) throw new ArgumentException("Invalid waveform level.");
                total = artifact.Waveform[level].Bins.Length;
                records = artifact.Waveform[level].Bins.Skip(offset).Take(limit).ToArray();
                break;
            case "loudness": total = artifact.Loudness is null ? 0 : 1; records = artifact.Loudness is null ? [] : new[] { artifact.Loudness }; break;
            case "summary": total = 0; records = Array.Empty<object>(); break;
            default: throw new ArgumentException("Unknown analysis result kind.");
        }
        return new { apiVersion = 1, artifact.Key, artifact.AssetId, artifact.StreamIndex, artifactKind = artifact.Kind, artifact.Algorithm, artifact.Parameters,
            artifact.Fingerprint, artifact.Status, artifact.SampleRate, artifact.Channels, artifact.ProcessedUnits, artifact.ContainerStart,
            levels = artifact.Waveform.Length, silenceCount = artifact.Silence.Length, activityCount = artifact.Activity.Length,
            sceneCount = artifact.Scenes.Length,
            transcript = artifact.Transcript is { } speech ? new { speech.Model, speech.Language, speech.Provider, speech.Alignment, speech.Preprocessing,
                wordCount = speech.Words.Length, segmentCount = speech.Segments.Length, speech.CompletedChunks, speech.ReusedChunks } : null,
            artifact.Loudness, artifact.Proxy, artifact.Index, kind, offset, total, records, artifact.Diagnostics };
    }
}
