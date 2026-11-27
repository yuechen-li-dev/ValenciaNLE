using Valencia.Core;
using Valencia.Persistence;

namespace Valencia.App.Editing;

internal static class RecoveryPrompt
{
    public static string? Choose(string? startupPath)
    {
        string root = Path.Combine(ValenciaData.Root, "Recovery");
        IReadOnlyList<ArchivedProject> entries;
        try { entries = RecoveryRun.ScanAsync(root).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("Valencia could not read its recovery directory. " + error.Message, "Project recovery",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return startupPath;
        }
        var entry = entries.FirstOrDefault(e => e.Metadata is { } metadata &&
            (startupPath is null || string.Equals(metadata.OriginalPath, Path.GetFullPath(startupPath), StringComparison.OrdinalIgnoreCase)));
        if (entry?.Metadata is not { } snapshot)
        {
            if (startupPath is null && entries.Any(e => e.Error is not null))
                MessageBox.Show("Some interrupted recovery files could not be read. Use the recovery list to inspect their errors.",
                    "Project recovery", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return startupPath;
        }
        string name = snapshot.OriginalPath is null ? "an unnamed project" : Path.GetFileName(snapshot.OriginalPath);
        var choice = MessageBox.Show(
            $"Valencia found an interrupted session for {name} at revision {snapshot.Revision}, saved {snapshot.CapturedUtc.ToLocalTime():g}.\n" +
            $"Missing media files: {entry.MissingMedia}.\n\nYes opens the recovered edit. No shows actions for the last explicit save and the recovery copy. Cancel keeps the copy for later.",
            "Project recovery", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
        if (choice == DialogResult.Yes) return entry.File;
        if (choice == DialogResult.Cancel) return startupPath;

        var remaining = MessageBox.Show(
            "Yes opens the last explicit save if it is available. No discards the selected recovery generation. Cancel keeps the recovery generation for later.",
            "Project recovery", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (remaining == DialogResult.Yes && snapshot.OriginalPath is { } original && File.Exists(original)) return original;
        if (remaining == DialogResult.No)
        {
            try { RecoveryRun.Discard(root, entry.Id); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { MessageBox.Show(error.Message, "Could not discard recovery", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        return startupPath;
    }
}
