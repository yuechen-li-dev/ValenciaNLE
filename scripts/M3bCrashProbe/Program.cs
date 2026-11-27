using Valencia.Core.Timeline;
using Valencia.Persistence;

if (args is not [var mode, var project, var control])
    throw new ArgumentException("Expected save|autosave|advance|stale, a test-owned project and a control path.");

var (loaded, version) = await ProjectWriteLease.OpenAsync(project);
var before = loaded.Snapshot;
var after = before with
{
    Revision = before.Revision + 1,
    Document = before.Document with { Name = mode is "save" or "autosave" ? new string('v', 32 * 1024 * 1024) : mode }
};
if (mode is "save" or "advance" or "stale")
{
    if (mode == "stale")
    {
        File.WriteAllText(control + ".ready", version.Sha256);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(control + ".go")) await Task.Delay(10, timeout.Token);
    }
    using var lease = ProjectWriteLease.Acquire(project);
    await lease.SaveAsync(after, version);
}
else if (mode == "autosave")
{
    using var autosave = new ProjectAutosave(control, TimeSpan.Zero, TimeSpan.Zero);
    await Capture(before);
    await Capture(after);

    async Task Capture(ProjectSnapshot snapshot)
    {
        while (true)
        {
            var status = autosave.Observe(snapshot, true, project);
            if (status?.Error is { } error) throw new IOException(error);
            if (status?.Revision == snapshot.Revision) return;
            await Task.Delay(10);
        }
    }
}
else
{
    throw new ArgumentException("Expected save, autosave, advance or stale.");
}
