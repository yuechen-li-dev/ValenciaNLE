using System.Diagnostics;
using System.Runtime.Versioning;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Media;
using Valencia.Obs;

namespace Valencia.Cli;

internal static class ObsCli
{
    [SupportedOSPlatform("windows")]
    public static (ObsConnectionOptions Options, string? Password) Connection(string[] arguments)
    {
        string? endpoint = Program.Option(arguments, "--endpoint");
        string? password = Environment.GetEnvironmentVariable(Program.Option(arguments, "--password-env") ?? "VALENCIA_OBS_PASSWORD");
        var stored = ObsCredentials.Load();
        return (endpoint is null ? stored?.Options ?? new(new Uri("ws://127.0.0.1:4455"))
            : new(new Uri(endpoint), arguments.Contains("--allow-remote")), password ?? stored?.Password);
    }
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("OBS credential storage currently targets Windows.");
        if (args.Length < 2) throw new ArgumentException("Use obs configure|info|preflight|record|ingest|marker|presets.");
        string Required(string name) => Program.Option(args, name) ?? throw new ArgumentException("Missing " + name);
        if (args[1] == "presets") { Console.WriteLine(EngineJson.Write(ObsDiscoveryService.Presets)); return 0; }
        var connection = Connection(args);
        if (args[1] == "configure")
        {
            await ObsCredentials.SaveAsync(connection.Options.Endpoint, connection.Password ?? throw new ArgumentException("Set VALENCIA_OBS_PASSWORD (never a command-line password)."), cancellation: cancellation);
            Console.WriteLine(EngineJson.Write(new { saved = true, endpoint = connection.Options.Endpoint, credentialStore = "Windows CurrentUser DPAPI" }));
            return 0;
        }
        string project = Required("--project");
        await using var engine = await EditingEngine.OpenAsync(project, cancellation: cancellation);
        string recordingRoot = Required("--recording-root");
        string manifest = Program.Option(args, "--manifest") ?? project + ".obs-session.json";
        if (args[1] == "ingest")
        {
            var imported = await engine.ImportRecordingAsync(ObsManifestFile.Load(manifest), recordingRoot, engine.Session.Snapshot.Revision, cancellation);
            await ObsManifestFile.SaveAsync(manifest, imported.Manifest, cancellation);
            if (args.Contains("--place")) Place(engine, imported.AssetIds, cancellation);
            if (Program.Option(args, "--save-as") is { } destination) await engine.SaveAsync(destination, cancellation);
            Console.WriteLine(EngineJson.Write(imported));
            return 0;
        }
        var obs = await engine.ConnectObsAsync(connection.Options, connection.Password, manifest, recordingRoot, args.Contains("--allow-recording-control"), cancellation);
        var discovery = await obs.DiscoverAsync(cancellation);
        if (args[1] is "info" or "preflight")
        {
            Console.WriteLine(EngineJson.Write(new { discovery, checks = ObsDiscoveryService.Preflight(discovery), manifest = obs.Recording.Snapshot }));
            return 0;
        }
        if (args[1] != "record") throw new ArgumentException("Use a connected UI/MCP scope to mark/pause/stop a recording. CLI record accepts --mark-at and --pause-at/--pause-seconds.");
        if (!args.Contains("--allow-recording-control")) throw new UnauthorizedAccessException("CLI recording requires explicit --allow-recording-control.");
        int seconds = int.Parse(Program.Option(args, "--seconds") ?? "15", System.Globalization.CultureInfo.InvariantCulture);
        if (seconds is < 1 or > 600) throw new ArgumentException("Bounded CLI recording must be 1..600 seconds.");
        int markAt = int.Parse(Program.Option(args, "--mark-at") ?? "3", System.Globalization.CultureInfo.InvariantCulture);
        int pauseAt = int.Parse(Program.Option(args, "--pause-at") ?? "-1", System.Globalization.CultureInfo.InvariantCulture);
        int pauseSeconds = int.Parse(Program.Option(args, "--pause-seconds") ?? "2", System.Globalization.CultureInfo.InvariantCulture);
        if (pauseSeconds is < 1 or > 30) throw new ArgumentException("Pause fixture must be 1..30 seconds.");
        await obs.StartAsync(cancellation);
        var timer = Stopwatch.StartNew();
        bool marked = false, paused = false;
        while (timer.Elapsed.TotalSeconds < seconds)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!marked && timer.Elapsed.TotalSeconds >= markAt)
            {
                await obs.MarkAsync(Program.Option(args, "--note") ?? "Important explanation", cancellation: cancellation);
                marked = true;
            }
            if (!paused && pauseAt >= 0 && timer.Elapsed.TotalSeconds >= pauseAt)
            {
                await obs.PauseAsync(true, cancellation);
                await Task.Delay(TimeSpan.FromSeconds(pauseSeconds), cancellation);
                await obs.PauseAsync(false, cancellation);
                paused = true;
            }
            Console.Error.WriteLine(EngineJson.Write(new { obs = obs.Recording.Client.Status, recording = await ObsDiscoveryService.RecordStatusAsync(obs.Recording.Client, cancellation) }));
            await Task.Delay(250, cancellation);
        }
        // This stop is explicitly part of the requested bounded record command, never a disposal side effect.
        await obs.StopAsync(cancellation);
        var result = await obs.ImportAsync(engine.Session.Snapshot.Revision, cancellation);
        if (args.Contains("--place")) Place(engine, result.AssetIds, cancellation);
        string saved = Program.Option(args, "--save-as") ?? project + ".recorded.valencia";
        await engine.SaveAsync(saved, cancellation);
        Console.WriteLine(EngineJson.Write(new { result, project = saved, manifest, elapsedSeconds = timer.Elapsed.TotalSeconds, discovery }));
        return 0;
    }

    public static void Place(EditingEngine engine, System.Collections.Immutable.ImmutableArray<Guid> assetIds, CancellationToken cancellation)
    {
        if (assetIds.Length != 1) throw new NotSupportedException("Automatic creator placement currently requires one completed recording file; split files are imported as separate assets for explicit assembly.");
        var asset = engine.Query().Assets.First(a => a.Id == assetIds[0]);
        var sequence = engine.Query().Sequences[0];
        var imported = MediaAssetProbe.Import(asset.Id, asset.SourcePath);
        var result = engine.Execute(new EditTransaction(Guid.NewGuid(), 1, engine.Session.Snapshot.Revision,
            [.. InsertionCommands.Build(engine.Session.Snapshot, sequence.Id, imported, sequence.Duration).Select(o => new EditCommand(Guid.NewGuid(), 1, o))]), cancellation);
        if (!result.Success) throw new InvalidOperationException(result.Message);
    }
}
