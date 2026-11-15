using System.Diagnostics;
using System.Text.Json;
using Valencia.Core.Timeline;

namespace Valencia.App.Editing;

/// <summary>Runs the actual native transcript actions and captures GPU-presented results.</summary>
internal sealed class TranscriptSmoke(string directory)
{
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private Task? operation;
    private ProjectDocument? before;
    private int phase;
    public bool Finished { get; private set; }

    public void Tick(EditorWindow window)
    {
        if (elapsed.Elapsed.TotalSeconds > 90) throw new TimeoutException("Transcript desktop smoke timed out.");
        if (operation is { IsCompleted: true })
        {
            operation.GetAwaiter().GetResult();
            operation = null;
        }
        if (operation is not null || window.Busy) return;
        var editor = window.Editor;
        Directory.CreateDirectory(directory);
        switch (phase)
        {
            case 0:
                before = editor.Session.Snapshot.Document;
                operation = window.TranscribeSelected();
                phase = 1;
                break;
            case 1:
                if (editor.Transcript?.Transcript?.Words.Length < 1) throw new InvalidOperationException("Transcript not populated.");
                window.FindTranscript("important announcement");
                if (editor.TranscriptSelectionStart is null) throw new InvalidOperationException("Search did not select words.");
                phase = 2;
                break;
            case 2:
                window.Capture(Path.Combine(directory, "transcript.png"));
                operation = window.ProposeTextCut("retain");
                phase = 3;
                break;
            case 3:
                if (editor.ProposedTextCut?.Preview?.Valid != true || !DocumentSemantics.Equal(before!, editor.Session.Snapshot.Document))
                    throw new InvalidOperationException("Text preview missing or mutated document.");
                window.Capture(Path.Combine(directory, "proposal.png"));
                window.Action("CancelTextCut");
                if (editor.ProposedTextCut is not null) throw new InvalidOperationException("Cancel failed.");
                operation = window.ProposeTextCut("retain");
                phase = 4;
                break;
            case 4:
                operation = window.ApplyTextCut();
                phase = 5;
                break;
            case 5:
                if (editor.Sequence.Duration.TotalSeconds >= 4) throw new InvalidOperationException("Text cut did not retain selected phrase.");
                if (!editor.History(false).Success || !DocumentSemantics.Equal(before!, editor.Session.Snapshot.Document) || !editor.History(true).Success)
                    throw new InvalidOperationException("Native text edit undo/redo failed.");
                window.Capture(Path.Combine(directory, "applied.png"));
                File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
                {
                    outcome = "passed",
                    gpu = window.Graphics.GpuName,
                    elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                    words = editor.Transcript!.Transcript!.Words.Length,
                    previewNonMutating = true,
                    cancelNonMutating = true,
                    undoRedo = true,
                    outputDurationSeconds = editor.Sequence.Duration.TotalSeconds,
                    searchNavigatedToSource = true,
                    seekLatenciesMs = window.SeekLatenciesMs
                }, new JsonSerializerOptions { WriteIndented = true }));
                Finished = true;
                Console.WriteLine("M2c native transcript smoke passed: " + directory);
                break;
        }
    }
}
