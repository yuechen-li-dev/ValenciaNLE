using System.Collections.Immutable;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.Engine;

public sealed record ReviewSelection(string Kind, ImmutableArray<long>? AtTicks = null, long? StartTicks = null,
    long? EndTicks = null, Guid? ProfileId = null, int MaxEdge = 640)
{
    public static ReviewSelection Parse(System.Text.Json.JsonElement value)
    {
        var errors = Copeland.TS.Tson.TypeProfileCompiler.Validate(EditPlanSchema.Catalog, nameof(ReviewSelection), value);
        if (errors.Count != 0) throw new ArgumentException(string.Join("\n", errors));
        return EngineJson.Read<ReviewSelection>(value.GetRawText());
    }
}
public sealed record ReviewResult(Guid ProjectId, Guid SequenceId, string BaseRevision, string CurrentRevision,
    string Candidate, bool BaseStillCurrent, Guid? ProfileId, string Output, int Width, int Height,
    ImmutableArray<ReviewSample>? Samples = null, ExportResult? Video = null, long? StartTicks = null, long? EndTicks = null);

public sealed partial class EditingEngine
{
    private readonly SemaphoreSlim reviewSlot = new(1);

    public Task<ReviewResult> ReviewAsync(Guid projectId, Guid sequenceId, long revision, ReviewSelection selection,
        string output, EditPlan? plan = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var current = Session.Snapshot;
        if (current.Document.Id != projectId || current.Revision != revision)
            throw new ArgumentException("Review project identity or revision is stale.");
        var candidate = current;
        if (plan is not null)
        {
            if (plan.ProjectId != projectId || plan.SequenceId != sequenceId || EngineJson.Int64(plan.ProjectRevision) != revision)
                throw new ArgumentException("Review and plan must target the same project, revision and sequence.");
            var result = EvaluatePlan(plan, false, out var validated, cancellation);
            if (!result.Valid || validated is null)
                throw new ArgumentException(string.Join("\n", result.Diagnostics));
            candidate = validated;
        }
        return RenderReviewAsync(current, candidate.Document, sequenceId, selection, output, plan is null ? "current" : "plan", cancellation);
    }

    /// <summary>Render a validated candidate produced by an isolated Copeland workflow.</summary>
    public Task<ReviewResult> ReviewWorkflowAsync(ProjectSnapshot original, ProjectDocument candidate, Guid sequenceId,
        ReviewSelection selection, string output, CancellationToken cancellation = default)
    {
        if (!ReferenceEquals(Session.Snapshot, original) || candidate.Id != original.Document.Id)
            throw new ArgumentException("The workflow's starting project revision is stale.");
        return RenderReviewAsync(original, candidate, sequenceId, selection, output, "workflow", cancellation);
    }

    private async Task<ReviewResult> RenderReviewAsync(ProjectSnapshot original, ProjectDocument candidate, Guid sequenceId,
        ReviewSelection selection, string output, string kind, CancellationToken cancellation)
    {
        string destination = OutputPath(output);
        bool movie = selection.Kind == "video";
        if (selection.Kind is not ("frame" or "contactSheet" or "video") || selection.MaxEdge is < 64 or > 960)
            throw new ArgumentException("Review needs frame, contactSheet or video, with maxEdge in 64..960.");
        if (!Path.GetExtension(destination).Equals(movie ? ".mp4" : ".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Review output must be a new PNG or MP4 matching its kind.");
        if (File.Exists(destination)) throw new IOException("Review destination already exists.");
        if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new DirectoryNotFoundException("Review output directory does not exist.");

        var snapshot = new ProjectSnapshot(candidate, original.Revision);
        CheckMedia(snapshot);
        var sequence = candidate.Sequences.FirstOrDefault(item => item.Id == sequenceId)
            ?? throw new ArgumentException("Unknown review sequence.");
        OutputProfile? profile = null;
        if (selection.ProfileId is { } profileId)
            profile = sequence.Creator?.Profiles.FirstOrDefault(item => item.Id == profileId)
                ?? throw new ArgumentException("Unknown review output profile.");
        long begin = profile?.Start.Ticks ?? 0;
        long end = Math.Min(sequence.Duration.Ticks, profile?.End?.Ticks ?? long.MaxValue);
        var times = selection.AtTicks ?? [];
        if (movie)
        {
            if (!times.IsDefaultOrEmpty || selection.StartTicks is not { } from || selection.EndTicks is not { } to
                || from < begin || to <= from || to > end || to - from > 30 * SequenceTime.TicksPerSecond)
                throw new ArgumentException("Review video requires a positive range of at most 30 seconds inside the output window.");
            var frameRate = profile?.FrameRate ?? sequence.Video.FrameRate;
            if (TimelineTime.FrameIndex(new(to - from), frameRate, TimeRounding.Ceiling) > 1800)
                throw new ArgumentException("Review video is limited to 1,800 frames.");
        }
        else if (selection.StartTicks is not null || selection.EndTicks is not null
            || times.IsDefaultOrEmpty || times.Length > (selection.Kind == "frame" ? 1 : 12)
            || times.Any(time => time < begin || time >= end))
            throw new ArgumentException("Request 1 frame or 1..12 contact sheet samples inside the output window.");

        if (!await reviewSlot.WaitAsync(0, cancellation).ConfigureAwait(false))
            throw new InvalidOperationException("Another review is rendering in this session.");
        try
        {
            if (movie)
            {
                var (width, height) = TimelineReviewImage.Fit(profile?.Width ?? sequence.Video.Width,
                    profile?.Height ?? sequence.Video.Height, selection.MaxEdge, alignment: 2);
                var clip = await SequenceExport.RunAsync(snapshot, sequenceId, destination, cancellation: cancellation,
                    profile: profile, overwrite: false, encodeSize: (width, height),
                    range: (new(selection.StartTicks!.Value), new(selection.EndTicks!.Value))).ConfigureAwait(false);
                return Result(width, height, video: clip);
            }
            var image = await Task.Run(() => TimelineReviewImage.Write(snapshot, sequenceId, profile, times, selection.MaxEdge,
                selection.Kind == "contactSheet", destination, cancellation), cancellation).ConfigureAwait(false);
            return Result(image.Width, image.Height, image.Samples);
        }
        finally { reviewSlot.Release(); }

        ReviewResult Result(int width, int height, ImmutableArray<ReviewSample>? samples = null, ExportResult? video = null)
        {
            var current = Session.Snapshot;
            return new(original.Document.Id, sequenceId, EngineJson.Exact(original.Revision), EngineJson.Exact(current.Revision),
                kind, ReferenceEquals(original, current), profile?.Id, destination, width, height, samples, video,
                selection.StartTicks, selection.EndTicks);
        }
    }
}
