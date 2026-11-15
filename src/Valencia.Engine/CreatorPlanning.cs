using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Valencia.Analysis;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;

namespace Valencia.Engine;

public sealed record SourceMarker(string Start, string End, string Label);
public sealed record CreatorRequest(Guid SequenceId, string TranscriptKey, string AudioKey, string? SceneKey = null,
    string TargetTicks = "152409600000000", string MinimumTicks = "2540160000000", string MaximumTicks = "11430720000000",
    string ShortTicks = "5080320000000", int Shorts = 3, int Width = 1280, int Height = 720,
    ImmutableArray<string>? Keywords = null, ImmutableArray<SourceMarker>? Markers = null, bool SourceOrder = true,
    double NoiseReductionDb = 12, double MusicGainDb = -12, double DuckDb = -12, bool Normalize = true,
    double TargetLufs = -16, double PeakCeilingDb = -1, int DuckAttackMs = 80, int DuckReleaseMs = 300, int DuckHoldMs = 200,
    ImmutableArray<int>? MusicStreamIndices = null);
public sealed record HighlightCandidate(Guid Id, SequenceTime Start, SequenceTime End, double Score, ImmutableArray<string> Evidence);
public sealed record RetainedSegment(Guid CandidateId, SequenceTime SourceStart, SequenceTime SourceEnd, SequenceTime TimelineStart);
public sealed record CreatorProposal(string Key, EditPlan Plan, PlanResult Preview, ImmutableArray<HighlightCandidate> Candidates,
    ImmutableArray<RetainedSegment> Segments, ImmutableArray<OutputProfile> Outputs, SequenceTime EstimatedDuration,
    ImmutableArray<string> Diagnostics, LoudnessData? Loudness = null);

/// <summary>Bounded explainable selection. Facts in, existing semantic edit operations out.</summary>
public static class CreatorPlanning
{
    public static ImmutableArray<HighlightCandidate> Candidates(AnalysisArtifact transcript, AnalysisArtifact audio, AnalysisArtifact? scenes,
        CreatorRequest request, SequenceTime sourceDuration)
    {
        if (transcript.Transcript is null || audio.Kind != "audio" || transcript.AssetId != audio.AssetId || transcript.StreamIndex != audio.StreamIndex
            || transcript.Fingerprint != audio.Fingerprint || transcript.Status != "complete" || audio.Status != "complete")
            throw new ArgumentException("Complete matching source/stream transcript and audio artifacts are required.");
        if (scenes is not null && (scenes.AssetId != audio.AssetId || scenes.Fingerprint != audio.Fingerprint || scenes.Kind != "scenes"))
            throw new ArgumentException("Scene artifact does not match the source.");
        long minimum = Positive(request.MinimumTicks), maximum = Positive(request.MaximumTicks);
        if (minimum > maximum || maximum > 300L * SequenceTime.TicksPerSecond) throw new ArgumentException("Candidate duration requires min <= max <= five minutes.");
        var seeds = new List<(long Begin, long End, string Reason, double Weight)>();
        foreach (var marker in request.Markers ?? [])
        {
            long start = EngineJson.Int64(marker.Start), end = EngineJson.Int64(marker.End);
            if (start < 0 || end <= start || string.IsNullOrWhiteSpace(marker.Label)) throw new ArgumentException("Invalid source marker.");
            seeds.Add((start, end, "user marker: " + marker.Label, 10));
        }
        foreach (string keyword in request.Keywords ?? [])
        foreach (var match in TranscriptQueries.Search(transcript, keyword, limit: 128, context: 0))
        {
            if (!match.TimingUsable) continue;
            long at = AnalysisTime.Normalize(match.Range.Start, transcript.ContainerStart).Ticks;
            seeds.Add((Math.Max(0, at - 2 * SequenceTime.TicksPerSecond), at + maximum - 2 * SequenceTime.TicksPerSecond, "transcript keyword: " + keyword, 3));
        }
        if (seeds.Count == 0)
        {
            // Energy provides structural candidates, not a claim of editorial importance.
            var runs = new List<(long Start, long End)>();
            foreach (var activity in audio.Activity.Where(a => a.Category != "silence" && a.Range.Epoch == 0))
            {
                long start = AnalysisTime.Normalize(activity.Range.Start, audio.ContainerStart).Ticks;
                long end = AnalysisTime.Normalize(activity.Range.End, audio.ContainerStart).Ticks;
                if (runs.Count > 0 && start - runs[^1].End <= 2 * SequenceTime.TicksPerSecond) runs[^1] = (runs[^1].Start, end);
                else runs.Add((start, end));
            }
            foreach (var (start, end) in runs)
            {
                for (long at = start; at < end && seeds.Count < 1024; at = checked(at + maximum))
                    seeds.Add((Math.Max(0, at - SequenceTime.TicksPerSecond / 4), Math.Min(end + SequenceTime.TicksPerSecond / 4, at + maximum), "energy-active interval (not speech recognition)", 1));
            }
        }
        if (seeds.Count > 1024) throw new ArgumentException("Candidate seed budget exceeded; narrow keywords/markers.");
        var result = new List<HighlightCandidate>();
        foreach (var seed in seeds)
        {
            long begin = Math.Clamp(seed.Begin, 0, sourceDuration.Ticks), end = Math.Clamp(seed.End, 0, sourceDuration.Ticks);
            end = Math.Min(end, checked(begin + maximum));
            var words = transcript.Transcript.Words.Where(w => w.TimingUsable && w.Range.Epoch == 0
                && AnalysisTime.Normalize(w.Range.Start, transcript.ContainerStart).Ticks < end
                && AnalysisTime.Normalize(w.Range.End, transcript.ContainerStart).Ticks > begin).ToArray();
            if (words.Length > 0 && !seed.Reason.StartsWith("user marker:", StringComparison.Ordinal))
            {
                begin = Math.Max(begin, AnalysisTime.Normalize(words[0].Range.Start, transcript.ContainerStart).Ticks - SequenceTime.TicksPerSecond / 4);
                end = Math.Min(end, AnalysisTime.Normalize(words[^1].Range.End, transcript.ContainerStart).Ticks + SequenceTime.TicksPerSecond / 4);
            }
            if (end - begin < minimum) continue;
            double activeTicks = audio.Activity.Where(a => a.Category == "active" && a.Range.Epoch == 0).Sum(a => (double)Math.Max(0,
                Math.Min(end, AnalysisTime.Normalize(a.Range.End, audio.ContainerStart).Ticks) - Math.Max(begin, AnalysisTime.Normalize(a.Range.Start, audio.ContainerStart).Ticks)));
            int cuts = scenes?.Scenes.Count(s => { long at = AnalysisTime.Normalize(new(s.Timestamp), scenes.ContainerStart).Ticks; return at >= begin && at < end; }) ?? 0;
            double score = seed.Weight + Math.Min(1, activeTicks / (end - begin)) + Math.Min(cuts, 10) * .05;
            var id = Id($"candidate-v1|{transcript.Key}|{begin}|{end}");
            result.Add(new(id, new(begin), new(end), score, [seed.Reason, $"{words.Length} timed words", $"{cuts} visual-change candidates", "Heuristic evidence; human editorial quality unqualified"]));
        }
        return [.. result.DistinctBy(c => c.Id).OrderByDescending(c => c.Score).ThenBy(c => c.Start.Ticks).Take(512)];
    }

