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
    public EditingEngine(EditingSession session, WorkspacePolicy? policy = null, string? projectPath = null, string? analysisDirectory = null)
    {
        Session = session;
        Policy = policy;
        ProjectPath = projectPath is null ? null : OutputPath(projectPath);
        CheckMedia(session.Snapshot);
        Exports = new(this);
        CreatorJobs = new(this);
        string cache = analysisDirectory ?? (policy is not null ? Path.Combine(policy.Root, ".valencia-analysis")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valencia", "Analysis-v1"));
        if (policy is not null) policy.Check(cache);
        Analysis = new(this, new Valencia.Analysis.AnalysisCache(cache));
    }
    public static EditingEngine Create(Guid id, string name) => new(new EditingSession(ProjectDocument.Create(id, name)));
    public static async Task<EditingEngine> OpenAsync(string path, WorkspacePolicy? policy = null, CancellationToken cancellation = default)
    {
        string resolved = policy is null ? Path.GetFullPath(path) : policy.Resolve(path);
        var loaded = await new ProjectFile().LoadAsync(resolved, cancellation).ConfigureAwait(false);
        return new(new EditingSession(loaded.Snapshot), policy) { ProjectPath = resolved, SavedDocument = loaded.Snapshot.Document };
    }
    public TimelineQueries Query() => new(Session.Snapshot);
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
        await new ProjectFile().SaveAsync(resolved, snapshot, cancellation).ConfigureAwait(false);
        ProjectPath = resolved;
        SavedDocument = snapshot.Document;
    }
    public async Task<EditResult> OpenIntoAsync(string path, long expectedRevision, CancellationToken cancellation = default)
    {
        string resolved = OutputPath(path);
        var loaded = await new ProjectFile().LoadAsync(resolved, cancellation).ConfigureAwait(false);
        CheckMedia(loaded.Snapshot);
        cancellation.ThrowIfCancellationRequested();
        var result = Session.ReplaceProject(loaded.Snapshot, new(Guid.NewGuid(), 1, expectedRevision));
        if (result.Success) { ProjectPath = resolved; SavedDocument = loaded.Snapshot.Document; }
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
    public PlanResult Plan(EditPlan plan, bool commit, CancellationToken cancellation = default)
    {
        var before = Session.Snapshot;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            long revision = EngineJson.Int64(plan.ProjectRevision);
            if (revision != before.Revision)
                throw new ArgumentException("Project revision is stale.");
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
            if (!result.Success) return Invalid(result.Message ?? result.Error.ToString());
            var after = result.Snapshot;
            var changes = Diff(before.Document, after.Document);
            var affected = changes.SelectMany(c => new[] { c.ClipId, c.BeforeTrack ?? Guid.Empty, c.AfterTrack ?? Guid.Empty })
                .Concat(plan.Operations.OfType<TrackStatePlan>().Select(o => o.TrackId))
                .Concat(plan.Operations.OfType<AddTrackPlan>().Select(o => o.TrackId))
                .Concat(plan.Operations.OfType<CreatorPlan>().Select(_ => plan.SequenceId))
                .Where(id => id != Guid.Empty).Distinct().Order().ToImmutableArray();
            long resulting = commit ? result.Revision : checked(revision + 1);
            var current = Session.Snapshot;
            return new(1, true, commit, plan.ProjectRevision, EngineJson.Exact(resulting), EngineJson.Exact(current.Revision),
                !commit && current.Revision == revision && current.Document.Id == plan.ProjectId, affected, changes,
                new TimelineQueries(after).Duration(plan.SequenceId), []);
        }
        catch (Exception error) when (error is ArgumentException or OverflowException or FormatException or System.Text.Json.JsonException or InvalidOperationException or IOException)
        {
            return Invalid(error.Message);
        }
        PlanResult Invalid(string message) => new(1, false, false, plan.ProjectRevision, EngineJson.Exact(Session.Snapshot.Revision),
            EngineJson.Exact(Session.Snapshot.Revision), false, [], [], null, [message]);
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
