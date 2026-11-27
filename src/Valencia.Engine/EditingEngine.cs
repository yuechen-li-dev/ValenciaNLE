using System.Collections.Immutable;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Media;
using Valencia.Persistence;

namespace Valencia.Engine;

/// <summary>Versioned facade over one authoritative session. No renderer, input simulation or duplicate edit reducer.</summary>
public sealed partial class EditingEngine : IAsyncDisposable
{
    public const int ApiVersion = 1;
    public EditingSession Session { get; }
    public ExportJobs Exports { get; }
    public AnalysisJobs Analysis { get; }
    public CreatorJobs CreatorJobs { get; }
    public WorkspacePolicy? Policy { get; }
    public string? ProjectPath { get; private set; }
    public ProjectDocument? SavedDocument { get; private set; }
    public ProjectDiskVersion? DiskVersion { get; private set; }
    private ProjectWriteLease? desktopLease;
    public EditingEngine(EditingSession session, WorkspacePolicy? policy = null, string? projectPath = null, string? analysisDirectory = null,
        ProjectDiskVersion? diskVersion = null, ProjectWriteLease? desktopLease = null)
    {
        Session = session;
        Policy = policy;
        ProjectPath = projectPath is null ? null : OutputPath(projectPath);
        DiskVersion = diskVersion;
        this.desktopLease = desktopLease;
        CheckMedia(session.Snapshot);
        Exports = new(this);
        CreatorJobs = new(this);
        string cache = analysisDirectory ?? (policy is not null ? Path.Combine(policy.Root, ".valencia-analysis")
            : Path.Combine(ValenciaData.Root, "Analysis-v1"));
        if (policy is not null) policy.Check(cache);
        Analysis = new(this, new Valencia.Analysis.AnalysisCache(cache));
    }
    public static EditingEngine Create(Guid id, string name) => new(new EditingSession(ProjectDocument.Create(id, name)));
    public static async Task<EditingEngine> OpenAsync(string path, WorkspacePolicy? policy = null, CancellationToken cancellation = default)
    {
        string resolved = policy is null ? Path.GetFullPath(path) : policy.Resolve(path);
        var (loaded, diskVersion) = await ProjectWriteLease.OpenAsync(resolved, cancellation).ConfigureAwait(false);
        return new(new EditingSession(loaded.Snapshot), policy, diskVersion: diskVersion)
            { ProjectPath = resolved, SavedDocument = loaded.Snapshot.Document };
    }
    public TimelineQueries Query() => new(Session.Snapshot);
    public SourceFrameInfo SourceFrame(Guid assetId, int stream, SequenceTime offset, string output, int maximumEdge = 640,
        SourcePoint? point = null, CancellationToken cancellation = default)
    {
        var asset = Session.Snapshot.Document.Assets.FirstOrDefault(a => a.Id == assetId)
            ?? throw new ArgumentException("Unknown source asset.");
        Policy?.Check(asset.SourcePath);
        return SourceFrameThumbnail.Write(asset, stream, offset, maximumEdge, OutputPath(output), point, cancellation);
    }
    public async Task<TextEditProposal> TextEditAsync(TextEditRequest request, CancellationToken cancellation = default)
    {
        var snapshot = Session.Snapshot;
        var artifact = await Analysis.ResultAsync(request.TranscriptKey, cancellation).ConfigureAwait(false);
        var proposal = await Task.Run(() => TranscriptEditing.Create(snapshot, artifact, request), cancellation).ConfigureAwait(false);
        return proposal.Plan is null ? proposal : proposal with { Preview = Plan(proposal.Plan, false, cancellation) };
    }
    public async Task<RoughCutProposal> RoughCutAsync(Guid sequence, string analysisKey, RoughCutPolicy policy, CancellationToken cancellation = default)
    {
        var before = Session.Snapshot;
        var artifact = await Analysis.ResultAsync(analysisKey, cancellation).ConfigureAwait(false);
        var proposal = await Task.Run(() => RoughCutPlanner.Create(before, sequence, artifact, policy), cancellation).ConfigureAwait(false);
        if (proposal.Plan is null) return proposal;
        var preview = Plan(proposal.Plan, false, cancellation);
        return proposal with { Valid = preview.Valid, Preview = preview, Diagnostics = proposal.Diagnostics.AddRange(preview.Diagnostics) };
    }
    public ImmutableArray<MissingMedia> MissingMedia() => [.. Session.Snapshot.Document.Assets
        .Where(a => !File.Exists(a.SourcePath)).Select(a => new MissingMedia(a.Id, a.SourcePath))];
    public EditResult Execute(EditCommand command, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (command.Operation is AddAsset add) Policy?.Check(add.Asset.SourcePath);
        if (command.Operation is RelinkAsset relink) Policy?.Check(relink.Asset.SourcePath);
        return Session.Execute(command);
    }
    public EditResult Execute(EditTransaction transaction, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        foreach (var command in transaction.Commands)
        {
            if (command.Operation is AddAsset add) Policy?.Check(add.Asset.SourcePath);
            if (command.Operation is RelinkAsset relink) Policy?.Check(relink.Asset.SourcePath);
        }
        return Session.Execute(transaction);
    }
    public async Task<EditResult> ImportAsync(Guid assetId, string path, long expectedRevision, CancellationToken cancellation = default)
    {
        string resolved = OutputPath(path);
        var imported = await Task.Run(() => MediaAssetProbe.Import(assetId, resolved), cancellation).ConfigureAwait(false);
        return Execute(new EditCommand(Guid.NewGuid(), 1, new AddAsset(imported.Asset), expectedRevision), cancellation);
    }
    public async Task SaveAsync(string path, CancellationToken cancellation = default)
    {
        var snapshot = Session.Snapshot;
        string resolved = OutputPath(path);
        if (Policy is not null && DiskVersion is { } original && string.Equals(original.Path, resolved, StringComparison.OrdinalIgnoreCase))
            throw new ProjectConflictException("MCP sessions save to a new destination. Apply the proposal in the desktop to update its project.");
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        var expected = DiskVersion is { } previous && string.Equals(previous.Path, resolved, StringComparison.OrdinalIgnoreCase) ? previous : null;
        var lease = desktopLease?.Matches(resolved) == true ? desktopLease : null;
        using var transient = lease is null ? ProjectWriteLease.Acquire(resolved) : null;
        DiskVersion = await (lease ?? transient!).SaveAsync(snapshot, expected, cancellation).ConfigureAwait(false);
        ProjectPath = resolved;
        SavedDocument = snapshot.Document;
    }
    public async Task<EditResult> OpenIntoAsync(string path, long expectedRevision, CancellationToken cancellation = default)
    {
        string resolved = OutputPath(path);
        var (loaded, diskVersion) = await ProjectWriteLease.OpenAsync(resolved, cancellation).ConfigureAwait(false);
        CheckMedia(loaded.Snapshot);
        cancellation.ThrowIfCancellationRequested();
        var result = Session.ReplaceProject(loaded.Snapshot, new(Guid.NewGuid(), 1, expectedRevision));
        if (result.Success) { ProjectPath = resolved; SavedDocument = loaded.Snapshot.Document; DiskVersion = diskVersion; }
        return result;
    }
    public string OutputPath(string path) => Policy is null ? Path.GetFullPath(path) : Policy.Resolve(path);
    private void CheckMedia(ProjectSnapshot snapshot)
    {
        if (Policy is null) return;
        foreach (var asset in snapshot.Document.Assets) Policy.Check(asset.SourcePath);
    }
    public EditResult Undo(long revision, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        return Session.Undo(new(Guid.NewGuid(), 1, revision));
    }
    public EditResult Redo(long revision, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        return Session.Redo(new(Guid.NewGuid(), 1, revision));
    }
    public PlanResult Plan(EditPlan plan, bool commit, CancellationToken cancellation = default) =>
        EvaluatePlan(plan, commit, out _, cancellation);