    public static (EditPlan Plan, ImmutableArray<RetainedSegment> Segments, CreatorSettings Settings, ImmutableArray<string> Diagnostics) Plan(
        ProjectSnapshot snapshot, CreatorRequest request, AnalysisArtifact transcript, AnalysisArtifact audio, ImmutableArray<HighlightCandidate> candidates)
    {
        var sequence = new TimelineQueries(snapshot).Sequence(request.SequenceId);
        var asset = snapshot.Document.Assets.First(a => a.Id == transcript.AssetId);
        var tracks = sequence.Tracks.Where(t => t.Clips.Length > 0).ToArray();
        if (tracks.Length == 0 || tracks.Any(t => t.State.Locked || t.Clips.Length != 1 || t.Clips[0].AssetId != asset.Id))
            throw new NotSupportedException("Creator source mode requires one unlocked aligned original recording clip per populated track; review complex sequences explicitly.");
        foreach (var track in tracks)
        {
            var clip = track.Clips[0];
            long offset = AnalysisTime.Normalize(clip.SourceIn, transcript.ContainerStart).Ticks - clip.Start.Ticks;
            if (offset != 0) throw new NotSupportedException("Creator source mode cannot silently choose between moved/repeated source occurrences.");
        }
        var otherAudio = tracks.Where(t => t.Kind == TrackKind.Audio && t.Clips[0].StreamIndex != transcript.StreamIndex).ToArray();
        if (request.MusicStreamIndices is null && otherAudio.Select(t => t.Clips[0].StreamIndex).Distinct().Count() > 1)
            throw new NotSupportedException("Multiple nonspeech streams require explicit musicStreamIndices; communications must not be silently ducked as music.");
        var musicStreams = request.MusicStreamIndices ?? otherAudio.Select(t => t.Clips[0].StreamIndex).ToImmutableArray();
        if (musicStreams.Any(index => index == transcript.StreamIndex || !otherAudio.Any(t => t.Clips[0].StreamIndex == index)))
            throw new ArgumentException("Music stream selection must refer to independent nonspeech source audio streams.");
        long target = Positive(request.TargetTicks), min = Positive(request.MinimumTicks);
        long shortLength = Positive(request.ShortTicks);
        if (target > 3600L * SequenceTime.TicksPerSecond || request.Shorts is < 0 or > 8 || !request.SourceOrder
            || !new[] { request.NoiseReductionDb, request.MusicGainDb, request.DuckDb, request.TargetLufs, request.PeakCeilingDb }.All(double.IsFinite)
            || request.NoiseReductionDb is < 0 or > 40 || request.MusicGainDb is < -60 or > 0 || request.DuckDb is < -60 or > 0
            || request.TargetLufs is < -30 or > -8 || request.PeakCeilingDb is < -12 or > 0
            || request.DuckAttackMs is < 0 or > 10000 || request.DuckReleaseMs is < 0 or > 10000 || request.DuckHoldMs is < 0 or > 10000)
            throw new ArgumentException("Unsupported creator duration/selection/audio policy. Only source-order assembly is implemented.");
        var selected = new List<HighlightCandidate>();
        long total = 0;
        foreach (var candidate in candidates)
        {
            if (selected.Any(c => c.Start.Ticks < candidate.End.Ticks && c.End.Ticks > candidate.Start.Ticks)) continue;
            long available = target - total;
            if (available < min) break;
            var start = TimelineTime.SnapToFrame(candidate.Start, sequence.Video.FrameRate, TimeRounding.Floor);
            var end = TimelineTime.SnapToFrame(new(Math.Min(candidate.End.Ticks, candidate.Start.Ticks + available)), sequence.Video.FrameRate, TimeRounding.Floor);
            if (end.Ticks - start.Ticks < min) continue;
            selected.Add(candidate with { Start = start, End = end });
            total += end.Ticks - start.Ticks;
        }
        if (selected.Count == 0) throw new ArgumentException("No suitable retained material; refusing to fabricate highlights.");
        selected = selected.OrderBy(c => c.Start.Ticks).ToList();
        var operations = ImmutableArray.CreateBuilder<PlanOperation>();
        foreach (var track in tracks) operations.Add(new DeletePlan(track.Clips[0].Id));
        var segments = ImmutableArray.CreateBuilder<RetainedSegment>();
        long cursor = 0;
        foreach (var selectedRange in selected)
        {
            segments.Add(new(selectedRange.Id, selectedRange.Start, selectedRange.End, new(cursor)));
            var from = new SourcePosition(transcript.ContainerStart).Shift(selectedRange.Start);
            var to = new SourcePosition(transcript.ContainerStart).Shift(selectedRange.End);
            foreach (var track in tracks)
            {
                var original = track.Clips[0];
                var begin = from.CompareTo(original.SourceIn) < 0 ? original.SourceIn : from;
                var end = to.CompareTo(original.SourceOut) > 0 ? original.SourceOut : to;
                if (end.CompareTo(begin) <= 0) continue;
                var sourceIn = original.SourceIn.Shift(begin.Since(original.SourceIn));
                var sourceOut = original.SourceIn.Shift(end.Since(original.SourceIn));
                var properties = original.Properties;
                if (track.Kind == TrackKind.Audio) properties = properties with { Audio = new(SequenceTime.TicksPerSecond / 200, SequenceTime.TicksPerSecond / 200,
                    original.StreamIndex == transcript.StreamIndex ? request.NoiseReductionDb : 0) };
                Guid id = Id($"creator-clip-v1|{snapshot.Document.Id}|{snapshot.Revision}|{track.Id}|{selectedRange.Id}");
                operations.Add(new InsertPlan(track.Id, new(id, asset.Id, original.StreamIndex, EngineJson.Exact(checked(cursor + begin.Since(from).Ticks)),
                    Position(sourceIn), Position(sourceOut), properties)));
            }
            cursor += selectedRange.End.Ticks - selectedRange.Start.Ticks;
        }
        if (operations.Count > 490) throw new NotSupportedException("Creator edit exceeds the atomic command budget; narrow the selection.");
        var retained = segments.ToImmutable();
        var captionWords = transcript.Transcript!.Words.Where(w => w.TimingUsable && w.Range.Epoch == 0 && retained.Any(s =>
            AnalysisTime.Normalize(w.Range.Start, transcript.ContainerStart).Ticks < s.SourceEnd.Ticks && AnalysisTime.Normalize(w.Range.End, transcript.ContainerStart).Ticks > s.SourceStart.Ticks))
            .Select(w => new CaptionWord(w.Id, asset.Id, transcript.StreamIndex, w.Range.Start, w.Range.End, w.Text)).ToImmutableArray();
        var profiles = ImmutableArray.CreateBuilder<OutputProfile>();
        profiles.Add(new(Id($"output|{snapshot.Document.Id}|long"), "long-form", request.Width, request.Height, sequence.Video.FrameRate, End: new(cursor)));
        foreach (var segment in retained.OrderByDescending(s => candidates.First(c => c.Id == s.CandidateId).Score).ThenBy(s => s.SourceStart.Ticks).Take(request.Shorts))
        {
            int index = profiles.Count;
            var end = new SequenceTime(segment.TimelineStart.Ticks + Math.Min(shortLength, segment.SourceEnd.Ticks - segment.SourceStart.Ticks));
            profiles.Add(new(Id($"output|{snapshot.Document.Id}|short|{segment.CandidateId}"), "short-" + index,
                Math.Max(18, request.Height / 18 * 18), Math.Max(18, request.Height / 18 * 18) / 9 * 16,
                sequence.Video.FrameRate, "fill", Start: segment.TimelineStart, End: end, CaptionY: .78));
        }
        var trackAudio = tracks.Where(t => t.Kind == TrackKind.Audio).Select(t => new TrackAudio(t.Id,
            musicStreams.Contains(t.Clips[0].StreamIndex) ? request.MusicGainDb : 0,
            musicStreams.Contains(t.Clips[0].StreamIndex) ? Ducking(audio, retained, request.DuckDb, request.DuckAttackMs, request.DuckReleaseMs, request.DuckHoldMs) : [])).ToImmutableArray();
        var settings = new CreatorSettings(1, captionWords, new(), profiles.ToImmutable(), trackAudio, PeakCeilingDb: request.PeakCeilingDb);
        operations.Add(new CreatorPlan(settings));
        var plan = new EditPlan(1, snapshot.Document.Id, EngineJson.Exact(snapshot.Revision), sequence.Id, operations.ToImmutable(),
            "Explainable source-order creator cut. Heuristics are not human editorial judgment.", [new(asset.Id, transcript.Fingerprint)]);
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        if (cursor < target - SequenceTime.TicksPerSecond) diagnostics.Add("Target duration is best effort: insufficient nonoverlapping material or a minimum-segment remainder.");
        if (profiles.Count - 1 < request.Shorts) diagnostics.Add("Fewer distinct retained candidates than requested Shorts.");
        return (plan, retained, settings, diagnostics.ToImmutable());
    }

