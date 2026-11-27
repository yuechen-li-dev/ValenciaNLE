using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Engine;
using System.Collections.Immutable;
using Valencia.Core.Editing;

namespace Valencia.Scripting;

/// <summary>Trusted CLR script surface. Native long overloads preserve Int64; wire contracts retain canonical strings.</summary>
public sealed class ScriptApi
{
    private readonly EditingEngine engine;
    private readonly CancellationToken cancellation;
    private readonly bool allowProjectReplacement;
    private readonly WorkflowContext? workflow;

    public ScriptApi(EditingEngine engine, CancellationToken cancellation = default, bool allowProjectReplacement = true)
        : this(engine, cancellation, allowProjectReplacement, null) { }

    internal ScriptApi(EditingEngine engine, CancellationToken cancellation, bool allowProjectReplacement, WorkflowContext? workflow)
    {
        this.engine = engine;
        this.cancellation = cancellation;
        this.allowProjectReplacement = allowProjectReplacement;
        this.workflow = workflow;
    }

    public string Argument(string name, string fallback)
    {
        Check();
        return workflow?.Arguments.GetValueOrDefault(name, fallback) ?? fallback;
    }

    public string SelectedSequence()
    {
        Check();
        return workflow?.SequenceId is { } sequence ? sequence.ToString() : FirstSequence();
    }

    public void Checkpoint() => Check();

    public string CreateSequence(string name, int width, int height, int fpsNumerator, int fpsDenominator, int audioSampleRate)
    {
        var id = Guid.NewGuid();
        ApplyOperations([new CreateSequence(id, name, new(width, height, new(fpsNumerator, fpsDenominator)), audioSampleRate)]);
        return id.ToString();
    }

    public string InsertAsset(string asset, string sequence, long at)
    {
        Check();
        var snapshot = engine.Session.Snapshot;
        Guid assetId = Guid.Parse(asset);
        var media = snapshot.Document.Assets.First(item => item.Id == assetId);
        var imported = Valencia.Media.MediaAssetProbe.Import(media.Id, engine.OutputPath(media.SourcePath));
        var operations = InsertionCommands.Build(snapshot, Guid.Parse(sequence), imported, new(at));
        ApplyOperations(operations, snapshot.Revision);
        return asset;
    }

    public string AddMarker(string sequence, long at, string label)
    {
        var marker = new SequenceMarker(Guid.NewGuid(), new(at), label);
        ApplyOperations([new SetSequenceMarker(Guid.Parse(sequence), marker)]);
        return marker.Id.ToString();
    }

    public void RemoveMarker(string sequence, string marker)
        => ApplyOperations([new RemoveSequenceMarker(Guid.Parse(sequence), Guid.Parse(marker))]);

    public string Markers(string sequence)
    {
        Check();
        return EngineJson.Write(engine.Query().Sequence(Guid.Parse(sequence)).Markers
            .Select(marker => new { marker.Id, At = EngineJson.Exact(marker.At.Ticks), marker.Label }));
    }

    private void ApplyOperations(IEnumerable<EditOperation> operations, long? revision = null)
    {
        Check();
        var result = engine.Execute(new EditTransaction(Guid.NewGuid(), 1, revision ?? engine.Session.Snapshot.Revision,
            [.. operations.Select(operation => new EditCommand(Guid.NewGuid(), 1, operation))]), cancellation);
        if (!result.Success) throw new InvalidOperationException(result.Message ?? result.Error.ToString());
    }

    private void RequireDirectScript()
    {
        Check();
        if (workflow is not null) throw new InvalidOperationException("Saving, exporting and OBS control are direct script operations. Publish the workflow edit first.");
    }

