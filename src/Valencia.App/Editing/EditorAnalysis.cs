using Valencia.Analysis;
using Valencia.Core.Timeline;
using Valencia.Engine;

namespace Valencia.App.Editing;

internal sealed partial class EditorWindow
{
    internal async Task AnalyzeSelected()
    {
        Playback?.Pause();
        CancelGesture();
        var asset = Editor.Session.Snapshot.Document.Assets.First(a => a.Id == (Editor.Selection?.AssetId ?? Editor.SelectedAsset));
        var chosen = asset.Streams.FirstOrDefault(s => s.Kind == TrackKind.Audio && s.Index == Editor.Selection?.StreamIndex)
            ?? asset.Streams.FirstOrDefault(s => s.Kind == TrackKind.Audio);
        await using var engine = new EditingEngine(Editor.Session, projectPath: Editor.ProjectPath);
        var token = jobCancellation?.Token ?? default;
        if (chosen is not null)
        {
            Guid jobId = engine.Analysis.Start(new(asset.Id, chosen.Index, "audio"));
            while (engine.Analysis.Status(jobId).State == "running")
            {
                Editor.Status = $"Analyzing stream {chosen.Index}: {engine.Analysis.Status(jobId).Progress:P0}";
                await Task.Delay(50, token);
            }
            var result = await engine.Analysis.WaitAsync(jobId, token);
            if (result.State != "completed") throw new InvalidOperationException(result.Error ?? result.State);
            var artifact = await engine.Analysis.ResultAsync(result.Key, token);
            var index = await Task.Run(() => new WaveformIndex(artifact), token);
            while (Editor.Waveforms.Count >= 4) Editor.Waveforms.Remove(Editor.Waveforms.First().Key);
            Editor.Waveforms[(asset.Id, chosen.Index)] = index;
            Editor.Status = $"Audio stream {chosen.Index}: {artifact.Silence.Length} silence intervals; {(result.CacheHit ? "cache hit" : "analyzed")}.";
        }
        if (asset.Streams.FirstOrDefault(s => s.Kind == TrackKind.Video) is { } video)
        {
            Guid jobId = engine.Analysis.Start(new(asset.Id, video.Index, "scenes"));
            string audioSummary = Editor.Status;
            while (engine.Analysis.Status(jobId).State == "running")
            {
                Editor.Status = $"Analyzing video stream {video.Index}: {engine.Analysis.Status(jobId).Progress:P0}";
                await Task.Delay(50, token);
            }
            var result = await engine.Analysis.WaitAsync(jobId, token);
            if (result.State != "completed") throw new InvalidOperationException(result.Error ?? result.State);
            var artifact = await engine.Analysis.ResultAsync(result.Key, token);
            Editor.Status = audioSummary + $" {artifact.Scenes.Length} visual-change candidates.";
        }
        lastUi = -1;
    }

    internal async Task ProposeRoughCut()
    {
        var assetId = Editor.Selection?.AssetId ?? Editor.SelectedAsset ?? throw new InvalidOperationException("Select media first.");
        var candidates = Editor.Waveforms.Where(p => p.Key.Asset == assetId);
        var waveform = Editor.Selection is { } clip && candidates.Any(p => p.Key.Stream == clip.StreamIndex)
            ? candidates.First(p => p.Key.Stream == clip.StreamIndex).Value : candidates.FirstOrDefault().Value;
        if (waveform is null) throw new InvalidOperationException("Analyze the selected audio stream first.");
        await using var engine = new EditingEngine(Editor.Session);
        var proposal = await engine.RoughCutAsync(Editor.SequenceId, waveform.Artifact.Key, new(), jobCancellation?.Token ?? default);
        Editor.ProposedCut = proposal.Valid && proposal.Plan is not null ? proposal : null;
        Editor.Status = Editor.ProposedCut is not null
            ? $"Proposed {proposal.Removed.Length} cuts; output {proposal.EstimatedDuration.TotalSeconds:0.###}s. Red ranges are removed. Apply or Cancel."
            : string.Join("; ", proposal.Diagnostics);
        lastUi = -1;
    }
    internal async Task ApplyRoughCut()
    {
        var proposal = Editor.ProposedCut ?? throw new InvalidOperationException("Preview a rough cut first.");
        await using var engine = new EditingEngine(Editor.Session);
        var result = engine.Plan(proposal.Plan!, true, jobCancellation?.Token ?? default);
        if (!result.Valid) throw new InvalidOperationException(string.Join("; ", result.Diagnostics));
        Editor.ProposedCut = null;
        Editor.SelectedClip = null;
        Editor.Status = "Rough cut committed as one undoable transaction.";
        Seek(Editor.Playhead);
    }
}
