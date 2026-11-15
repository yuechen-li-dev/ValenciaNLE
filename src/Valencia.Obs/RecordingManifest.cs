using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valencia.Obs;

public sealed record RecordingMarker(Guid Id, string Kind, string Note, decimal OutputDurationMs, double RoundTripMs,
    DateTimeOffset ObservedAt, string? FileAtObservation, bool ContinuousObservation, bool Paused,
    string Precision = "OBS output-duration observation; not aligned to media PTS; absolute uncertainty unbounded until refined");
public sealed record RecordingObservation(string Type, string? State, DateTimeOffset ReceivedAt, long ConnectionGeneration);
public enum RecordingFileKind { Recording, Replay }
public sealed record RecordingFile(string Path, bool Completed, string Evidence, Guid? AssetId = null,
    string? Fingerprint = null, decimal? DurationMs = null, JsonElement? Streams = null, RecordingFileKind Kind = RecordingFileKind.Recording);
public sealed record RecordingManifest(int SchemaVersion, Guid SessionId, string? ObsVersion, DateTimeOffset? StartedAt,
    DateTimeOffset? StoppedAt, string State, bool ContinuousObservation, ImmutableArray<RecordingObservation> Events,
    ImmutableArray<RecordingFile> Files, ImmutableArray<RecordingMarker> Markers, ImmutableArray<string> Diagnostics)
{
    public static RecordingManifest Empty() => new(1, Guid.NewGuid(), null, null, null, "idle", false, [], [], [], []);
}

public static class ObsManifestFile
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public static RecordingManifest Load(string path)
    {
        if (new FileInfo(path).Length > 1_048_576) throw new InvalidDataException("Recording manifest exceeds one MiB.");
        var value = JsonSerializer.Deserialize<RecordingManifest>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Missing recording manifest.");
        Validate(value);
        return value;
    }
    public static void Validate(RecordingManifest value)
    {
        if (value.SchemaVersion != 1 || value.SessionId == Guid.Empty || value.Events.IsDefault || value.Files.IsDefault || value.Markers.IsDefault
            || value.Diagnostics.IsDefault || value.Events.Length > 2000 || value.Files.Length > 64 || value.Markers.Length > 1000
            || value.Files.Any(f => !System.IO.Path.IsPathFullyQualified(f.Path) || !Enum.IsDefined(f.Kind))
            || value.Markers.Any(m => m.Id == Guid.Empty || m.Note.Length > 256 || m.OutputDurationMs < 0))
            throw new InvalidDataException("Invalid recording manifest version, identity, paths or collection budget.");
    }
    public static Task SaveAsync(string path, RecordingManifest manifest, CancellationToken cancellation = default)
    {
        Validate(manifest);
        return WriteAtomicAsync(path, manifest, cancellation);
    }
    internal static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken cancellation)
    {
        string full = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            if (bytes.Length > 1_048_576) throw new InvalidDataException("OBS metadata exceeds one MiB.");
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, cancellation).ConfigureAwait(false);
                await file.FlushAsync(cancellation).ConfigureAwait(false);
                file.Flush(true);
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