    public string ObsConnect(string endpoint, string recordingRoot, string manifest, bool allowRecordingControl)
    {
        RequireDirectScript();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("OBS credentials currently require Windows.");
        var stored = Valencia.Obs.ObsCredentials.Load();
        var options = endpoint.Length == 0 ? stored?.Options ?? new(new Uri("ws://127.0.0.1:4455"))
            : new Valencia.Obs.ObsConnectionOptions(new Uri(endpoint));
        var workflow = engine.ConnectObsAsync(options, Environment.GetEnvironmentVariable("VALENCIA_OBS_PASSWORD") ?? stored?.Password,
            manifest, recordingRoot, allowRecordingControl, cancellation).GetAwaiter().GetResult();
        return EngineJson.Write(workflow.Recording.Client.Status);
    }
    public string ObsInfo() { Check(); return EngineJson.Write(RequireObs().DiscoverAsync(cancellation).GetAwaiter().GetResult()); }
    public void ObsStart() { Check(); RequireObs().StartAsync(cancellation).GetAwaiter().GetResult(); }
    public void ObsStop() { Check(); RequireObs().StopAsync(cancellation).GetAwaiter().GetResult(); }
    public void ObsPause(bool pause) { Check(); RequireObs().PauseAsync(pause, cancellation).GetAwaiter().GetResult(); }
    public string ObsMarker(string note) { Check(); return EngineJson.Write(RequireObs().MarkAsync(note, cancellation: cancellation).GetAwaiter().GetResult()); }
    public string ObsImport() { Check(); return EngineJson.Write(RequireObs().ImportAsync(engine.Session.Snapshot.Revision, cancellation).GetAwaiter().GetResult()); }
    public string ObsManifest() { Check(); return EngineJson.Write(RequireObs().Recording.Snapshot); }
    public void ObsDisconnect() { Check(); engine.DisconnectObsAsync().GetAwaiter().GetResult(); }
    private ObsWorkflow RequireObs() => engine.Obs ?? throw new InvalidOperationException("Connect OBS explicitly first.");
    public void OpenProject(string path)
    {
        Check();
        if (!allowProjectReplacement) throw new InvalidOperationException("This UI script scope edits the current project; use the native Open command to replace it.");
        var result = engine.OpenIntoAsync(path, engine.Session.Snapshot.Revision, cancellation).GetAwaiter().GetResult();
        if (!result.Success) throw new InvalidOperationException(result.Message);
    }
    public string Duration(string sequence) { Check(); return EngineJson.Exact(engine.Query().Duration(Guid.Parse(sequence)).Ticks); }
    public long DurationTicks(string sequence) { Check(); return engine.Query().Duration(Guid.Parse(sequence)).Ticks; }
    public long RevisionValue() { Check(); return engine.Session.Snapshot.Revision; }
    public string ActiveAt(string sequence, long ticks) => ActiveAt(sequence, EngineJson.Exact(ticks));
    public string ActiveAt(string sequence, string ticks) { Check(); return EngineJson.Write(engine.Query().Evaluate(Guid.Parse(sequence), new(EngineJson.Int64(ticks)))); }
    public string Intersecting(string sequence, string start, string end)
    {
        Check();
        return EngineJson.Write(engine.Query().Intersecting(Guid.Parse(sequence), new(EngineJson.Int64(start)), new(EngineJson.Int64(end))));
    }
    public string Assets() { Check(); return EngineJson.Write(engine.Query().Assets); }
    public string MissingMedia() { Check(); return EngineJson.Write(engine.MissingMedia()); }
    public string TrackByName(string sequence, string name) { Check(); return engine.Query().Tracks(Guid.Parse(sequence)).First(t => t.Name == name).Id.ToString(); }
    public string Info() { Check(); return EngineJson.Write(new { apiVersion = 1, project = engine.Session.Snapshot, missingMedia = engine.MissingMedia() }); }
    public string FirstSequence() { Check(); return engine.Query().Sequences.First().Id.ToString(); }
    public string FirstClip(string sequence) { Check(); return engine.Query().Tracks(Guid.Parse(sequence)).SelectMany(t => t.Clips).First().Id.ToString(); }
    public string Tracks(string sequence) { Check(); return EngineJson.Write(engine.Query().Tracks(Guid.Parse(sequence))); }
    public int TrackCount(string sequence) { Check(); return engine.Query().Tracks(Guid.Parse(sequence)).Length; }
    public string Revision() { Check(); return EngineJson.Exact(engine.Session.Snapshot.Revision); }
    public string ProjectId() { Check(); return engine.Session.Snapshot.Document.Id.ToString(); }
    public string ProjectFile() { Check(); return engine.ProjectPath ?? throw new InvalidOperationException("No project file in this scope."); }
    public string FirstTrack(string sequence, string kind) { Check(); return engine.Query().Tracks(Guid.Parse(sequence)).First(t => t.Kind == Enum.Parse<TrackKind>(kind, true)).Id.ToString(); }
    public string FirstClipOnTrack(string sequence, string track) { Check(); return engine.Query().Clips(Guid.Parse(sequence), Guid.Parse(track)).First().Id.ToString(); }
    public string[] Sequences() { Check(); return [.. engine.Query().Sequences.Select(s => s.Id.ToString())]; }
    public string[] Clips(string sequence) { Check(); return [.. engine.Query().Tracks(Guid.Parse(sequence)).SelectMany(t => t.Clips).Select(c => c.Id.ToString())]; }
    public string MediaInfo(string path)
    {
        Check();
        return EngineJson.Write(Valencia.Media.MediaAssetProbe.Read(Guid.NewGuid(), engine.OutputPath(path)));
    }
    public string Import(string path) => Import(path, Guid.NewGuid().ToString());

