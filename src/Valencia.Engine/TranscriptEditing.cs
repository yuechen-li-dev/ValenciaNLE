using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Valencia.Analysis;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;

namespace Valencia.Engine;

public sealed record TranscriptMatch(int FirstWord, int WordCount, string Text, ImmutableArray<Guid> WordIds, SourceRange Range, bool TimingUsable);
public sealed record TranscriptOccurrence(Guid OccurrenceId, ImmutableArray<Guid> ClipIds, SequenceTime Start, SequenceTime End);
public sealed record TextEditRequest(Guid SequenceId, string TranscriptKey, string Mode, ImmutableArray<Guid> WordIds, ImmutableArray<Guid>? OccurrenceIds = null, int PaddingMs = 0);
public sealed record TextEditProposal(int ApiVersion, EditPlan? Plan, PlanResult? Preview, ImmutableArray<TranscriptOccurrence> Occurrences,
    ImmutableArray<TimelineRemoval> Removed, ImmutableArray<string> Diagnostics);

public static class TranscriptQueries
{
    public static TranscriptMatch UniqueMatch(AnalysisArtifact artifact, string query)
    {
        var matches = Search(artifact, query, limit: 2);
        if (matches.Length != 1) throw new ArgumentException(matches.Length == 0
            ? "No spoken phrase matches."
            : "Phrase matches multiple source ranges; select explicit word IDs with TextEditRequest.");
        return matches[0];
    }
    public static ImmutableArray<TranscriptWord> Range(AnalysisArtifact artifact, SourcePosition start, SourcePosition end, int offset = 0, int limit = 64)
    {
        if (start.CompareTo(end) >= 0 || offset < 0 || limit is < 1 or > 128) throw new ArgumentException("Invalid source range/page.");
        var data = artifact.Transcript ?? throw new ArgumentException("Transcript required.");
        return [.. data.Words.Where(w => w.TimingUsable && w.Range.Start.CompareTo(end) < 0 && w.Range.End.CompareTo(start) > 0).Skip(offset).Take(limit)];
    }
    public static ImmutableArray<TranscriptWord> ForClip(AnalysisArtifact artifact, TimelineClip clip, int offset = 0, int limit = 64)
    {
        if (clip.AssetId != artifact.AssetId) throw new ArgumentException("Clip refers to another source.");
        return Range(artifact, clip.SourceIn, clip.SourceOut, offset, limit);
    }
    public static ImmutableArray<TranscriptMatch> Search(AnalysisArtifact artifact, string query, bool exactWord = false, int offset = 0, int limit = 64, int context = 3)
    {
        if (offset < 0 || limit is < 1 or > 128) throw new ArgumentException("Invalid bounded transcript page.");
        return [.. EnumerateMatches(artifact, query, exactWord, context).Skip(offset).Take(limit)];
    }
    internal static IEnumerable<TranscriptMatch> EnumerateMatches(AnalysisArtifact artifact, string query, bool exactWord = false, int context = 3)
    {
        if (query.Length is < 1 or > 1024 || context is < 0 or > 20) throw new ArgumentException("Invalid transcript query.");
        var transcript = artifact.Transcript ?? throw new ArgumentException("Artifact is not a transcript.");
        string[] terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(WhisperTranscriber.Normalize).ToArray();
        if (terms.Length == 0 || terms.Any(t => t.Length == 0) || (exactWord && terms.Length != 1)) throw new ArgumentException("Query requires spoken words.");
        var words = transcript.Words;
        for (int i = 0; i + terms.Length <= words.Length; i++)
        {
            if (!terms.Select((term, j) => term == WhisperTranscriber.Normalize(words[i + j].Text)).All(x => x)) continue;
            if (words[i].Range.Epoch != words[i + terms.Length - 1].Range.Epoch) continue;
            int begin = Math.Max(0, i - context), end = Math.Min(words.Length, i + terms.Length + context);
            yield return new(i, terms.Length, string.Join(" ", words.Skip(begin).Take(end - begin).Select(w => w.Text)),
                [.. words.Skip(i).Take(terms.Length).Select(w => w.Id)], new(words[i].Range.Start, words[i + terms.Length - 1].Range.End, words[i].Range.Epoch),
                words.Skip(i).Take(terms.Length).All(w => w.TimingUsable));
        }
    }
}

