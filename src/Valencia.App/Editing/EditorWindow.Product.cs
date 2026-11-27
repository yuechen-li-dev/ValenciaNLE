using Valencia.Analysis;
using Valencia.Core;
using Valencia.Engine;
using Valencia.Media;

namespace Valencia.App.Editing;

internal sealed partial class EditorWindow
{
    private Guid? shownExport;
    private ExportStatus? lastExportStatus;
    private double lastProductPoll;
    private bool firstRunChecked;
    private (EditingEngine Engine, Guid Job)? previewProxy;
    private (Guid Asset, int Stream)? pendingPreviewProxy;
    private bool refreshPausedProxy;

    private void ConsiderPreviewProxy(ImportedAsset imported)
    {
        var stream = imported.Asset.Streams.FirstOrDefault(item => MediaProxy.ShouldPrepare(item, imported.Duration));
        if (stream is not null) pendingPreviewProxy = (imported.Asset.Id, stream.Index);
    }

    private void PollPreviewProxy()
    {
        if (previewProxy is { } active)
        {
            if (!ReferenceEquals(active.Engine.Session, Editor.Session)) previewProxy = null;
            else
            {
                var status = active.Engine.Analysis.Status(active.Job);
                if (status.State == "running") return;
                previewProxy = null;
                refreshPausedProxy = status.State == "completed";
                Editor.Status = status.State switch
                {
                    "completed" => "Preview proxy ready.",
                    "cancelled" => "Preview proxy cancelled.",
                    _ => "Preview proxy failed. " + status.Error
                };
                diagnostics.Record("preview-proxy-" + status.State, generation, Editor.Playhead.Ticks, status.Error);
                lastUi = -1;
            }
        }
        if (pendingPreviewProxy is not { } pending || job is not null) return;
        if (agentProposalEngine is { } service && ReferenceEquals(service.Session, Editor.Session)
            && service.Analysis.ActiveJobs().Length > 0) return;
        pendingPreviewProxy = null;
        if (!Editor.Session.Snapshot.Document.Assets.Any(asset => asset.Id == pending.Asset)) return;
        RunJob(async () =>
        {
            var engine = await DesktopAgentEngineAsync();
            var id = engine.Analysis.Start(new(pending.Asset, pending.Stream, "proxy"));
            previewProxy = (engine, id);
            Editor.Status = "Preparing preview proxy in the background.";
        });
    }

    private void CancelPreviewProxy()
    {
        pendingPreviewProxy = null;
        if (previewProxy is { } active && ReferenceEquals(active.Engine.Session, Editor.Session))
            active.Engine.Analysis.Cancel(active.Job);
    }

    private void QueueDesktopExport(string? presetId = null)
    {
        if (Editor.Sequence.Duration.Ticks == 0) { Editor.Status = "The active sequence is empty."; return; }
        PauseTransport();
        CancelGesture();
        keys.Reset();
        var preset = presetId is null ? null : ExportCatalog.Preset(presetId);
        using var dialog = new SaveFileDialog
        {
            Title = "Queue edited sequence",
            Filter = "VP9 + Opus WebM|*.webm|AV1 + Opus WebM|*.webm|H.264 + AAC MP4|*.mp4|FFV1 + FLAC Matroska|*.mkv|HEVC + AAC MP4|*.mp4",
            FilterIndex = preset?.Encoder switch { "av1" => 2, "archive" => 4, _ => 1 },
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(new DialogOwner(WindowHandle)) != DialogResult.OK) return;
        keys.Reset();
        var encoder = ExportCatalog.Encoder(dialog.FilterIndex switch { 2 => "av1", 3 => "h264", 4 => "archive", 5 => "hevc", _ => "vp9" });
        string output = Path.GetFullPath(dialog.FileName);
        var snapshot = Editor.Session.Snapshot;
        var spec = new RenderSpecification(Editor.SequenceId, output, Editor.SelectedProfile, encoder.Id, preset,
            preset?.Audio ?? (encoder.Id == "archive" ? null : MonitorLoudnessFinish()), preset?.BurnCaptions ?? true, preset?.Quality);
        RunJob(async () =>
        {
            var engine = await DesktopAgentEngineAsync();
            var status = await engine.Exports.EnqueueAsync(snapshot, spec, jobCancellation?.Token ?? default);
            shownExport = status.JobId;
            lastExportStatus = null;
            Editor.Status = $"Queued {Path.GetFileName(output)} at revision {snapshot.Revision}.";
        });
    }