    public string Import(string path, string assetId)
    {
        Check();
        var id = Guid.Parse(assetId);
        var result = engine.ImportAsync(id, path, engine.Session.Snapshot.Revision, cancellation).GetAwaiter().GetResult();
        if (!result.Success) throw new InvalidOperationException(result.Message);
        return id.ToString();
    }
    public string NewId() { Check(); return Guid.NewGuid().ToString(); }
    public string CanonicalTicks(string value) { Check(); return EngineJson.Exact(EngineJson.Int64(value)); }
    public string AddTicks(string left, string right) { Check(); return EngineJson.Exact(checked(EngineJson.Int64(left) + EngineJson.Int64(right))); }
    public long AddTicks(long left, long right) { Check(); return checked(left + right); }
    public long Frames(long frames, int numerator, int denominator)
    {
        Check();
        return TimelineTime.FromFrames(frames, new(numerator, denominator)).Ticks;
    }
    public string Frames(string frames, int numerator, int denominator)
    {
        Check();
        return EngineJson.Exact(TimelineTime.FromFrames(EngineJson.Int64(frames), new(numerator, denominator)).Ticks);
    }
    public PlanBuilder Plan(string sequence)
    {
        Check();
        var snapshot = engine.Session.Snapshot;
        _ = engine.Query().Sequence(Guid.Parse(sequence));
        return new(snapshot.Document.Id, EngineJson.Exact(snapshot.Revision), Guid.Parse(sequence));
    }
    public string Validate(PlanBuilder plan) { Check(); return EngineJson.Write(engine.Plan(plan.Build(), false, cancellation)); }
    public string ListEffects() { Check(); return EngineJson.Write(new { version = EffectCatalog.Version, effects = EffectCatalog.Effects }); }
    public string Apply(PlanBuilder plan)
    {
        Check();
        var result = engine.Plan(plan.Build(), true, cancellation);
        if (!result.Valid) throw new InvalidOperationException(string.Join("; ", result.Diagnostics));
        return EngineJson.Write(result);
    }
    public string ApplyPlan(string json)
    {
        Check();
        var result = engine.Plan(EditPlanSchema.Parse(json), true, cancellation);
        if (!result.Valid) throw new InvalidOperationException(string.Join("; ", result.Diagnostics));
        return EngineJson.Write(result);
    }
    public string ValidatePlan(string json)
    {
        Check();
        return EngineJson.Write(engine.Plan(EditPlanSchema.Parse(json), false, cancellation));
    }
    public string Undo()
    {
        var result = engine.Undo(engine.Session.Snapshot.Revision, cancellation);
        if (!result.Success) throw new InvalidOperationException(result.Message);
        return EngineJson.Exact(result.Revision);
    }
    public string Redo()
    {
        var result = engine.Redo(engine.Session.Snapshot.Revision, cancellation);
        if (!result.Success) throw new InvalidOperationException(result.Message);
        return EngineJson.Exact(result.Revision);
    }
    public void Save(string path) { RequireDirectScript(); engine.SaveAsync(path, cancellation).GetAwaiter().GetResult(); }
    public string ReviewFrame(PlanBuilder plan, long at, string output, string profileId, int maximumEdge) =>
        RenderReview(plan, new("frame", [at], ProfileId: ParseProfile(profileId), MaxEdge: maximumEdge), output);

    public string ReviewContactSheet(PlanBuilder plan, string sampleTicks, string output, string profileId, int maximumEdge) =>
        RenderReview(plan, new("contactSheet", [.. sampleTicks.Split(',', 13).Select(EngineJson.Int64)],
            ProfileId: ParseProfile(profileId), MaxEdge: maximumEdge), output);

