using System.Diagnostics;
using System.Text.Json;

namespace Valencia.App.Editing;

/// <summary>A bounded flight recorder, including failures that escape a native callback. No media data is retained.</summary>
internal sealed class EditorDiagnostics(string? logPath = null)
{
    private readonly object gate = new();
    private readonly Queue<object> events = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly string destination = logPath ?? LogPath;
    private string? lastFailure;
    public string DirectoryPath => Path.GetDirectoryName(destination)!;
    public static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valencia", "Logs", "last-editor.json");
    public void Record(string phase, long generation, long ticks, string? detail = null)
    {
        lock (gate)
        {
            if (events.Count == 128) events.Dequeue();
            events.Enqueue(new { elapsedMs = clock.Elapsed.TotalMilliseconds, phase, generation, ticks, detail });
        }
    }
    public void Save(Exception? failure = null)
    {
        try
        {
            lock (gate)
            {
                if (failure is not null) lastFailure = failure.ToString();
                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(destination, JsonSerializer.Serialize(new
                {
                    process = Environment.ProcessId, utc = DateTimeOffset.UtcNow, runtime = Environment.Version.ToString(),
                    failure = lastFailure, events = events.ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
