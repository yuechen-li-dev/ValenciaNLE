using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Persistence;

namespace Valencia.App.Editing;

internal sealed partial class EditorWindow
{
    private ProjectWriteLease? projectLease;
    private ProjectDiskVersion? projectVersion;
    private ProjectAutosave? autosave;
    private readonly List<Task> recoveryCleanup = [];
    private ProjectSnapshot? dirtyCheckSnapshot;
    private ProjectDocument? dirtyCheckSaved;
    private bool autosaveDirty;
    private bool cleanDocumentClose;

    private static ProjectAutosave CreateAutosave()
    {
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("VALENCIA_AUTOSAVE_SECONDS"), out int configured)
            ? Math.Clamp(configured, 5, 300) : 30;
        return new(Path.Combine(ValenciaData.Root, "Recovery"), TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(2));
    }

    private void PollAutosave()
    {
        if (autosave is null) return;
        var snapshot = Editor.Session.Snapshot;
        var saved = Editor.SavedDocument;
        if (!ReferenceEquals(snapshot, dirtyCheckSnapshot) || !ReferenceEquals(saved, dirtyCheckSaved))
        {
            dirtyCheckSnapshot = snapshot;
            dirtyCheckSaved = saved;
            autosaveDirty = saved is null || !DocumentSemantics.Equal(saved, snapshot.Document);
        }
        var status = autosave.Observe(snapshot, autosaveDirty, Editor.ProjectPath);
        if (status is null) return;
        Editor.Status = status.Error is { } error ? "Autosave failed. Save As to a writable location. " + error
            : $"Autosaved revision {status.Revision}.";
        lastUi = -1;
    }

    private async Task OpenManagedProject(string path)
    {
        string full = Path.GetFullPath(path);
        CancellationToken cancellation = jobCancellation?.Token ?? default;
        var (loaded, version) = await ProjectWriteLease.OpenAsync(full, cancellation);
        SwitchRecoveryRun();
        bool recovered = loaded.Metadata?.Kind == "autosave";
        if (recovered)
        {
            ReleaseProject();
        }
        else
        {
            TryHoldProject(full);
            projectVersion = version;
        }
        Editor.Open(loaded.Snapshot, full);
        if (recovered)
        {
            Editor.ProjectPath = null;
            Editor.SavedDocument = null;
        }
        imports.Clear();
        Seek(default);
        string missing = loaded.MissingMedia.Length == 0 ? "" : $" Missing {loaded.MissingMedia.Length} media files. Select media and Relink.";
        Editor.Status = recovered ? "Recovered edits. Save As to a new project." + missing
            : (projectLease is null ? "Opened without write ownership. Use Save As." : "Project opened.") + missing;
    }

    private async Task SaveManagedProject(string path)
    {
        var saved = Editor.Session.Snapshot;
        string full = Path.GetFullPath(path);
        bool useHeldLease = projectLease?.Matches(full) == true;
        var acquired = useHeldLease ? null : ProjectWriteLease.Acquire(full);
        try
        {
            var expected = projectVersion is { } previous && string.Equals(previous.Path, full, StringComparison.OrdinalIgnoreCase)
                ? previous : null;
            var version = await (projectLease is { } held && useHeldLease ? held : acquired!).SaveAsync(saved, expected,
                jobCancellation?.Token ?? default);
            if (acquired is not null)
            {
                projectLease?.Dispose();
                projectLease = acquired;
                acquired = null;
            }
            projectVersion = version;
            Editor.ProjectPath = full;
            Editor.SavedDocument = saved.Document;
            Editor.Status = "Project saved.";
            SwitchRecoveryRun();
        }
        finally { acquired?.Dispose(); }
    }

    private void TryHoldProject(string path)
    {
        if (projectLease?.Matches(path) == true) return;
        ReleaseProject();
        try { projectLease = ProjectWriteLease.Acquire(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            diagnostics.Record("project-write-lease-unavailable", generation, Editor.Playhead.Ticks, error.Message);
        }
    }

    private void ReleaseProject()
    {
        projectLease?.Dispose();
        projectLease = null;
        projectVersion = null;
    }

    private void SwitchRecoveryRun()
    {
        if (autosave is not { } previous) return;
        autosave = CreateAutosave();
        recoveryCleanup.RemoveAll(t => t.IsCompletedSuccessfully);
        recoveryCleanup.Add(Task.Run(previous.CloseCleanly));
    }

    private void FinishDocumentProtection()
    {
        try
        {
            if (cleanDocumentClose) autosave?.CloseCleanly();
            else autosave?.Dispose();
            Task.WhenAll(recoveryCleanup).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            diagnostics.Record("recovery-cleanup-error", generation, Editor.Playhead.Ticks, error.Message);
            diagnostics.Save(error);
        }
        finally { ReleaseProject(); }
    }
}