    private void PollProductServices()
    {
        if (clock.Elapsed.TotalSeconds - lastProductPoll < .3) return;
        lastProductPoll = clock.Elapsed.TotalSeconds;
        PollPreviewProxy();
        if (!firstRunChecked && Media.FfmpegRuntime.IsDistribution && job is null && smoke is null)
        {
            firstRunChecked = true;
            string marker = Path.Combine(ValenciaData.Root, "first-run-" + ProductDoctor.Version);
            if (!File.Exists(marker))
                RunJob(async () =>
                {
                    var report = await Task.Run(() => ProductDoctor.CheckAsync(Graphics.GpuName, jobCancellation?.Token ?? default));
                    Editor.Status = FormatEssential(report);
                    Directory.CreateDirectory(ValenciaData.Root);
                    File.WriteAllText(marker, EngineJson.Write(report));
                });
        }
        if (shownExport is not { } id || agentProposalEngine is not { } engine ||
            !ReferenceEquals(engine.Session, Editor.Session)) return;
        try
        {
            ExportStatus status = engine.Exports.Status(id);
            if (status == lastExportStatus) return;
            lastExportStatus = status;
            Editor.Status = status.State switch
            {
                "running" => $"Rendering {status.Encoder}: {status.Frames}/{status.TotalFrames} frames.",
                "checking" => "Checking the encoded output.",
                "completed" => $"Export completed. {status.Lint?.Findings.Count ?? 0} review findings.",
                "failed" => status.Error ?? "Export failed.",
                _ => $"Render is {status.State}."
            };
            lastUi = -1;
        }
        catch (KeyNotFoundException) { shownExport = null; }
    }

    private async Task ShowRenderQueue()
    {
        var service = await DesktopAgentEngineAsync();
        var rows = await Task.Run(() => service.Exports.List());
        string text = rows.Length == 0 ? "No render jobs for this project." : string.Join("\n\n", rows.Take(10).Select(row =>
            $"{Path.GetFileName(row.Output)}  [{row.State}]  rev {row.Revision}" +
            (row.Error is null ? "" : "\n" + row.Error) +
            (row.Lint is null ? "" : "\n" + string.Join("\n", row.Lint.Findings.Take(3).Select(f => f.Message)))));
        MessageBox.Show(new DialogOwner(WindowHandle), text, "Valencia Render Queue", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task RetryDesktopRender()
    {
        var engine = await DesktopAgentEngineAsync();
        var previous = engine.Exports.List().FirstOrDefault(status => status.State is "interrupted" or "failed" or "cancelled");
        if (previous is null || previous.Encoder is null) { Editor.Status = "No render is available to retry."; return; }
        var encoder = ExportCatalog.Encoder(previous.Encoder);
        using var dialog = new SaveFileDialog
        {
            Title = $"Retry render from revision {previous.Revision}",
            FileName = Path.GetFileName(previous.Output),
            Filter = $"{encoder.Id} output|*{encoder.Extension}",
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(new DialogOwner(WindowHandle)) != DialogResult.OK) return;
        keys.Reset();
        var status = await engine.Exports.RetryAsync(previous.ProjectId, previous.JobId, Path.GetFullPath(dialog.FileName),
            jobCancellation?.Token ?? default);
        shownExport = status.JobId;
        lastExportStatus = null;
        Editor.Status = $"Queued retry from revision {status.Revision}.";
    }

    private void CancelRenderJob()
    {
        if (agentProposalEngine?.Exports.ActiveJobs().FirstOrDefault(s => s.ProjectId == Editor.Session.Snapshot.Document.Id) is not { } active)
        { Editor.Status = "No active render to cancel."; return; }
        agentProposalEngine.Exports.Cancel(active.JobId);
        Editor.Status = "Render cancellation requested.";
    }

    private async Task ShowProductDiagnostics()
    {
        var report = await Task.Run(() => ProductDoctor.CheckAsync(Graphics.GpuName, jobCancellation?.Token ?? default));
        string checks = string.Join("\n", report.Checks.Select(check =>
            $"{check.Id}: {(check.Available ? "available" : "unavailable")} — {check.Detail}" +
            (check.Suggestion is null ? "" : " " + check.Suggestion)));
        MessageBox.Show(new DialogOwner(WindowHandle), $"Valencia {report.Version}\n{report.Runtime}\n\n{checks}",
            "Valencia Diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task ManageSpeechModel()
    {
        var model = WhisperModels.List().Single(item => item.Id == "tiny.en");
        if (model.State is "present" or "verified")
        {
            bool managed = model.Path is { } path && string.Equals(Path.GetDirectoryName(path), WhisperModels.ManagedDirectory, StringComparison.OrdinalIgnoreCase);
            if (managed && MessageBox.Show(new DialogOwner(WindowHandle), "Remove the managed tiny.en speech model?",
                "Speech model", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                WhisperModels.RemoveManaged("tiny.en");
                Editor.Status = "Managed speech model removed.";
            }
            else Editor.Status = "The tiny.en model is available.";
            return;
        }
        if (MessageBox.Show(new DialogOwner(WindowHandle), $"Download the optional tiny.en speech model ({model.DownloadBytes:N0} bytes)?",
            "Speech model", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var progress = new Progress<ModelDownloadProgress>(p => Editor.Status = $"Downloading speech model {p.DownloadedBytes * 100 / p.TotalBytes}%.");
        await WhisperModels.DownloadAsync("tiny.en", progress, jobCancellation?.Token ?? default);
        Editor.Status = "Speech model downloaded and verified.";
    }

    private static string FormatEssential(ProductCapabilities report)
    {
        var failure = report.Checks.FirstOrDefault(check => check.Essential && !check.Available);
        if (failure is null) return $"Valencia {report.Version} capability check complete.";
        return failure.Detail + " " + failure.Suggestion;
    }
}
