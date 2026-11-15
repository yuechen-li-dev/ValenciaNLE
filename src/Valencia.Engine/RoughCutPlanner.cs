using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Valencia.Analysis;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;

namespace Valencia.Engine;

public sealed record RoughCutPolicy(int MinimumSegmentMs = 250, int MinimumRemovalMs = 800, int PrePaddingMs = 100,
    int PostPaddingMs = 100, int MergeDistanceMs = 0, int MaximumRetainedGapMs = 0, int? TargetDurationMs = null)
{
    public void Validate()
    {
        if (new[] { MinimumSegmentMs, MinimumRemovalMs, PrePaddingMs, PostPaddingMs, MergeDistanceMs, MaximumRetainedGapMs }.Any(v => v is < 0 or > 3_600_000)
            || TargetDurationMs is <= 0) throw new ArgumentException("Invalid rough-cut policy.");
    }
}
public sealed record TimelineRemoval(SequenceTime Start, SequenceTime End);
public sealed record RoughCutProposal(int ApiVersion, bool Valid, string AnalysisKey, EditPlan? Plan, PlanResult? Preview,
    ImmutableArray<SourceRange> Retained, ImmutableArray<TimelineRemoval> Removed, SequenceTime EstimatedDuration,
    bool? TargetMet, ImmutableArray<string> Diagnostics);

