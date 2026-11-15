using System.Diagnostics;
using System.Text.Json;

namespace Valencia.Obs;

/// <summary>One observable recording session, owned by a UI/CLI/MCP scope. Disposal never stops OBS recording.</summary>
public sealed class ObsRecordingSession : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim saving = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly string manifestPath;
    private Task? observation;
    private RecordingManifest manifest;
    private TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ObsClient Client { get; }
    public RecordingManifest Snapshot { get { lock (gate) return manifest; } }
    public Exception? ObservationFailure { get; private set; }

    public ObsRecordingSession(ObsClient client, string manifestPath, RecordingManifest? resume = null)
    {
        Client = client;
        this.manifestPath = Path.GetFullPath(manifestPath);
        manifest = resume is null ? RecordingManifest.Empty() : resume with
        {
            ContinuousObservation = false,
            Diagnostics = resume.Diagnostics.Add("Connection resumed without continuous observation; markers require explicit approximate-use policy.")
        };
        ObsManifestFile.Validate(manifest);
    }
    public async Task ConnectAsync(CancellationToken cancellation = default)
    {
        observation ??= Task.Run(ObserveAsync);
        await Client.ConnectAsync(cancellation).ConfigureAwait(false);
        var status = await ObsDiscoveryService.RecordStatusAsync(Client, cancellation).ConfigureAwait(false);
        lock (gate)
        {
            manifest = manifest with { ObsVersion = Client.Version!.ObsVersionString };
            if (status.Active && manifest.State == "idle") manifest = manifest with { State = "observedRecording", ContinuousObservation = false };
        }
        await SaveAsync(cancellation).ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellation = default)
    {
        var before = await ObsDiscoveryService.RecordStatusAsync(Client, cancellation).ConfigureAwait(false);
        if (before.Active) throw new InvalidOperationException("OBS is already recording; Valencia will not restart it.");
        lock (gate)
        {
            stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            manifest = RecordingManifest.Empty() with { ObsVersion = Client.Version!.ObsVersionString, StartedAt = DateTimeOffset.UtcNow, State = "starting", ContinuousObservation = true };
        }
        await SaveAsync(cancellation).ConfigureAwait(false);
        try
        {
            await Client.RequestAsync("StartRecord", cancellation: cancellation).ConfigureAwait(false);
            lock (gate) manifest = manifest with { State = "recording" };
            await SaveAsync(cancellation).ConfigureAwait(false);
        }
        catch
        {
            lock (gate) manifest = manifest with { State = "unknown", ContinuousObservation = false };
            await SaveAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
    public async Task StopAsync(CancellationToken cancellation = default)
    {
        Task completed;
        lock (gate) completed = stopped.Task;
        var result = await Client.RequestAsync("StopRecord", cancellation: cancellation).ConfigureAwait(false);
        lock (gate)
        {
            string? output = result.TryGetProperty("outputPath", out var path) ? path.GetString() : null;
            AddFile(output, false, "StopRecord accepted; awaiting final encoder-drain STOPPED event");
        }
        // Real OBS 32 can acknowledge StopRecord while GetRecordStatus remains active.
        // Only the output STOPPED event establishes recording completion, not this response.
        await completed.WaitAsync(TimeSpan.FromSeconds(15), cancellation).ConfigureAwait(false);
        await SaveAsync(cancellation).ConfigureAwait(false);
    }
    public async Task PauseAsync(bool pause, CancellationToken cancellation = default)
    {
        await Client.RequestAsync(pause ? "PauseRecord" : "ResumeRecord", cancellation: cancellation).ConfigureAwait(false);
        lock (gate) manifest = manifest with { State = pause ? "paused" : "recording" };
        await SaveAsync(cancellation).ConfigureAwait(false);
    }
    public async Task<RecordingMarker> MarkAsync(string note, string kind = "highlight", CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(note) || note.Length > 256 || kind is not ("highlight" or "explanation" or "mistake" or "retake" or "chapter"))
            throw new ArgumentException("Marker requires a short note and supported kind.");
        var timer = Stopwatch.StartNew();
        var status = await ObsDiscoveryService.RecordStatusAsync(Client, cancellation).ConfigureAwait(false);
        if (!status.Active) throw new InvalidOperationException("Markers require an active OBS recording.");
        RecordingMarker marker;
        lock (gate)
        {
            if (manifest.Markers.Length >= 1000) throw new InvalidOperationException("Recording marker budget exceeded.");
            marker = new(Guid.NewGuid(), kind, note, status.DurationMs, timer.Elapsed.TotalMilliseconds, DateTimeOffset.UtcNow,
                manifest.Files.LastOrDefault(f => f.Kind == RecordingFileKind.Recording)?.Path, manifest.ContinuousObservation, status.Paused);
            manifest = manifest with { Markers = manifest.Markers.Add(marker) };
        }
        await SaveAsync(cancellation).ConfigureAwait(false);
        return marker;
    }
    public async Task SaveReplayAsync(CancellationToken cancellation = default)
    {
        await Client.RequestAsync("SaveReplayBuffer", cancellation: cancellation).ConfigureAwait(false);
        // ReplayBufferSaved is the completion proof; the request alone is not a finalized path.
    }

    public async Task AttachMediaMetadataAsync(System.Collections.Immutable.ImmutableArray<RecordingFile> files, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            manifest = manifest with { Files = [.. manifest.Files.Select(current =>
            {
                var enriched = files.FirstOrDefault(file => string.Equals(file.Path, current.Path, StringComparison.OrdinalIgnoreCase));
                return enriched is null ? current : current with { AssetId = enriched.AssetId, Fingerprint = enriched.Fingerprint, DurationMs = enriched.DurationMs, Streams = enriched.Streams };
            })] };
        }
        await SaveAsync(cancellation).ConfigureAwait(false);
    }

    private async Task ObserveAsync()
    {
        try
        {
            await foreach (var value in Client.Events.ReadAllAsync(stopping.Token).ConfigureAwait(false))
            {
                lock (gate)
                {
                    string? state = value.Data.TryGetProperty("outputState", out var raw) ? raw.GetString() : null;
                    if (value.Type == "RecordStateChanged" && state == "OBS_WEBSOCKET_OUTPUT_STARTED"
                        && (manifest.StoppedAt is not null || manifest.State == "idle"))
                    {
                        // An external OBS start is a new take too. The preceding take was
                        // archived by SaveAsync, and its completion must not satisfy this stop.
                        stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        manifest = RecordingManifest.Empty() with { ObsVersion = Client.Version?.ObsVersionString,
                            StartedAt = value.ReceivedAt, ContinuousObservation = true, State = "recording" };
                    }
                    if (manifest.Events.Length >= 2000) throw new InvalidOperationException("Recording event manifest budget exceeded; recording continues independently in OBS.");
                    if (value.Type is "RecordStateChanged" or "RecordFileChanged" or "CurrentProgramSceneChanged" or "Disconnected" or "Connected" or "ReplayBufferSaved")
                        manifest = manifest with { Events = manifest.Events.Add(new(value.Type, state, value.ReceivedAt, value.Generation)) };
                    switch (value.Type)
                    {
                        case "Disconnected":
                            manifest = manifest with { ContinuousObservation = false, State = "unknown",
                                Diagnostics = manifest.Diagnostics.Add("OBS connection lost; recording may continue. No automatic stop/retry was issued.") };
                            break;
                        case "RecordFileChanged":
                            if (value.Data.TryGetProperty("newOutputPath", out var changed)) AddFile(changed.GetString(), manifest.StoppedAt is not null, "RecordFileChanged; ingestion deferred until stopped");
                            break;
                        case "RecordStateChanged":
                            string? path = value.Data.TryGetProperty("outputPath", out var output) ? output.GetString() : null;
                            if (state == "OBS_WEBSOCKET_OUTPUT_STOPPED") Complete(path, "Observed OBS RecordStateChanged STOPPED");
                            else if (manifest.StoppedAt is null)
                            {
                                if (state == "OBS_WEBSOCKET_OUTPUT_STARTED") { AddFile(path, false, "OBS recording started"); manifest = manifest with { State = "recording" }; }
                                else if (state == "OBS_WEBSOCKET_OUTPUT_PAUSED") manifest = manifest with { State = "paused" };
                                else if (state == "OBS_WEBSOCKET_OUTPUT_RESUMED") manifest = manifest with { State = "recording" };
                            }
                            break;
                        case "ReplayBufferSaved":
                            if (value.Data.TryGetProperty("savedReplayPath", out var replay)) AddFile(replay.GetString(), true,
                                "OBS ReplayBufferSaved completion event", RecordingFileKind.Replay);
                            break;
                    }
                }
                await SaveAsync(stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            ObservationFailure = error;
            lock (gate) manifest = manifest with { ContinuousObservation = false, State = "observationFailed" };
        }
    }

    private void AddFile(string? path, bool complete, string evidence, RecordingFileKind kind = RecordingFileKind.Recording)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("OBS reported a non-absolute recording path.");
        path = Path.GetFullPath(path);
        int index = Array.FindIndex(manifest.Files.ToArray(), f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            if (manifest.Files.Length >= 64) throw new InvalidOperationException("Recording file-split budget exceeded.");
            manifest = manifest with { Files = manifest.Files.Add(new(path, complete, evidence, Kind: kind)) };
        }
        else if (complete) manifest = manifest with { Files = manifest.Files.SetItem(index, manifest.Files[index] with { Completed = true, Evidence = evidence }) };
    }
    private void Complete(string? output, string evidence)
    {
        AddFile(output, true, evidence);
        manifest = manifest with { State = "stopped", StoppedAt = DateTimeOffset.UtcNow,
            Files = [.. manifest.Files.Select(f => f with { Completed = true, Evidence = evidence })] };
        stopped.TrySetResult();
    }
    public async Task SaveAsync(CancellationToken cancellation = default)
    {
        await saving.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var current = Snapshot;
            await ObsManifestFile.SaveAsync(manifestPath, current, cancellation).ConfigureAwait(false);
            if (current.StoppedAt is not null || !current.Files.IsEmpty || !current.Markers.IsEmpty)
            {
                string archive = Path.Combine(Path.GetDirectoryName(manifestPath)!, Path.GetFileNameWithoutExtension(manifestPath) + "." + current.SessionId.ToString("N") + ".json");
                await ObsManifestFile.SaveAsync(archive, current, cancellation).ConfigureAwait(false);
            }
        }
        finally { saving.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        if (observation is not null)
        {
            try { await observation.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                await stopping.CancelAsync().ConfigureAwait(false);
                await observation.ConfigureAwait(false);
            }
        }
        await stopping.CancelAsync().ConfigureAwait(false);
        await SaveAsync(CancellationToken.None).ConfigureAwait(false);
        stopping.Dispose(); saving.Dispose();
    }
}