    public static ImmutableArray<GainPoint> Ducking(AnalysisArtifact audio, ImmutableArray<RetainedSegment> segments, double attenuationDb,
        int attackMs = 80, int releaseMs = 300, int holdMs = 200)
    {
        var ranges = new List<(long Start, long End)>();
        long attack = AnalysisTime.Milliseconds(attackMs).Ticks, release = AnalysisTime.Milliseconds(releaseMs).Ticks, hold = AnalysisTime.Milliseconds(holdMs).Ticks;
        foreach (var segment in segments)
        foreach (var activity in audio.Activity.Where(a => a.Category == "active" && a.Range.Epoch == 0))
        {
            long begin = Math.Max(segment.SourceStart.Ticks, AnalysisTime.Normalize(activity.Range.Start, audio.ContainerStart).Ticks);
            long end = Math.Min(segment.SourceEnd.Ticks, AnalysisTime.Normalize(activity.Range.End, audio.ContainerStart).Ticks);
            if (end <= begin) continue;
            ranges.Add((segment.TimelineStart.Ticks + begin - segment.SourceStart.Ticks, segment.TimelineStart.Ticks + end - segment.SourceStart.Ticks + hold));
        }
        var merged = new List<(long Start, long End)>();
        foreach (var range in ranges.OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End + attack + release) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
            else merged.Add(range);
        }
        var points = new SortedDictionary<long, double> { [0] = 0 };
        foreach (var range in merged)
        {
            points[Math.Max(0, range.Start - attack)] = 0;
            points[range.Start] = attenuationDb;
            points[range.End] = attenuationDb;
            points[checked(range.End + release)] = 0;
        }
        return [.. points.Select(p => new GainPoint(new(p.Key), p.Value))];
    }
    private static SourcePositionDto Position(SourcePosition p) => new(EngineJson.Exact(p.Timestamp.Value), p.Timestamp.Numerator, p.Timestamp.Denominator, EngineJson.Exact(p.Offset.Ticks));
    private static long Positive(string value) => EngineJson.Int64(value) is var n && n > 0 ? n : throw new ArgumentException("Duration must be a positive canonical Int64 string.");
    public static Guid Id(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16]);
}