/// <summary>Pure plan generation over immutable facts. Only aligned, ordered, single-asset track groups are supported.</summary>
public static class RoughCutPlanner
{
    public static RoughCutProposal Create(ProjectSnapshot snapshot, Guid sequenceId, AnalysisArtifact artifact, RoughCutPolicy policy)
    {
        policy.Validate();
        if (artifact.SchemaVersion != 1 || artifact.Status != "complete" || artifact.Kind != "audio" || artifact.Algorithm != MediaAnalyzer.AudioAlgorithm)
            throw new ArgumentException("A completed compatible audio artifact is required.");
        if (artifact.SampleRate <= 0 || artifact.ProcessedUnits <= 0) throw new ArgumentException("Audio artifact has no sample coverage.");
        for (int i = 0; i < artifact.Silence.Length; i++)
        {
            var range = artifact.Silence[i];
            if (range.Epoch != 0 || range.End.CompareTo(range.Start) <= 0 || (i > 0 && range.Start.CompareTo(artifact.Silence[i - 1].End) < 0))
                throw new ArgumentException("Silence intervals must be ordered, nonoverlapping and continuous in source time.");
        }
        if (artifact.Activity.Any(a => a.Range.Epoch != 0)) throw new NotSupportedException("Discontinuous source audio requires explicit editorial decisions.");
        var asset = snapshot.Document.Assets.First(a => a.Id == artifact.AssetId);
        if (AnalysisCache.Fingerprint(asset.SourcePath) != artifact.Fingerprint) throw new ArgumentException("Stale analysis fingerprint.");
        var sequence = new TimelineQueries(snapshot).Sequence(sequenceId);
        var tracks = sequence.Tracks.Where(t => t.Clips.Length > 0).ToArray();
        if (tracks.Length == 0 || tracks.Any(t => t.State.Locked || t.Clips.Any(c => c.AssetId != artifact.AssetId)))
            throw new NotSupportedException("Rough cuts require unlocked, single-source tracks; unrelated tracks are not shifted implicitly.");
        if (!tracks.Any(t => t.Kind == TrackKind.Audio && t.Clips.Any(c => c.StreamIndex == artifact.StreamIndex)))
            throw new ArgumentException("The analyzed audio stream must be placed in the target sequence.");
        // Source stream offsets are legitimate (for example AAC priming before the first video PTS).
        // Alignment means equal source-minus-sequence intercepts, not identical clip edges.
        var guide = tracks.First(t => t.Kind == TrackKind.Audio && t.Clips.Any(c => c.StreamIndex == artifact.StreamIndex)).Clips;
        foreach (var track in tracks)
        {
            if (track.Clips.Length != guide.Length) throw new NotSupportedException("Tracks have different clip groups.");
            for (int i = 0; i < guide.Length; i++)
            {
                var clip = track.Clips[i];
                if (!clip.SourceIn.Shift(new(-clip.Start.Ticks)).EquivalentTo(guide[i].SourceIn.Shift(new(-guide[i].Start.Ticks)))
                    || clip.End.Ticks <= guide[i].Start.Ticks || clip.Start.Ticks >= guide[i].End.Ticks)
                    throw new NotSupportedException("Tracks are not source/time aligned; no automatic linking is inferred.");
                if (i > 0 && clip.SourceIn.CompareTo(track.Clips[i - 1].SourceOut) < 0)
                    throw new NotSupportedException("Source order must be monotonic for this rough-cut policy.");
            }
        }

        var removals = new List<TimelineRemoval>();
        var retained = new List<SourceRange>();
        long padTotal = checked((long)policy.PrePaddingMs + policy.PostPaddingMs);
        long extra = Math.Max(0, policy.MaximumRetainedGapMs - padTotal);
        long beforeTicks = AnalysisTime.Milliseconds(checked(policy.PostPaddingMs + (int)(extra / 2))).Ticks;
        long afterTicks = AnalysisTime.Milliseconds(checked(policy.PrePaddingMs + (int)(extra - extra / 2))).Ticks;
        foreach (var clip in guide)
        {
            var local = new List<TimelineRemoval>();
            foreach (var silence in artifact.Silence)
            {
                if (silence.End.CompareTo(silence.Start) <= 0) throw new ArgumentException("Invalid silence interval.");
                var sourceStart = silence.Start.Shift(new(beforeTicks));
                var sourceEnd = silence.End.Shift(new(-afterTicks));
                if (sourceStart.CompareTo(clip.SourceIn) < 0) sourceStart = clip.SourceIn;
                if (sourceEnd.CompareTo(clip.SourceOut) > 0) sourceEnd = clip.SourceOut;
                if (sourceEnd.CompareTo(sourceStart) <= 0) continue;
                var start = TimelineTime.SnapToFrame(AnalysisTime.ToTimeline(clip, sourceStart), sequence.Video.FrameRate, TimeRounding.Ceiling);
                var end = TimelineTime.SnapToFrame(AnalysisTime.ToTimeline(clip, sourceEnd), sequence.Video.FrameRate, TimeRounding.Floor);
                if (end.Ticks <= start.Ticks || end.Ticks - start.Ticks < AnalysisTime.Milliseconds(policy.MinimumRemovalMs).Ticks) continue;
                if (start.Ticks - clip.Start.Ticks < AnalysisTime.Milliseconds(policy.MinimumSegmentMs).Ticks) start = clip.Start;
                if (clip.End.Ticks - end.Ticks < AnalysisTime.Milliseconds(policy.MinimumSegmentMs).Ticks) end = clip.End;
                if (local.LastOrDefault() is { } previous && start.Ticks - previous.End.Ticks <= AnalysisTime.Milliseconds(Math.Max(policy.MergeDistanceMs, policy.MinimumSegmentMs)).Ticks)
                    local[^1] = previous with { End = new(Math.Max(previous.End.Ticks, end.Ticks)) };
                else local.Add(new(start, end));
            }
            var cursor = clip.Start;
            foreach (var range in local)
            {
                if (cursor.Ticks < range.Start.Ticks) retained.Add(new(clip.MapToSource(cursor), clip.MapToSource(range.Start), 0));
                cursor = range.End;
            }
            if (cursor.Ticks < clip.End.Ticks) retained.Add(new(clip.MapToSource(cursor), clip.SourceOut, 0));
            removals.AddRange(local);
        }
        if (removals.Count > 500) throw new NotSupportedException("Rough cut exceeds the 500-operation atomic plan budget; select a smaller sequence.");
        var simulation = new EditingSession(snapshot, historyLimit: 0);
        var operations = ImmutableArray.CreateBuilder<PlanOperation>();
        foreach (var range in removals.OrderByDescending(r => r.Start.Ticks))
        {
            var ids = simulation.Snapshot.Document.Sequences.First(s => s.Id == sequenceId).Tracks.Where(t => tracks.Any(target => target.Id == t.Id))
                .SelectMany(t => t.Clips).Where(c => c.Start.Ticks < range.Start.Ticks && c.End.Ticks > range.End.Ticks)
                .Select(c => new RippleSplitId(c.Id, StableId(snapshot, artifact.Key, range, c.Id))).ToImmutableArray();
            var operation = new RipplePlan([.. tracks.Select(t => t.Id)], EngineJson.Exact(range.Start.Ticks), EngineJson.Exact(range.End.Ticks), ids);
            var result = simulation.Execute(new EditCommand(Guid.NewGuid(), 1, operation.ToCore(sequenceId), simulation.Snapshot.Revision));
            if (!result.Success) throw new InvalidOperationException(result.Message);
            operations.Add(operation);
        }
        var estimated = new TimelineQueries(simulation.Snapshot).Duration(sequenceId);
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        if (operations.Count == 0) diagnostics.Add("No removable silence under this policy; no empty edit transaction is emitted.");
        bool? targetMet = policy.TargetDurationMs is { } target ? estimated.Ticks <= AnalysisTime.Milliseconds(target).Ticks : null;
        if (targetMet == false) diagnostics.Add("Target duration cannot be reached by the selected dead-air policy; active material was not ranked or truncated.");
        EditPlan? plan = operations.Count == 0 ? null : new(1, snapshot.Document.Id, EngineJson.Exact(snapshot.Revision), sequenceId, operations.ToImmutable(),
            SourcePreconditions: [new(artifact.AssetId, artifact.Fingerprint)]);
        return new(1, true, artifact.Key, plan, null, [.. retained], [.. removals], estimated, targetMet, diagnostics.ToImmutable());
    }
    private static Guid StableId(ProjectSnapshot snapshot, string key, TimelineRemoval range, Guid clip) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"roughcut-v1|{snapshot.Document.Id}|{snapshot.Revision}|{key}|{range.Start.Ticks}|{range.End.Ticks}|{clip}"))[..16]);
}
