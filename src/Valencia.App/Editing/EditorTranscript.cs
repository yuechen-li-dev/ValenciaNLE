using Valencia.Core;
using System.Collections.Immutable;
using Valencia.Core.Timeline;
using Valencia.Engine;

namespace Valencia.App.Editing;

internal sealed partial class EditorWindow
{
    internal async Task TranscribeSelected()
    {
        Playback?.Pause(); CancelGesture();
        var asset = Editor.Session.Snapshot.Document.Assets.First(a => a.Id == (Editor.Selection?.AssetId ?? Editor.SelectedAsset));
        var stream = asset.Streams.FirstOrDefault(s => s.Kind == TrackKind.Audio && s.Index == Editor.Selection?.StreamIndex)
            ?? asset.Streams.First(s => s.Kind == TrackKind.Audio);
        await using var engine = new EditingEngine(Editor.Session);
        Guid id = engine.Analysis.Start(new(asset.Id, stream.Index, "transcript"));
        var token = jobCancellation?.Token ?? default;
        while (engine.Analysis.Status(id).State == "running")
        {
            Editor.Status = $"Local transcription: {engine.Analysis.Status(id).Progress:P0}";
            await Task.Delay(50, token);
        }
        var result = await engine.Analysis.WaitAsync(id, token);
        if (result.State != "completed") throw new InvalidOperationException(result.Error ?? result.State);
        Editor.Transcript = await engine.Analysis.ResultAsync(result.Key, token);
        Editor.TranscriptMode = true; Editor.TranscriptFirstWord = 0;
        Editor.TranscriptSelectionStart = null; Editor.TranscriptSelectionEnd = null;
        Editor.Status = "Transcript ready. [?] marks unsupported word timing. Shift-click selects a range.";
        lastUi = -1;
    }
    internal void FindTranscript(string? search = null)
    {
        if (Editor.Transcript is null) { Editor.Status = "Transcribe selected media first."; return; }
        if (search is null)
        {
            Playback?.Pause(); CancelGesture(); keys.Reset();
            using var form = new Form { Text = "Find spoken words", Width = 440, Height = 140, StartPosition = FormStartPosition.CenterScreen };
            var text = new TextBox { Left = 12, Top = 12, Width = 396 };
            var button = new Button { Text = "Find", DialogResult = DialogResult.OK, Left = 312, Top = 44 };
            form.Controls.Add(text); form.Controls.Add(button); form.AcceptButton = button;
            search = form.ShowDialog() == DialogResult.OK ? text.Text : null;
            keys.Reset();
        }
        if (string.IsNullOrWhiteSpace(search)) return;
        var match = TranscriptQueries.Search(Editor.Transcript, search, limit: 1).FirstOrDefault();
        if (match is null) { Editor.Status = "No transcript match."; return; }
        Editor.TranscriptFirstWord = match.FirstWord;
        Editor.TranscriptSelectionStart = match.FirstWord;
        Editor.TranscriptSelectionEnd = match.FirstWord + match.WordCount - 1;
        Editor.Status = match.Text;
        NavigateTranscript(match.FirstWord);
    }
    private void SelectTranscriptWord(int index)
    {
        if (Editor.Transcript?.Transcript is not { } data || index < 0 || index >= data.Words.Length) return;
        if (!keys.Shift || Editor.TranscriptSelectionStart is null) Editor.TranscriptSelectionStart = index;
        Editor.TranscriptSelectionEnd = index;
        NavigateTranscript(index);
    }
    private void NavigateTranscript(int index)
    {
        var artifact = Editor.Transcript!;
        var word = artifact.Transcript!.Words[index];
        if (!word.TimingUsable) { Editor.Status = "This word has no usable native alignment."; return; }
        var times = Editor.Sequence.Tracks.SelectMany(t => t.Clips).Where(c => c.AssetId == artifact.AssetId && word.Range.Start.CompareTo(c.SourceIn) >= 0 && word.Range.Start.CompareTo(c.SourceOut) < 0)
            .Select(c => c.MapToTimeline(word.Range.Start)).Distinct().ToArray();
        if (times.Length != 1) { Editor.Status = "No unique timeline occurrence; choose an explicit occurrence through the engine/CLI."; return; }
        Seek(times[0]);
    }
    internal async Task ProposeTextCut(string mode)
    {
        var artifact = Editor.Transcript ?? throw new InvalidOperationException("No transcript.");
        int first = Math.Min(Editor.TranscriptSelectionStart ?? -1, Editor.TranscriptSelectionEnd ?? -1);
        int last = Math.Max(Editor.TranscriptSelectionStart ?? -1, Editor.TranscriptSelectionEnd ?? -1);
        if (first < 0) throw new InvalidOperationException("Select transcript words first.");
        var words = artifact.Transcript!.Words.Skip(first).Take(last - first + 1).Select(w => w.Id).ToImmutableArray();
        await using var engine = new EditingEngine(Editor.Session);
        var proposal = await engine.TextEditAsync(new(Editor.SequenceId, artifact.Key, mode, words), jobCancellation?.Token ?? default);
        if (proposal.Plan is null || proposal.Preview?.Valid != true) throw new InvalidOperationException(EngineJson.Write(proposal));
        Editor.ProposedTextCut = proposal;
        Editor.ProposedCut = null;
        Editor.Status = "Text edit preview: red ranges will be removed. Apply or Cancel.";
        double removedSeconds = proposal.Removed.Sum(r => (r.End.Ticks - r.Start.Ticks) / (double)SequenceTime.TicksPerSecond);
        ShowProposal("Text edit", proposal.Removed, Editor.Sequence.Duration.TotalSeconds - removedSeconds);
        lastUi = -1;
    }
    internal async Task ApplyTextCut()
    {
        await using var engine = new EditingEngine(Editor.Session);
        var proposal = Editor.ProposedTextCut ?? throw new InvalidOperationException("No text edit preview.");
        var result = engine.Plan(proposal.Plan!, true, jobCancellation?.Token ?? default);
        if (!result.Valid) throw new InvalidOperationException(EngineJson.Write(result));
        Editor.ProposedTextCut = null; Editor.SelectedClip = null;
        Editor.Status = "Text edit committed as one undoable transaction.";
        Seek(Editor.Playhead);
    }
}
