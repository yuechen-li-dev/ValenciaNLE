using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace Valencia.Obs;

public static class ObsDiscoveryService
{
    public static ImmutableArray<ObsPreset> Presets { get; } =
    [
        new("quality", "High-quality local recording", "MKV, then remux if required", "Native source resolution/frame rate", "Separate microphone and desktop tracks; verify with a test recording", "Inspect OBS's encoder list; prefer a supported hardware H.264 encoder; quality is workload dependent"),
        new("game", "Long-form game recording", "MKV for interruption recovery", "1080p60 initially; qualify 1440p60 independently", "Independent microphone/game/communications tracks", "H.264 is Valencia's qualified baseline; estimate storage before long takes"),
        new("tutorial", "Talking head/tutorial", "MKV", "1080p30 or60 according to motion", "Isolate speech from music/desktop", "Use OBS's quality controls; do not infer routing from input names"),
        new("storage", "Low-storage recording", "MKV", "Reduce resolution/frame rate only with explicit user approval", "Keep useful independent tracks", "Compare a short sample at the chosen quality; no universally correct bitrate")
    ];

    public static async Task<ObsRecordStatus> RecordStatusAsync(ObsClient client, CancellationToken cancellation = default)
    {
        var data = await client.RequestAsync("GetRecordStatus", cancellation: cancellation).ConfigureAwait(false);
        return new(data.GetProperty("outputActive").GetBoolean(), data.GetProperty("outputPaused").GetBoolean(),
            data.GetProperty("outputDuration").GetDecimal(), data.GetProperty("outputTimecode").GetString()!, data.GetProperty("outputBytes").GetInt64());
    }

    public static async Task<ObsDiscovery> ReadAsync(ObsClient client, CancellationToken cancellation = default)
    {
        var version = client.Version ?? throw new IOException("OBS capability negotiation has not completed.");
        var status = await RecordStatusAsync(client, cancellation).ConfigureAwait(false);
        var directory = await client.RequestAsync("GetRecordDirectory", cancellation: cancellation).ConfigureAwait(false);
        var scenes = await client.RequestAsync("GetSceneList", cancellation: cancellation).ConfigureAwait(false);
        var collection = await client.RequestAsync("GetSceneCollectionList", cancellation: cancellation).ConfigureAwait(false);
        var inputs = await client.RequestAsync("GetInputList", cancellation: cancellation).ConfigureAwait(false);
        var sources = ImmutableArray.CreateBuilder<ObsInput>();
        foreach (var input in inputs.GetProperty("inputs").EnumerateArray().Take(128))
        {
            string name = input.GetProperty("inputName").GetString()!;
            ImmutableArray<int> tracks = [];
            bool observed = false;
            if (version.Supports("GetInputAudioTracks"))
            {
                try
                {
                    var routing = await client.RequestAsync("GetInputAudioTracks", new { inputName = name }, cancellation).ConfigureAwait(false);
                    tracks = [.. routing.GetProperty("inputAudioTracks").EnumerateObject().Where(p => p.Value.GetBoolean()).Select(p => int.Parse(p.Name, CultureInfo.InvariantCulture)).Order()];
                    observed = true;
                }
                catch (ObsRequestException) { /* Non-audio inputs legitimately reject this request; routing stays unknown. */ }
            }
            sources.Add(new(name, input.GetProperty("inputKind").GetString()!, tracks, observed));
        }
        JsonElement? video = version.Supports("GetVideoSettings")
            ? await client.RequestAsync("GetVideoSettings", cancellation: cancellation).ConfigureAwait(false) : null;
        var settings = ImmutableDictionary.CreateBuilder<string, string?>();
        // Read only recording-specific keys. Never request stream-service settings or stream keys.
        if (version.Supports("GetProfileParameter"))
        foreach (var key in new[] { ("Output", "Mode"), ("AdvOut", "RecFormat2"), ("AdvOut", "RecEncoder"), ("AdvOut", "RecTracks"),
            ("SimpleOutput", "RecFormat2"), ("SimpleOutput", "RecQuality"), ("SimpleOutput", "RecEncoder") })
        {
            try
            {
                var value = await client.RequestAsync("GetProfileParameter", new { parameterCategory = key.Item1, parameterName = key.Item2 }, cancellation).ConfigureAwait(false);
                settings[key.Item1 + "/" + key.Item2] = value.GetProperty("parameterValue").ValueKind == JsonValueKind.Null ? null : value.GetProperty("parameterValue").ToString();
            }
            catch (ObsRequestException) { settings[key.Item1 + "/" + key.Item2] = null; }
        }
        return new(version, status, directory.GetProperty("recordDirectory").GetString()!,
            collection.GetProperty("currentSceneCollectionName").GetString()!, scenes.GetProperty("currentProgramSceneName").GetString()!,
            [.. scenes.GetProperty("scenes").EnumerateArray().Take(128).Select(s => s.GetProperty("sceneName").GetString()!)], sources.ToImmutable(), video, settings.ToImmutable());
    }