public static class TranscriptEditing
{
    public static TextEditProposal Create(ProjectSnapshot snapshot, AnalysisArtifact artifact, TextEditRequest request)
    {
        var transcript = artifact.Transcript ?? throw new ArgumentException("Completed transcript required.");
        if (artifact.Status != "complete" || request.Mode is not ("retain" or "remove") || request.WordIds.IsDefaultOrEmpty || request.WordIds.Length > 1000
            || request.PaddingMs is < 0 or > 5000 || (request.Mode == "remove" && request.PaddingMs != 0))
            throw new ArgumentException("Invalid text edit selection/mode.");
        var selected = request.WordIds.Select(id => transcript.Words.FirstOrDefault(w => w.Id == id) ?? throw new ArgumentException("Unknown transcript word ID.")).ToArray();
        if (selected.Any(w => !w.TimingUsable || w.Range.Epoch != 0)) throw new ArgumentException("Untimed/discontinuous words cannot be used as exact edit boundaries.");
        var sequence = new TimelineQueries(snapshot).Sequence(request.SequenceId);
        var tracks = sequence.Tracks.Where(t => t.Clips.Length > 0).ToArray();
        if (tracks.SelectMany(t => t.Clips).Any(c => c.Retime is not null))
            throw new NotSupportedException("Transcript editing requires original-speed clips.");
        if (tracks.Length == 0 || tracks.Any(t => t.State.Locked || t.Clips.Any(c => c.AssetId != artifact.AssetId)))
            throw new NotSupportedException("Text editing currently requires unlocked aligned tracks of the transcribed source.");
        var guide = tracks.FirstOrDefault(t => t.Kind == TrackKind.Video)?.Clips ?? tracks[0].Clips;
        var groups = new List<List<TimelineClip>>();
        foreach (var clip in guide)
        {
            var intercept = clip.SourceIn.Shift(new(-clip.Start.Ticks));
            var group = groups.FirstOrDefault(g => g[0].SourceIn.Shift(new(-g[0].Start.Ticks)).EquivalentTo(intercept));
            if (group is null) groups.Add([clip]); else group.Add(clip);
        }
        var candidates = new List<TranscriptOccurrence>();
        var rangesByOccurrence = new Dictionary<Guid, List<TimelineRemoval>>();
        var coverageByOccurrence = new Dictionary<Guid, List<TimelineClip>>();
        foreach (var group in groups)
        {
            Guid identity = group[0].Id;
            var mapped = new List<TimelineRemoval>();
            foreach (var clip in group)
                foreach (var word in selected)
                {
                    // Retain padding expands context before outward frame snapping, clamped to each occurrence.
                    // Remove uses inward snapping and explicitly rejects padding, avoiding accidental extra deletion.
                    long padding = checked(request.PaddingMs * (SequenceTime.TicksPerSecond / 1000));
                    var paddedStart = word.Range.Start.Shift(new(-padding));
                    var paddedEnd = word.Range.End.Shift(new(padding));
                    var from = paddedStart.CompareTo(clip.SourceIn) < 0 ? clip.SourceIn : paddedStart;
                    var to = paddedEnd.CompareTo(clip.SourceOut) > 0 ? clip.SourceOut : paddedEnd;
                    if (to.CompareTo(from) <= 0) continue;
                    var start = AnalysisTime.ToTimeline(clip, from);
                    var end = AnalysisTime.ToTimeline(clip, to);
                    start = TimelineTime.SnapToFrame(start, sequence.Video.FrameRate, request.Mode == "retain" ? TimeRounding.Floor : TimeRounding.Ceiling);
                    end = TimelineTime.SnapToFrame(end, sequence.Video.FrameRate, request.Mode == "retain" ? TimeRounding.Ceiling : TimeRounding.Floor);
                    start = new(Math.Max(start.Ticks, clip.Start.Ticks)); end = new(Math.Min(end.Ticks, clip.End.Ticks));
                    if (start.Ticks < end.Ticks) mapped.Add(new(start, end));
                }
            if (mapped.Count == 0) continue;
            rangesByOccurrence.Add(identity, mapped);
            coverageByOccurrence.Add(identity, group);
            var intercept = group[0].SourceIn.Shift(new(-group[0].Start.Ticks));
            var aligned = tracks.SelectMany(t => t.Clips).Where(c => c.SourceIn.Shift(new(-c.Start.Ticks)).EquivalentTo(intercept)).Select(c => c.Id).ToImmutableArray();
            candidates.Add(new(identity, aligned, new(mapped.Min(r => r.Start.Ticks)), new(mapped.Max(r => r.End.Ticks))));
        }
        bool ambiguous = false;
        for (int a = 0; a < groups.Count; a++)
            for (int b = a + 1; b < groups.Count; b++)
                foreach (var left in groups[a])
                    foreach (var right in groups[b])
                        foreach (var word in selected)
                        {
                            var begin = new[] { left.SourceIn, right.SourceIn, word.Range.Start }.MaxBy(p => p, Comparer<SourcePosition>.Create((x, y) => x.CompareTo(y)));
                            var end = new[] { left.SourceOut, right.SourceOut, word.Range.End }.MinBy(p => p, Comparer<SourcePosition>.Create((x, y) => x.CompareTo(y)));
                            if (begin.CompareTo(end) < 0) ambiguous = true;
                        }
        if (ambiguous && request.OccurrenceIds is null)
            return new(1, null, null, [.. candidates], [], ["Ambiguous source occurrence: select explicit occurrence IDs before planning."]);
        var chosen = request.OccurrenceIds ?? candidates.Select(o => o.OccurrenceId).ToImmutableArray();
        if (chosen.IsDefaultOrEmpty || chosen.Any(id => !rangesByOccurrence.ContainsKey(id))) throw new ArgumentException("Selection has no matching timeline occurrence.");
        var removals = new List<TimelineRemoval>();
        foreach (var id in chosen.Distinct())
        {
            var union = new List<TimelineRemoval>();
            foreach (var range in rangesByOccurrence[id].OrderBy(r => r.Start.Ticks))
            {
                if (union.LastOrDefault() is { } last && range.Start.Ticks <= last.End.Ticks) union[^1] = last with { End = new(Math.Max(last.End.Ticks, range.End.Ticks)) };
                else union.Add(range);
            }
            if (request.Mode == "remove") removals.AddRange(union);
            else
            {
                foreach (var clip in coverageByOccurrence[id])
                {
                    var cursor = clip.Start;
                    foreach (var range in union.Where(r => r.End.Ticks > clip.Start.Ticks && r.Start.Ticks < clip.End.Ticks))
                    {
                        var begin = new SequenceTime(Math.Max(clip.Start.Ticks, range.Start.Ticks));
                        if (cursor.Ticks < begin.Ticks) removals.Add(new(cursor, begin));
                        cursor = new(Math.Min(clip.End.Ticks, range.End.Ticks));
                    }
                    if (cursor.Ticks < clip.End.Ticks) removals.Add(new(cursor, clip.End));
                }
            }
        }
        foreach (var removal in removals)
        {
            var owner = groups.First(g => chosen.Contains(g[0].Id) && g.Any(c => c.Start.Ticks < removal.End.Ticks && c.End.Ticks > removal.Start.Ticks));
            var intercept = owner[0].SourceIn.Shift(new(-owner[0].Start.Ticks));
            if (tracks.SelectMany(t => t.Clips).Any(c => c.Start.Ticks < removal.End.Ticks && c.End.Ticks > removal.Start.Ticks
                && !c.SourceIn.Shift(new(-c.Start.Ticks)).EquivalentTo(intercept)))
                throw new NotSupportedException("Another source occurrence overlaps this cut; explicit per-track editing is required.");
        }
        if (removals.Count is 0 or > 500) return new(1, null, null, [.. candidates], [.. removals], ["No edits, or the atomic plan budget is exceeded."]);
        var simulation = new EditingSession(snapshot, 0);
        var operations = ImmutableArray.CreateBuilder<PlanOperation>();
        foreach (var range in removals.OrderByDescending(r => r.Start.Ticks))
        {
            var current = new TimelineQueries(simulation.Snapshot).Sequence(sequence.Id);
            var splits = current.Tracks.SelectMany(t => t.Clips).Where(c => c.Start.Ticks < range.Start.Ticks && c.End.Ticks > range.End.Ticks)
                .Select(c => new RippleSplitId(c.Id, new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"textedit-v1|{snapshot.Revision}|{artifact.Key}|{c.Id}|{range.Start.Ticks}|{range.End.Ticks}"))[..16]))).ToImmutableArray();
            var op = new RipplePlan([.. tracks.Select(t => t.Id)], EngineJson.Exact(range.Start.Ticks), EngineJson.Exact(range.End.Ticks), splits);
            var result = simulation.Execute(new EditCommand(Guid.NewGuid(), 1, op.ToCore(sequence.Id), simulation.Snapshot.Revision));
            if (!result.Success) throw new InvalidOperationException(result.Message);
            operations.Add(op);
        }
        var plan = new EditPlan(1, snapshot.Document.Id, EngineJson.Exact(snapshot.Revision), sequence.Id, operations.ToImmutable(),
            SourcePreconditions: [new(artifact.AssetId, artifact.Fingerprint)]);
        return new(1, plan, null, [.. candidates], [.. removals], []);
    }
}
