using Valencia.Core;
using Valencia.Engine;
using Valencia.Obs;

namespace Valencia.App.Editing;

internal sealed partial class EditorWindow
{
    private EditingEngine? obsEngine;
    public ObsWorkflow? Obs => obsEngine?.Obs;

    internal async Task ConnectObs()
    {
        if (Obs is not null)
        {
            await obsEngine!.DisconnectObsAsync();
            await obsEngine.DisposeAsync();
            obsEngine = null;
            Editor.Status = "OBS disconnected. Any active recording continues independently.";
            return;
        }
        var stored = ObsCredentials.Load();
        string? endpoint = Environment.GetEnvironmentVariable("VALENCIA_OBS_ENDPOINT");
        var options = endpoint is null ? stored?.Options ?? new(new Uri("ws://127.0.0.1:4455")) : new ObsConnectionOptions(new Uri(endpoint));
        string? password = Environment.GetEnvironmentVariable("VALENCIA_OBS_PASSWORD") ?? stored?.Password;
        string? root = Environment.GetEnvironmentVariable("VALENCIA_OBS_RECORDING_ROOT");
        if (root is null)
        {
            keys.Reset(); CancelGesture();
            using var folder = new FolderBrowserDialog { Description = "Choose the expected OBS recording directory (ingestion boundary)" };
            root = folder.ShowDialog() == DialogResult.OK ? folder.SelectedPath : null;
            keys.Reset();
            if (root is null) return;
        }
        string manifestDirectory = Environment.GetEnvironmentVariable("VALENCIA_OBS_MANIFEST_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valencia", "Recordings");
        var engine = new EditingEngine(Editor.Session, projectPath: Editor.ProjectPath);
        try
        {
            await engine.ConnectObsAsync(options, password, Path.Combine(manifestDirectory, Guid.NewGuid().ToString("N") + ".obs-session.json"), root,
                allowRecordingControl: true, jobCancellation?.Token ?? default);
            obsEngine = engine;
            Editor.Status = "OBS connected. Start/stop/markers require explicit controls; no output settings were changed.";
        }
        catch { await engine.DisposeAsync(); throw; }
    }
    internal async Task ObsAction(string action)
    {
        var workflow = Obs ?? throw new InvalidOperationException("Connect OBS first. Credentials come from obs configure or VALENCIA_OBS_PASSWORD.");
        var token = jobCancellation?.Token ?? default;
        switch (action)
        {
            case "start":
                await workflow.StartAsync(token);
                ShowCard(new(TitleCardKind.Recording, "REC", "RECORDING\n" + DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                    "OBS is recording. Mark highlight drops a marker without stopping.", ["OBS " + Editor.ObsConnection.ToUpperInvariant()]));
                break;
            case "stop": await workflow.StopAsync(token); break;
            case "pause":
                var status = await ObsDiscoveryService.RecordStatusAsync(workflow.Recording.Client, token);
                await workflow.PauseAsync(!status.Paused, token); break;
            case "marker": await workflow.MarkAsync("Highlight", cancellation: token); break;
            case "import":
                await using (var engine = new EditingEngine(Editor.Session))
                {
                    var imported = await workflow.ImportIntoAsync(engine, Editor.Session.Snapshot.Revision, token);
                    Editor.SelectedAsset = imported.AssetIds.FirstOrDefault();
                    Editor.Status = imported.AlreadyImported ? "Recording already imported." : "Completed recording imported. Use Insert to place it.";
                }
                return;
            default: throw new ArgumentException("Unknown recording action.");
        }
        Editor.Status = "OBS " + workflow.Recording.Snapshot.State + "; markers " + workflow.Recording.Snapshot.Markers.Length;
    }
}
