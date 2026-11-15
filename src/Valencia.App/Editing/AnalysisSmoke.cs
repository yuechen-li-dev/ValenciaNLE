using System.Diagnostics;
using System.Text.Json;
using Valencia.Core.Timeline;

namespace Valencia.App.Editing;

internal sealed class AnalysisSmoke(string directory)
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly List<double> renderCost = [];
    private int phase;
    private Task? operation;
    private ProjectDocument? before;
    public bool Finished { get; private set; }
    public void Tick(EditorWindow window)
    {
        if (elapsed.Elapsed.TotalSeconds > 90) throw new TimeoutException("Analysis desktop smoke timed out.");
        if (operation is { IsCompleted: true }) { operation.GetAwaiter().GetResult(); operation = null; }
        if (operation is not null || window.Busy) return;
        var editor = window.Editor;
        Directory.CreateDirectory(directory);
        switch (phase)
        {
            case 0:
                before = editor.Session.Snapshot.Document;
                operation = window.AnalyzeSelected(); phase = 1; break;
            case 1:
                if (editor.Waveforms.Count == 0) throw new InvalidOperationException("Native waveform was not loaded.");
                operation = window.ProposeRoughCut(); phase = 2; break;
            case 2:
                if (editor.ProposedCut is null || !DocumentSemantics.Equal(before!, editor.Session.Snapshot.Document)) throw new InvalidOperationException("Rough cut preview missing or mutated session.");
                window.Capture(Path.Combine(directory, "proposal.png"));
                window.Action("CancelCut");
                if (editor.ProposedCut is not null) throw new InvalidOperationException("Cancel failed.");
                operation = window.ProposeRoughCut(); phase = 3; break;
            case 3:
                operation = window.ApplyRoughCut(); phase = 4; break;
            case 4:
                if (editor.Sequence.Duration.TotalSeconds >= 6) throw new InvalidOperationException("Rough cut did not shorten the sequence.");
                if (!editor.History(false).Success || !DocumentSemantics.Equal(before!, editor.Session.Snapshot.Document) || !editor.History(true).Success)
                    throw new InvalidOperationException("Rough cut undo/redo failed.");
                window.Capture(Path.Combine(directory, "waveforms.png"));
                for (int i = 0; i < 120; i++) renderCost.Add(window.MeasureWaveformFrame());
                File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new {
                    outcome = "passed", gpu = window.Graphics.GpuName, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                    waveformStreams = editor.Waveforms.Count, waveformQuads = window.CanvasCount,
                    waveformFrameMedianMs = renderCost.Order().ElementAt(60), waveformFrameP95Ms = renderCost.Order().ElementAt(114),
                    outputDurationSeconds = editor.Sequence.Duration.TotalSeconds, previewNonMutating = true, cancelNonMutating = true, undoRedo = true
                }, new JsonSerializerOptions { WriteIndented = true }));
                Finished = true;
                Console.WriteLine("M2b native analysis smoke passed: " + directory);
                break;
        }
    }
}
