using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Valencia.Analysis;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.Engine;

public sealed partial class EditingEngine
{
    private readonly object creatorGate = new();
    private readonly Dictionary<string, CreatorProposal> creatorPlans = [];

    public async Task<CreatorProposal> CreateCreatorAsync(CreatorRequest request, CancellationToken cancellation = default, long? expectedRevision = null)
    {
        var before = Session.Snapshot;
        if (expectedRevision is { } expected && before.Revision != expected) throw new ArgumentException("Stale creator revision.");
        var transcript = await Analysis.ResultAsync(request.TranscriptKey, cancellation).ConfigureAwait(false);
        var audio = await Analysis.ResultAsync(request.AudioKey, cancellation).ConfigureAwait(false);
        var scenes = request.SceneKey is { } sceneKey ? await Analysis.ResultAsync(sceneKey, cancellation).ConfigureAwait(false) : null;
        var source = before.Document.Assets.First(a => a.Id == transcript.AssetId);
        var imported = await Task.Run(() => Valencia.Media.MediaAssetProbe.Import(source.Id, source.SourcePath), cancellation).ConfigureAwait(false);
        var candidates = await Task.Run(() => CreatorPlanning.Candidates(transcript, audio, scenes, request, imported.Duration), cancellation).ConfigureAwait(false);
        var built = await Task.Run(() => CreatorPlanning.Plan(before, request, transcript, audio, candidates), cancellation).ConfigureAwait(false);
        var plan = built.Plan;
        var preview = Plan(plan, false, cancellation);
        if (!preview.Valid) throw new ArgumentException(string.Join("; ", preview.Diagnostics));
        LoudnessData? loudness = null;
        if (request.Normalize)
        {
            var simulation = new EditingSession(before, 0);
            var applied = simulation.Execute(new EditTransaction(Guid.NewGuid(), 1, before.Revision,
                [.. plan.Operations.Select(o => new EditCommand(Guid.NewGuid(), 1, o.ToCore(plan.SequenceId)))]));
            if (!applied.Success) throw new ArgumentException(applied.Message);
            loudness = await Task.Run(() => LoudnessAnalyzer.SequenceAsync(simulation.Snapshot, plan.SequenceId, cancellation), cancellation).ConfigureAwait(false);
            loudness = loudness with { TargetLufs = request.TargetLufs, PeakCeilingDb = request.PeakCeilingDb };
            var settings = built.Settings with { MasterGainDb = loudness.SafeGainDb ?? 0 };
            plan = plan with { Operations = plan.Operations.SetItem(plan.Operations.Length - 1, new CreatorPlan(settings)) };
            preview = Plan(plan, false, cancellation);
            if (!preview.Valid) throw new ArgumentException(string.Join("; ", preview.Diagnostics));
        }
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(EngineJson.Write(plan))));
        var proposal = new CreatorProposal(key, plan, preview, candidates, built.Segments, built.Settings.Profiles,
            preview.ResultingDuration!.Value, loudness?.IntegratedLufs is { } measured && loudness.SafeGainDb is { } gain && gain < loudness.TargetLufs - measured - .01
                ? built.Diagnostics.Add("Normalization gain is constrained by true-peak safety; the LUFS target cannot be reached by static gain alone.") : built.Diagnostics, loudness);
        lock (creatorGate)
        {
            while (creatorPlans.Count >= 4) creatorPlans.Remove(creatorPlans.First().Key);
            creatorPlans[key] = proposal;
        }
        return proposal;
    }
    public CreatorProposal CreatorProposal(string key) { lock (creatorGate) return creatorPlans[key]; }
    public PlanResult ApplyCreator(string key, CancellationToken cancellation = default) => Plan(CreatorProposal(key).Plan, true, cancellation);
    public async Task<CreatorProposal> CreateCaptionsAsync(Guid sequenceId, string transcriptKey, CancellationToken cancellation = default)
    {
        var before = Session.Snapshot;
        var artifact = await Analysis.ResultAsync(transcriptKey, cancellation).ConfigureAwait(false);
        var data = artifact.Transcript ?? throw new ArgumentException("Transcript required.");
        var sequence = Query().Sequence(sequenceId);
        var clips = sequence.Tracks.SelectMany(t => t.Clips).Where(c => c.AssetId == artifact.AssetId).ToArray();
        var words = data.Words.Where(w => w.TimingUsable && w.Range.Epoch == 0 && clips.Any(c => c.SourceIn.CompareTo(w.Range.End) < 0 && c.SourceOut.CompareTo(w.Range.Start) > 0))
            .Select(w => new CaptionWord(w.Id, artifact.AssetId, artifact.StreamIndex, w.Range.Start, w.Range.End, w.Text)).ToImmutableArray();
        var settings = sequence.Creator is { } current ? current with { Words = words } : new CreatorSettings(1, words, new(), [], []);
        var plan = new EditPlan(1, before.Document.Id, EngineJson.Exact(before.Revision), sequenceId, [new CreatorPlan(settings)],
            "Source-aligned captions; unusable timings omitted", [new(artifact.AssetId, artifact.Fingerprint)]);
        var preview = Plan(plan, false, cancellation);
        if (!preview.Valid) throw new ArgumentException(string.Join("; ", preview.Diagnostics));
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(EngineJson.Write(plan))));
        var proposal = new CreatorProposal(key, plan, preview, [], [], settings.Profiles, sequence.Duration, []);
        lock (creatorGate)
        {
            while (creatorPlans.Count >= 4) creatorPlans.Remove(creatorPlans.First().Key);
            creatorPlans[key] = proposal;
        }
        return proposal;
    }
    public static object CreatorSummary(CreatorProposal proposal) => new { proposal.Key, valid = proposal.Preview.Valid, proposal.Preview.ExpectedRevision,
        proposal.Preview.CanCommit, proposal.EstimatedDuration, proposal.Outputs, segments = proposal.Segments,
        candidateCount = proposal.Candidates.Length, candidates = proposal.Candidates.Take(16), operationCount = proposal.Plan.Operations.Length,
        proposal.Diagnostics, proposal.Loudness };
    public async Task<ExportStatus[]> ExportProfilesAsync(Guid sequenceId, string directory, CancellationToken cancellation = default,
        string? encoderId = null)
    {
        directory = OutputPath(directory);
        var snapshot = Session.Snapshot;
        var profiles = new TimelineQueries(snapshot).Sequence(sequenceId).Creator?.Profiles ?? [];
        if (profiles.IsEmpty) throw new ArgumentException("No output profiles configured.");
        var encoder = encoderId is not null || FfmpegRuntime.IsDistribution
            ? ExportCatalog.Encoder(encoderId ?? "vp9")
            : null;
        var finish = new AudioFinish();
        var results = new List<ExportStatus>();
        foreach (var batch in profiles.Chunk(4))
        {
            var ids = new List<Guid>();
            try
            {
                foreach (var p in batch)
                {
                    string name = p.Name.Length > 0 && p.Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                        ? p.Name : p.Id.ToString("N");
                    string output = Path.Combine(directory, name + (encoder?.Extension ?? ".mp4"));
                    if (encoder is null)
                        ids.Add(Exports.Start(sequenceId, Policy is null ? output : Path.GetRelativePath(Policy.Root, output), p.Id));
                    else
                        ids.Add((await Exports.EnqueueAsync(snapshot, new(sequenceId, output, p.Id, encoder.Id, null, finish, true, null),
                            cancellation).ConfigureAwait(false)).JobId);
                }
                foreach (var id in ids)
                {
                    var result = await Exports.WaitAsync(id, cancellation).ConfigureAwait(false);
                    if (result.State != "completed") throw new InvalidOperationException(result.Error ?? result.State);
                    results.Add(result);
                }
            }
            catch { foreach (var id in ids) Exports.Cancel(id); throw; }
        }
        return [.. results];
    }
    public async Task<AnalysisArtifact> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellation = default)
    {
        Guid id = Analysis.Start(request);
        try
        {
            var status = await Analysis.WaitAsync(id, cancellation).ConfigureAwait(false);
            if (status.State != "completed") throw new InvalidOperationException(status.Error ?? status.State);
            return await Analysis.ResultAsync(status.Key, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { Analysis.Cancel(id); throw; }
    }
    public EditResult AttachProxy(Guid assetId, string key, long revision, CancellationToken cancellation = default)
    {
        var asset = Query().Assets.First(a => a.Id == assetId);
        if (Valencia.Media.MediaProxy.Read(key) is null) throw new ArgumentException("Proxy is unavailable.");
        return Execute(new EditCommand(Guid.NewGuid(), 1, new RelinkAsset(asset with { ProxyKey = key }), revision), cancellation);
    }
    public async Task<AnalysisArtifact> CorrectTranscriptAsync(string key, IReadOnlyDictionary<Guid, string> corrections, CancellationToken cancellation = default)
    {
        var artifact = await Analysis.ResultAsync(key, cancellation).ConfigureAwait(false);
        var data = artifact.Transcript ?? throw new ArgumentException("Transcript required.");
        if (corrections.Count is < 1 or > 1000 || corrections.Any(c => c.Value.Length is < 1 or > 128 || !data.Words.Any(w => w.Id == c.Key)))
            throw new ArgumentException("Invalid bounded transcript correction.");
        string correctionKey = AnalysisCache.Key(artifact.AssetId, artifact.Fingerprint, artifact.StreamIndex, "transcript-correction-v1",
            key + EngineJson.Write(corrections.OrderBy(c => c.Key)));
        var words = data.Words.Select(w => corrections.TryGetValue(w.Id, out var text) ? w with { Text = text, OriginalText = w.OriginalText ?? w.Text } : w).ToImmutableArray();
        var segments = data.Segments.Select(s => s with { Text = string.Join(" ", words.Skip(s.FirstWord).Take(s.WordCount).Select(w => w.Text)) }).ToImmutableArray();
        var corrected = artifact with { Key = correctionKey, Algorithm = artifact.Algorithm + "/text-correction-v1", Transcript = data with { Words = words, Segments = segments } };
        await Analysis.Cache.PublishAsync(corrected, cancellation).ConfigureAwait(false);
        return corrected;
    }
}