    public string ReviewVideo(PlanBuilder plan, long start, long end, string output, string profileId, int maximumEdge) =>
        RenderReview(plan, new("video", StartTicks: start, EndTicks: end, ProfileId: ParseProfile(profileId), MaxEdge: maximumEdge), output);

    private static Guid? ParseProfile(string profileId) => profileId.Length == 0 ? null : Guid.Parse(profileId);

    private string RenderReview(PlanBuilder plan, ReviewSelection selection, string output)
    {
        RequireDirectScript();
        var edit = plan.Build();
        var result = engine.ReviewAsync(edit.ProjectId, edit.SequenceId, EngineJson.Int64(edit.ProjectRevision),
            selection, output, edit, cancellation).GetAwaiter().GetResult();
        return EngineJson.Write(result);
    }
    public string Export(string sequence, string path) { RequireDirectScript(); return engine.Exports.Start(Guid.Parse(sequence), path).ToString(); }
    public string ExportStatus(string job) { Check(); return EngineJson.Write(engine.Exports.Status(Guid.Parse(job))); }
    public void CancelExport(string job) { Check(); engine.Exports.Cancel(Guid.Parse(job)); }
    public string WaitExport(string job)
    {
        Check();
        var id = Guid.Parse(job);
        ExportStatus result;
        try { result = engine.Exports.WaitAsync(id, cancellation).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { engine.Exports.Cancel(id); throw; }
        if (result.State != "completed") throw new InvalidOperationException(result.Error ?? result.State);
        return EngineJson.Write(result);
    }
    private void Check() => cancellation.ThrowIfCancellationRequested();
    public string OutputProfiles(string sequence) { Check(); return EngineJson.Write(engine.Query().Sequence(Guid.Parse(sequence)).Creator?.Profiles ?? []); }
    public string Titles(string sequence)
    {
        Check();
        return EngineJson.Write(engine.Query().Sequence(Guid.Parse(sequence)).Titles);
    }
    public string ExportProfile(string sequence, string profile, string output) { RequireDirectScript(); return engine.Exports.Start(Guid.Parse(sequence), output, Guid.Parse(profile)).ToString(); }
    public string ExportOutputs(string sequence, string directory) { RequireDirectScript(); return EngineJson.Write(engine.ExportProfilesAsync(Guid.Parse(sequence), directory, cancellation).GetAwaiter().GetResult()); }
    public string TranscribeAligned(string asset, int stream, string model)
    {
        Check();
        return engine.Analysis.Start(new(Guid.Parse(asset), stream, "transcript", Transcription: new(Model: model, Alignment: "dtw"))).ToString();
    }
    public string CreatorRequest(string sequence, string transcript, string audio, string targetTicks, string shortTicks)
    {
        Check();
        return EngineJson.Write(new Valencia.Engine.CreatorRequest(Guid.Parse(sequence), transcript, audio, TargetTicks: targetTicks, ShortTicks: shortTicks));
    }
    public string StartCreator(string request)
    {
        Check();
        return engine.CreatorJobs.Start(AnalysisContracts.Parse<CreatorRequest>(request, "CreatorRequest"), engine.Session.Snapshot.Revision).ToString();
    }
    public string StartCreatorFromFile(string path)
    {
        Check();
        var request = AnalysisContracts.Parse<CreatorRequest>(File.ReadAllText(path), "CreatorRequest", path);
        return engine.CreatorJobs.Start(request, engine.Session.Snapshot.Revision).ToString();
    }
    public string CreatorStatus(string job) { Check(); return EngineJson.Write(engine.CreatorJobs.Status(Guid.Parse(job))); }
    public void CancelCreator(string job) { Check(); engine.CreatorJobs.Cancel(Guid.Parse(job)); }
    public string WaitCreator(string job)
    {
        Check();
        var id = Guid.Parse(job);
        CreatorJobStatus status;
        try { status = engine.CreatorJobs.WaitAsync(id, cancellation).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { engine.CreatorJobs.Cancel(id); throw; }
        if (status.State != "completed") throw new InvalidOperationException(status.Error ?? status.State);
        return status.PlanKey!;
    }
    public string CreatorSummary(string key) { Check(); return EngineJson.Write(EditingEngine.CreatorSummary(engine.CreatorProposal(key))); }
    public string CreatorPlan(string key) { Check(); return EngineJson.Write(engine.CreatorProposal(key).Plan); }
    public string ApplyCreator(string key)
    {
        Check();
        var applied = engine.ApplyCreator(key, cancellation);
        if (!applied.Valid) throw new InvalidOperationException(EngineJson.Write(applied));
        return EngineJson.Write(applied);
    }
    public string GenerateCaptions(string sequence, string transcript)
    {
        Check(); return engine.CreateCaptionsAsync(Guid.Parse(sequence), transcript, cancellation).GetAwaiter().GetResult().Key;
    }

    public string StartAnalysis(string request)
    {
        Check();
        return engine.Analysis.Start(AnalysisContracts.Parse<AnalysisRequest>(request, "AnalysisRequest")).ToString();
    }
    public string FirstAsset() { Check(); return engine.Query().Assets.First().Id.ToString(); }
    public string Transcribe(string asset, int stream, string model)
    {
        Check();
        return engine.Analysis.Start(new(Guid.Parse(asset), stream, "transcript", Transcription: new(Model: model))).ToString();
    }
    public string TranscriptSearch(string key, string query)
    {
        Check();
        return EngineJson.Write(TranscriptQueries.Search(engine.Analysis.ResultAsync(key, cancellation).GetAwaiter().GetResult(), query));
    }
    public string TranscriptRange(string key, string normalizedStart, string normalizedEnd)
    {
        Check();
        var artifact = engine.Analysis.ResultAsync(key, cancellation).GetAwaiter().GetResult();
        var start = new SourcePosition(artifact.ContainerStart).Shift(new(EngineJson.Int64(normalizedStart)));
        var end = new SourcePosition(artifact.ContainerStart).Shift(new(EngineJson.Int64(normalizedEnd)));
        return EngineJson.Write(TranscriptQueries.Range(artifact, start, end));
    }
    public string TranscriptForClip(string key, string sequence, string clip)
    {
        Check();
        var artifact = engine.Analysis.ResultAsync(key, cancellation).GetAwaiter().GetResult();
        var selected = engine.Query().Sequences.First(s => s.Id == Guid.Parse(sequence)).Tracks.SelectMany(t => t.Clips).First(c => c.Id == Guid.Parse(clip));
        return EngineJson.Write(TranscriptQueries.ForClip(artifact, selected));
    }
    public string TextEdit(string request)
    {
        Check();
        var typed = AnalysisContracts.Parse<TextEditRequest>(request, "TextEditRequest");
        return EngineJson.Write(engine.TextEditAsync(typed, cancellation).GetAwaiter().GetResult());
    }
    public string TextEditPlan(string sequence, string key, string query, string mode)
    {
        Check();
        var artifact = engine.Analysis.ResultAsync(key, cancellation).GetAwaiter().GetResult();
        var words = TranscriptQueries.UniqueMatch(artifact, query).WordIds;
        var proposal = engine.TextEditAsync(new(Guid.Parse(sequence), key, mode, words), cancellation).GetAwaiter().GetResult();
        if (proposal.Plan is null || proposal.Preview?.Valid != true) throw new InvalidOperationException(EngineJson.Write(proposal));
        return EngineJson.Write(proposal.Plan);
    }
    public int FirstAudioStream(string asset) { Check(); return engine.Query().Assets.First(a => a.Id == Guid.Parse(asset)).Streams.First(s => s.Kind == TrackKind.Audio).Index; }
    public int FirstVideoStream(string asset) { Check(); return engine.Query().Assets.First(a => a.Id == Guid.Parse(asset)).Streams.First(s => s.Kind == TrackKind.Video).Index; }
    public string AnalyzeScenes(string asset, int stream)
    {
        Check();
        return engine.Analysis.Start(new(Guid.Parse(asset), stream, "scenes")).ToString();
    }
    public string AnalyzeAudio(string assetId, int stream, double thresholdDb, int minimumSilenceMs)
    {
        Check();
        return engine.Analysis.Start(new(Guid.Parse(assetId), stream, "audio",
            new Valencia.Analysis.AudioSettings(ThresholdDb: thresholdDb, ActiveDb: Math.Max(-20, thresholdDb), MinimumSilenceMs: minimumSilenceMs))).ToString();
    }
    public string AnalysisStatus(string job) { Check(); return EngineJson.Write(engine.Analysis.Status(Guid.Parse(job))); }
    public void CancelAnalysis(string job) { Check(); engine.Analysis.Cancel(Guid.Parse(job)); }
    public string WaitAnalysis(string job)
    {
        Check();
        Guid id = Guid.Parse(job);
        Valencia.Engine.AnalysisStatus result;
        try { result = engine.Analysis.WaitAsync(id, cancellation).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { engine.Analysis.Cancel(id); throw; }
        if (result.State != "completed") throw new InvalidOperationException(result.Error ?? result.State);
        return result.Key;
    }
    public string AnalysisResult(string key, string kind, int offset, int limit)
    {
        Check();
        var artifact = engine.Analysis.ResultAsync(key, cancellation).GetAwaiter().GetResult();
        return EngineJson.Write(AnalysisContracts.Page(artifact, kind, offset, limit));
    }
    public string RoughCut(string sequence, string key, string policy)
    {
        Check();
        var proposal = engine.RoughCutAsync(Guid.Parse(sequence), key,
            AnalysisContracts.Parse<RoughCutPolicy>(policy, "RoughCutPolicy"), cancellation).GetAwaiter().GetResult();
        return EngineJson.Write(proposal);
    }
    public string RoughCutPlan(string sequence, string key, string policy)
    {
        Check();
        var proposal = engine.RoughCutAsync(Guid.Parse(sequence), key,
            AnalysisContracts.Parse<RoughCutPolicy>(policy, "RoughCutPolicy"), cancellation).GetAwaiter().GetResult();
        if (!proposal.Valid || proposal.Plan is null) throw new InvalidOperationException(string.Join("; ", proposal.Diagnostics));
        return EngineJson.Write(proposal.Plan);
    }
}

/// <summary>Typed native plan builder. Construction never mutates the engine.</summary>
public sealed class PlanBuilder(Guid projectId, string revision, Guid sequence)
{
    private readonly List<PlanOperation> operations = [];
    public void Split(string clipId, string at, string rightClipId) => operations.Add(new SplitPlan(Guid.Parse(clipId), at, Guid.Parse(rightClipId)));
    public void Split(string clipId, long at, string rightClipId) => Split(clipId, EngineJson.Exact(at), rightClipId);
    public void Move(string clipId, string trackId, string start) => operations.Add(new MovePlan(Guid.Parse(clipId), Guid.Parse(trackId), start));
    public void Move(string clipId, string trackId, long start) => Move(clipId, trackId, EngineJson.Exact(start));
    public void Delete(string clipId) => operations.Add(new DeletePlan(Guid.Parse(clipId)));
    public void BasicColor(string clipId, double exposure, double contrast, double saturation, double temperature) =>
        operations.Add(new ColorPlan(Guid.Parse(clipId), new(exposure, contrast, saturation, temperature)));
    public void Region(string clipId, string mode, double left, double top, double right, double bottom, int size) =>
        operations.Add(new RegionPlan(Guid.Parse(clipId), new(mode, left, top, right, bottom, size)));
    public void ClearRegion(string clipId) => operations.Add(new RegionPlan(Guid.Parse(clipId)));
    public void OutputFraming(string profileId, string name, int width, int height, string fit, double focalX, double focalY) =>
        operations.Add(new OutputFramingPlan(Guid.Parse(profileId), name, width, height, fit, focalX, focalY));
    public void OutputFocalPoint(string profileId, double x, double y) =>
        operations.Add(new OutputFramingPlan(Guid.Parse(profileId), FocalX: x, FocalY: y));
    public void Title(string titleId, string text, long start, long end, string motion, long entrance, long exit) =>
        operations.Add(new TitlePlan(Guid.Parse(titleId), text, start, end, motion, entrance, exit));
    public void StyledTitle(string titleId, string text, long start, long end, string motion, long entrance, long exit,
        double x, double y, double size, long color, long outline) =>
        operations.Add(new TitlePlan(Guid.Parse(titleId), text, start, end, motion, entrance, exit, x, y, size,
            checked((uint)color), checked((uint)outline)));
    /// <summary>A full-frame Valencia title card: heavy display head, orange rule, subtitle in expanded caps.</summary>
    public void CardTitle(string titleId, string text, string subtitle, long start, long end) =>
        operations.Add(new TitlePlan(Guid.Parse(titleId), text, start, end, Style: TitleStyles.Card, Subtitle: subtitle));
    /// <summary>A lower-third slab with an orange tab over the picture.</summary>
    public void LowerThird(string titleId, string text, string subtitle, long start, long end) =>
        operations.Add(new TitlePlan(Guid.Parse(titleId), text, start, end, Style: TitleStyles.LowerThird, Subtitle: subtitle));
    public void RemoveTitle(string titleId) => operations.Add(new RemoveTitlePlan(Guid.Parse(titleId)));
    public void ClipMotion(string clipId, string preset, long duration, double strength) =>
        operations.Add(new ClipMotionPlan(Guid.Parse(clipId), new(preset, duration, strength)));
    public void ClearClipMotion(string clipId) => operations.Add(new ClipMotionPlan(Guid.Parse(clipId)));
    public TransformBuilder Keyframes() => new();
    public void ClipKeys(string clipId, TransformBuilder frames) => operations.Add(new TransformPlan("clip", Guid.Parse(clipId), frames.Build()));
    public void ClipKeysPreservingOrigin(string clipId, TransformBuilder frames) =>
        operations.Add(new TransformPlan("clip", Guid.Parse(clipId), frames.Build(), PreserveOrigin: true));
    public void TitleKeys(string titleId, TransformBuilder frames) => operations.Add(new TransformPlan("title", Guid.Parse(titleId), frames.Build()));
    public void ClearKeys(string target, string id) => operations.Add(new TransformPlan(target, Guid.Parse(id)));
    public void SoundHit(string trackId, string clipId, string assetId, int stream, long at, long sourceOffset, long duration, double gainDb) =>
        operations.Add(new SoundHitPlan(Guid.Parse(trackId), Guid.Parse(clipId), Guid.Parse(assetId), stream, at, sourceOffset, duration, gainDb));
    public GainEnvelopeBuilder GainEnvelope() => new();
    public void TrackGain(string trackId, double gainDb) => operations.Add(new TrackGainPlan(Guid.Parse(trackId), gainDb));
    public void TrackEnvelope(string trackId, GainEnvelopeBuilder envelope) =>
        operations.Add(new TrackGainPlan(Guid.Parse(trackId), Envelope: envelope.Build()));
    public void Transition(string trackId, string id, string outgoing, string incoming, long before, long after) =>
        operations.Add(new TransitionPlan(Guid.Parse(trackId), Guid.Parse(id), new(Guid.Parse(outgoing), Guid.Parse(incoming), before, after)));
    public void ClearTransition(string trackId, string id) => operations.Add(new TransitionPlan(Guid.Parse(trackId), Guid.Parse(id)));
    public void ClipGain(string clipId, double gainDb) => operations.Add(new ClipGainPlan(Guid.Parse(clipId), gainDb));
    public void Speed(string clipId, int numerator, int denominator) => operations.Add(new SpeedPlan(Guid.Parse(clipId), numerator, denominator));
    public void Freeze(string clipId, long at, long duration) => operations.Add(new FreezePlan(Guid.Parse(clipId), at, duration));
    public void AddTrack(string trackId, string kind, int order, string name) => operations.Add(new AddTrackPlan(Guid.Parse(trackId), Enum.Parse<TrackKind>(kind, true), order, name));
    public string ToJson() => EngineJson.Write(Build());
    public EditPlan Build() => new(1, projectId, revision, sequence, [.. operations]);
}

public sealed class TransformBuilder
{
    private readonly List<TransformKey> keys = [];

    public void Key(long at, double x, double y, double scale, double rotation, double opacity, string interpolation)
    {
        if (keys.Count == 128) throw new ArgumentException("A transform supports at most 128 keyframes.");
        keys.Add(new(at, x, y, scale, rotation, opacity, interpolation));
    }

    internal TransformCurve Build() => new([.. keys]);
}

public sealed class GainEnvelopeBuilder
{
    private readonly List<GainPoint> points = [];

    public void Point(long at, double gainDb)
    {
        if (points.Count == 20000) throw new ArgumentException("A gain envelope supports at most 20,000 points.");
        points.Add(new(new(at), gainDb));
    }

    internal ImmutableArray<GainPoint> Build() => [.. points];
}