    private PlanResult EvaluatePlan(EditPlan plan, bool commit, out ProjectSnapshot? candidate, CancellationToken cancellation)
    {
        candidate = null;
        var before = Session.Snapshot;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            long revision = EngineJson.Int64(plan.ProjectRevision);
            if (revision != before.Revision)
                return Invalid("Project revision is stale.", AgentStale(revision, before.Revision));
            if (plan.SchemaVersion != 1 || plan.ProjectId != before.Document.Id || plan.Operations.IsDefaultOrEmpty || plan.Operations.Length > 500)
                throw new ArgumentException("Invalid plan schema, project identity or operation count.");
            // Direct typed plans receive exactly the same compiler-owned schema validation as wire plans.
            plan = EditPlanSchema.Parse(EngineJson.Write(plan));
            if (plan.SourcePreconditions is { } checks)
            {
                if (checks.Length is < 1 or > 8) throw new ArgumentException("Source precondition budget is 1..8 assets.");
                foreach (var check in checks)
                {
                    var asset = before.Document.Assets.FirstOrDefault(a => a.Id == check.AssetId)
                        ?? throw new ArgumentException("Source precondition refers to an unknown asset.");
                    Policy?.Check(asset.SourcePath);
                    if (Valencia.Analysis.AnalysisCache.Fingerprint(asset.SourcePath) != check.Fingerprint)
                        throw new ArgumentException("Source changed since this analysis plan was generated.");
                }
            }
            var transaction = new EditTransaction(Guid.NewGuid(), 1, revision,
                [.. plan.Operations.Select(op => new EditCommand(Guid.NewGuid(), 1, op.ToCore(plan.SequenceId)))]);
            var result = commit ? Session.Execute(transaction) : Session.Preview(transaction);
            if (!result.Success) return Invalid(result.Message ?? result.Error.ToString(), ReportEditFailure(result, revision));
            var after = result.Snapshot;
            var changes = Diff(before.Document, after.Document);
            var outputProfiles = DiffOutputFraming(plan, before, after);
            var titles = DiffTitles(plan, before, after);
            var transitionChanges = DiffTransitions(plan.SequenceId, before, after);
            var gainChanges = DiffTrackGain(plan.SequenceId, before, after);
            var affected = changes.SelectMany(c => new[] { c.ClipId, c.BeforeTrack ?? Guid.Empty, c.AfterTrack ?? Guid.Empty })
                .Concat(plan.Operations.OfType<TrackStatePlan>().Select(o => o.TrackId))
                .Concat(plan.Operations.OfType<AddTrackPlan>().Select(o => o.TrackId))
                .Concat(plan.Operations.OfType<TrackRolePlan>().Select(o => o.TrackId))
                .Concat(plan.Operations.OfType<AssetRolePlan>().Select(o => o.AssetId))
                .Concat(plan.Operations.OfType<CreatorPlan>().Select(_ => plan.SequenceId))
                .Concat(outputProfiles.SelectMany(change => new[] { plan.SequenceId, change.ProfileId }))
                .Concat(titles.SelectMany(change => new[] { plan.SequenceId, change.TitleId }))
                .Concat(transitionChanges.SelectMany(change => new[] { change.TransitionId, change.BeforeTrack ?? Guid.Empty, change.AfterTrack ?? Guid.Empty }))
                .Concat(gainChanges.Select(change => change.TrackId))
                .Where(id => id != Guid.Empty).Distinct().Order().ToImmutableArray();
            long resulting = commit ? result.Revision : checked(revision + 1);
            var current = Session.Snapshot;
            var duration = new TimelineQueries(after).Duration(plan.SequenceId);
            candidate = after;
            return new(1, true, commit, plan.ProjectRevision, EngineJson.Exact(resulting), EngineJson.Exact(current.Revision),
                !commit && current.Revision == revision && current.Document.Id == plan.ProjectId, affected, changes,
                duration, [], outputProfiles.IsEmpty ? null : outputProfiles,
                titles.IsEmpty ? null : titles, transitionChanges.IsEmpty ? null : transitionChanges,
                gainChanges.IsEmpty ? null : gainChanges);
        }
        catch (Exception error) when (error is ArgumentException or OverflowException or FormatException or System.Text.Json.JsonException or InvalidOperationException or IOException)
        {
            return Invalid(error.Message);
        }
        PlanResult Invalid(string message, AgentIssue? issue = null) => new(1, false, false, plan.ProjectRevision, EngineJson.Exact(Session.Snapshot.Revision),
            EngineJson.Exact(Session.Snapshot.Revision), false, [], [], null, [message], Issues: [issue ?? new("INVALID_PLAN", message)]);
    }
    internal static AgentIssue AgentStale(long requested, long current) => new("STALE_REVISION", "Project revision is stale.",
        "Refresh session_info and re-evaluate the edit against that revision; no changes were applied.",
        Constraints: new { requested, current });

    internal static AgentIssue ReportEditFailure(EditResult result, long expected)
    {
        if (result.Error == EditError.StaleRevision) return AgentStale(expected, result.Revision);
        var detail = result.Constraint;
        string code = detail?.Code ?? result.Error switch
        {
            EditError.NothingToUndo => "NOTHING_TO_UNDO", EditError.NothingToRedo => "NOTHING_TO_REDO",
            EditError.InvalidCommand => "INVALID_COMMAND", EditError.NoChange => "NO_CHANGE",
            EditError.Overflow => "TIME_RANGE", _ => "INVALID_EDIT"
        };
        (string kind, string? hint) = code switch
        {
            "TRACK_LOCKED" => ("track", "Unlock the named track explicitly or target an unlocked track."),
            "CLIP_OVERLAP" => ("track", "Select an empty range on this track or a different unlocked track."),
            "SOURCE_RANGE" => ("clip", "Keep the source range inside the reported stream bounds."),
            "SPLIT_OUTSIDE_CLIP" => ("clip", "Choose a position strictly within the reported clip range, on its supported grid."),
            _ => ("clip", null)
        };
        return new(code, result.Message ?? code, hint, detail is null ? null : AgentHandles.For(kind, detail.Subject),
            result.FailedCommandIndex, detail?.Bounds);
    }
    private static ImmutableArray<TransitionChange> DiffTransitions(Guid sequence, ProjectSnapshot before, ProjectSnapshot after)
    {
        Dictionary<Guid, (Guid? Track, TimelineTransition Item)> Get(ProjectSnapshot snapshot) => new TimelineQueries(snapshot)
            .Tracks(sequence).SelectMany(track => track.Transitions.Select(item => (Track: (Guid?)track.Id, Item: item)))
            .ToDictionary(pair => pair.Item.Id);
        var old = Get(before);
        var current = Get(after);
        return [.. old.Keys.Union(current.Keys).Order().Select(id =>
        {
            var a = old.GetValueOrDefault(id);
            var b = current.GetValueOrDefault(id);
            return new TransitionChange(id, a.Track, b.Track, a.Item, b.Item);
        }).Where(change => change.BeforeTrack != change.AfterTrack || change.Before != change.After)];
    }
    private static ImmutableArray<TrackGainChange> DiffTrackGain(Guid sequence, ProjectSnapshot before, ProjectSnapshot after)
    {
        Dictionary<Guid, TrackAudio> Get(ProjectSnapshot snapshot) =>
            (new TimelineQueries(snapshot).Sequence(sequence).Creator?.Audio ?? []).ToDictionary(item => item.TrackId);
        var old = Get(before);
        var current = Get(after);
        return [.. old.Keys.Union(current.Keys).Order().Select(id => new TrackGainChange(id, old.GetValueOrDefault(id), current.GetValueOrDefault(id)))
            .Where(change => change.Before is not { } a || change.After is not { } b || a.GainDb != b.GainDb
                || !a.Envelope.SequenceEqual(b.Envelope))];
    }
    private static ImmutableArray<TitleChange> DiffTitles(EditPlan plan, ProjectSnapshot before, ProjectSnapshot after)
    {
        var ids = plan.Operations.OfType<TitlePlan>().Select(operation => operation.TitleId)
            .Concat(plan.Operations.OfType<RemoveTitlePlan>().Select(operation => operation.TitleId))
            .Concat(plan.Operations.OfType<TransformPlan>().Where(operation => operation.Target == "title").Select(operation => operation.TargetId))
            .Distinct().Order().ToArray();
        if (ids.Length == 0) return [];
        var left = new TimelineQueries(before).Sequence(plan.SequenceId).Titles;
        var right = new TimelineQueries(after).Sequence(plan.SequenceId).Titles;
        return [.. ids.Select(id => new TitleChange(id, left.FirstOrDefault(cue => cue.Id == id), right.FirstOrDefault(cue => cue.Id == id)))
            .Where(change => change.Before != change.After)];
    }
    private static ImmutableArray<OutputProfileChange> DiffOutputFraming(EditPlan plan, ProjectSnapshot before, ProjectSnapshot after)
    {
        var ids = plan.Operations.OfType<OutputFramingPlan>().Select(o => o.ProfileId).Distinct().Order().ToArray();
        if (ids.Length == 0) return [];
        var left = new TimelineQueries(before).Sequence(plan.SequenceId).Creator?.Profiles ?? [];
        var right = new TimelineQueries(after).Sequence(plan.SequenceId).Creator?.Profiles ?? [];
        return [.. ids.Select(id => new OutputProfileChange(id, left.FirstOrDefault(p => p.Id == id), right.FirstOrDefault(p => p.Id == id)))
            .Where(change => change.Before != change.After)];
    }
    private static ImmutableArray<ClipChange> Diff(ProjectDocument before, ProjectDocument after)
    {
        static Dictionary<Guid, (Guid Track, TimelineClip Clip)> Clips(ProjectDocument document) => document.Sequences
            .SelectMany(s => s.Tracks).SelectMany(t => t.Clips.Select(c => (t.Id, Clip: c))).ToDictionary(p => p.Clip.Id);
        var left = Clips(before);
        var right = Clips(after);
        var result = ImmutableArray.CreateBuilder<ClipChange>();
        foreach (var id in left.Keys.Union(right.Keys).Order())
        {
            bool old = left.TryGetValue(id, out var a);
            bool next = right.TryGetValue(id, out var b);
            if (old && next && a == b) continue;
            string kind = !old ? "created" : !next ? "deleted" : a.Track != b.Track || a.Clip.Start != b.Clip.Start ? "moved" : "updated";
            result.Add(new(kind, id, old ? a.Track : null, next ? b.Track : null, old ? a.Clip : null, next ? b.Clip : null));
        }
        return result.ToImmutable();
    }
    public async ValueTask DisposeAsync()
    {
        await DisconnectObsAsync().ConfigureAwait(false);
        await CreatorJobs.DisposeAsync().ConfigureAwait(false);
        await Analysis.DisposeAsync().ConfigureAwait(false);
        await Exports.DisposeAsync().ConfigureAwait(false);
    }
}