    public static ImmutableArray<ObsPreflight> Preflight(ObsDiscovery discovery, IEnumerable<string>? expectedInputs = null,
        decimal estimatedMbps = 20, int minutes = 60)
    {
        if (estimatedMbps is <= 0 or > 1000 || minutes is < 1 or > 1440) throw new ArgumentOutOfRangeException(nameof(estimatedMbps));
        var checks = ImmutableArray.CreateBuilder<ObsPreflight>();
        checks.Add(new("info", "CONNECTED", $"OBS {discovery.Version.ObsVersionString}, WebSocket {discovery.Version.WebSocketVersion}, RPC {discovery.Version.RpcVersion}"));
        checks.Add(new(discovery.Recording.Active ? "warning" : "info", "RECORD_STATE", discovery.Recording.Active ? "Already recording; connecting does not restart or stop it." : "Recording is inactive."));
        if (!Directory.Exists(discovery.Directory)) checks.Add(new("error", "DIRECTORY", "OBS recording directory is not accessible on this machine."));
        else
        {
            try
            {
                long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(discovery.Directory))!).AvailableFreeSpace;
                decimal needed = estimatedMbps * 1_000_000 / 8 * minutes * 60;
                checks.Add(new(free < needed ? "error" : "info", "DISK_ESTIMATE",
                    FormattableString.Invariant($"Free {free} bytes; estimate {needed} bytes at user-selected {estimatedMbps} Mbps for {minutes} minutes. CQP/VBR can exceed this estimate.")));
            }
            catch (IOException) { checks.Add(new("warning", "DISK_UNKNOWN", "Available disk space could not be measured.")); }
        }
        foreach (string expected in expectedInputs ?? [])
            checks.Add(new(discovery.Inputs.Any(i => i.Name == expected) ? "info" : "warning", "EXPECTED_INPUT", "Expected input " + expected + (discovery.Inputs.Any(i => i.Name == expected) ? " is present; signal/routing still needs a recorded test." : " was not found.")));
        checks.Add(new("warning", "VERIFY_AUDIO", "Input presence and OBS track flags do not prove audible content. Record and independently inspect separate streams."));
        checks.Add(new("warning", "ENCODER_UNKNOWN", "WebSocket discovery does not enumerate all encoder availability/properties. No encoder configuration is changed. Validate the actual encoded sample."));
        var format = discovery.RecordingSettings.GetValueOrDefault("AdvOut/RecFormat2") ?? discovery.RecordingSettings.GetValueOrDefault("SimpleOutput/RecFormat2");
        checks.Add(new(format is null || format.Contains("mkv", StringComparison.OrdinalIgnoreCase) || format.Contains("mp4", StringComparison.OrdinalIgnoreCase) ? "info" : "warning",
            "CONTAINER", "Observed configured format: " + (format ?? "unknown") + ". Probe the completed file; MKV/MP4 are the qualified ingestion targets."));
        return checks.ToImmutable();
    }
}
