using System.Text.Json;
using Valencia.App.Editing;
using Xunit;

namespace Valencia.Tests;

public sealed class EditorDiagnosticsTests
{
    [Fact]
    public void ConcurrentRecordingIsBoundedAndPreservesFailureStack()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Valencia.Diagnostics", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "trace.json");
        try
        {
            var trace = new EditorDiagnostics(path);
            Parallel.For(0, 1000, i => trace.Record("seek", i, i * 100L));
            try { throw new InvalidOperationException("Regression probe"); }
            catch (InvalidOperationException error) { trace.Save(error); }
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(128, json.RootElement.GetProperty("events").GetArrayLength());
            Assert.Contains("ConcurrentRecordingIsBounded", json.RootElement.GetProperty("failure").GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
