using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Engine;

public sealed partial class EditingEngine
{
    private sealed record AgentCandidate(Guid Id, ProjectSnapshot Base, ProjectSnapshot Snapshot, EditPlan Plan, object Summary)
    {
        public string Handle => AgentHandles.For("candidate", Id);
    }

    private readonly Dictionary<Guid, AgentCandidate> agentCandidates = [];

    private object ManageCandidate(ProjectSnapshot snapshot, JsonElement request, CancellationToken cancellation)
    {
        string action = Text(request, "action")!;
        if (action == "prepare")
        {
            bool hasAuthored = request.TryGetProperty("plan", out var authored);
            bool hasCanonical = request.TryGetProperty("canonicalPlan", out var canonical);
            string? key = Text(request, "creatorKey");
            if ((hasAuthored ? 1 : 0) + (hasCanonical ? 1 : 0) + (key is null ? 0 : 1) != 1)
                throw AgentError("INVALID_REQUEST", "Prepare requires exactly one of plan, canonicalPlan or creatorKey.");
            var plan = hasAuthored ? NormalizePlan(snapshot, authored) : hasCanonical ? EditPlanSchema.Parse(canonical.GetRawText()) : CreatorProposal(key!).Plan;
            var result = EvaluatePlan(plan, false, out var preview, cancellation);
            if (!result.Valid || preview is null)
                throw new AgentRequestException(result.Issues?.FirstOrDefault() ?? new("INVALID_PLAN", string.Join("; ", result.Diagnostics)));
            RequireCurrent(snapshot);
            var summary = Summarize(snapshot, preview, result, plan);
            lock (agentGate)
            {
                RequireCurrent(snapshot);
                if (agentCandidates.Count >= 16) throw AgentError("CANDIDATE_LIMIT", "The session can keep 16 candidates. Discard one before preparing another.");
                var candidate = new AgentCandidate(Guid.NewGuid(), snapshot, preview, plan, summary);
                agentCandidates.Add(candidate.Id, candidate);
                return Describe(candidate, snapshot);
            }
        }
        if (action == "list")
        {
            lock (agentGate) return agentCandidates.Values.Where(c => c.Base.Document.Id == snapshot.Document.Id)
                .Select(c => new { candidate = c.Handle, baseRevision = c.Base.Revision, stale = !ReferenceEquals(snapshot, c.Base), c.Plan.Note }).ToArray();
        }
        var selected = FindCandidate(snapshot, RequiredText(request, "candidate"), action is "inspect" or "discard");
        if (action == "inspect") return Describe(selected, snapshot,
            request.TryGetProperty("includePlan", out var include) && include.GetBoolean());
        if (action == "discard")
        {
            lock (agentGate) return new { candidate = selected.Handle, discarded = agentCandidates.Remove(selected.Id) };
        }
        if (action == "compare")
        {
            var other = FindCandidate(snapshot, RequiredText(request, "other"));
            if (selected.Plan.SequenceId != other.Plan.SequenceId) throw AgentError("CANDIDATE_SCOPE", "Compare candidates for the same sequence.");
            return new { baseRevision = snapshot.Revision, left = Describe(selected, snapshot), right = Describe(other, snapshot) };
        }
        RequireCurrent(snapshot);
        var committed = EvaluatePlan(selected.Plan, true, out _, cancellation);
        if (!committed.Committed)
            throw new AgentRequestException(committed.Issues?.FirstOrDefault() ?? new("INVALID_PLAN", string.Join("; ", committed.Diagnostics)));
        return new { candidate = selected.Handle, priorRevision = snapshot.Revision, resultingRevision = EngineJson.Int64(committed.ResultingRevision),
            committed = true, selected.Summary };
    }

    private void RequireCurrent(ProjectSnapshot snapshot)
    {
        var now = Session.Snapshot;
        if (!ReferenceEquals(now, snapshot)) throw new AgentRequestException(AgentStale(snapshot.Revision, now.Revision));
    }

    private AgentCandidate FindCandidate(ProjectSnapshot snapshot, string reference, bool allowStale = false)
    {
        lock (agentGate)
        {
            Guid id = AgentHandles.Resolve(reference, "candidate", agentCandidates.Values.Where(c => c.Base.Document.Id == snapshot.Document.Id).Select(c => c.Id));
            var candidate = agentCandidates[id];
            if (!allowStale && !ReferenceEquals(candidate.Base, snapshot)) throw new AgentRequestException(AgentStale(candidate.Base.Revision, snapshot.Revision));
            return candidate;
        }
    }

    private static object Describe(AgentCandidate candidate, ProjectSnapshot current, bool includePlan = false) => new
    {
        candidate = candidate.Handle, baseRevision = candidate.Base.Revision, sequence = AgentHandles.For("sequence", candidate.Plan.SequenceId),
        stale = !ReferenceEquals(current, candidate.Base), candidate.Plan.Note, candidate.Summary, canonicalPlan = includePlan ? candidate.Plan : null
    };

    private static EditPlan NormalizePlan(ProjectSnapshot snapshot, JsonElement authored)
    {
        var document = snapshot.Document;
        var sequence = AgentHandles.Sequence(document, RequiredText(authored, "sequence"));
        var clips = sequence.Tracks.SelectMany(t => t.Clips).ToDictionary(c => c.Id, c => (c.AssetId, c.StreamIndex));
        var tracks = sequence.Tracks.Select(t => t.Id).ToHashSet();
        var profiles = (sequence.Creator?.Profiles ?? []).Select(p => p.Id).ToHashSet();
        var assetIds = document.Assets.Select(a => a.Id).ToHashSet();
        var payload = JsonNode.Parse(authored.GetRawText())!.AsObject();
        var operations = payload["operations"]!.AsArray();
        if (operations.Count is < 1 or > 500) throw AgentError("PLAN_LIMIT", "Prepare requires 1..500 operations.");
        for (int i = 0; i < operations.Count; i++)
        {
            var op = operations[i]!.AsObject();
            try { Normalize(op); }
            catch (AgentRequestException e) { throw new AgentRequestException(e.Issue with { OperationIndex = i }); }
        }
        if (payload["sourcePreconditions"] is JsonArray checks)
            foreach (var check in checks) Resolve(check!.AsObject(), "assetId", "asset", assetIds);
        payload.Remove("sequence");
        payload["sequenceId"] = sequence.Id.ToString();
        payload["schemaVersion"] = 1;
        payload["projectId"] = document.Id.ToString();
        payload["projectRevision"] = EngineJson.Exact(snapshot.Revision);
        return EditPlanSchema.Parse(payload.ToJsonString());

        void Normalize(JsonObject op)
        {
            string kind = Value(op, "operation");
            if (kind is not ("soundHit") && op.ContainsKey("clipId")) Resolve(op, "clipId", "clip", clips.Keys);
            if (kind is not ("addTrack") && op.ContainsKey("trackId")) Resolve(op, "trackId", "track", tracks);
            if (op.ContainsKey("assetId")) Resolve(op, "assetId", "asset", assetIds);
            foreach (string key in new[] { "start", "end", "at" }) Position(op, key);
            foreach (string key in new[] { "duration", "holdTicks", "entranceTicks", "exitTicks" }) Duration(op, key);
            switch (kind)
            {
                case "addTrack": tracks.Add(NewId(op, "trackId")); break;
                case "insert":
                    var insert = op["clip"]!.AsObject();
                    Guid asset = Resolve(insert, "assetId", "asset", assetIds);
                    int stream = insert["streamIndex"]!.GetValue<int>();
                    Position(insert, "start");
                    SourceRange(insert, asset, stream);
                    clips.TryAdd(NewId(insert, "id"), (asset, stream));
                    break;
                case "trim":
                    var source = clips[Guid.Parse(Value(op, "clipId"))];
                    SourceRange(op, source.AssetId, source.StreamIndex);
                    break;
                case "split":
                    clips.TryAdd(NewId(op, "rightClipId"), clips[Guid.Parse(Value(op, "clipId"))]); break;
                case "rippleDelete":
                    var selection = op["trackIds"]!.AsArray();
                    for (int i = 0; i < selection.Count; i++)
                        selection[i] = AgentHandles.Resolve(selection[i]!.GetValue<string>(), "track", tracks).ToString();
                    foreach (var split in op["splitIds"]!.AsArray())
                    {
                        var item = split!.AsObject();
                        Guid left = Resolve(item, "clipId", "clip", clips.Keys);
                        clips.TryAdd(NewId(item, "rightClipId"), clips[left]);
                    }
                    break;
                case "soundHit":
                    Guid sound = Guid.Parse(Value(op, "assetId"));
                    int channel = op["streamIndex"]!.GetValue<int>();
                    var audio = GetStream(sound, channel);
                    op["sourceOffset"] = EngineJson.Exact(AgentTime.Position(Value(op, "sourceOffset"), audio.FrameRate, source: true).Ticks);
                    clips.TryAdd(NewId(op, "clipId"), (sound, channel));
                    break;
                case "title":
                    if (Guid.TryParseExact(Value(op, "titleId"), "D", out var title))
                        clips.TryAdd(title, (TitleClips.AssetId, 0));
                    else Resolve(op, "titleId", "clip", clips.Keys);
                    break;
                case "removeTitle": Resolve(op, "titleId", "clip", clips.Keys); break;
                case "transform":
                    Resolve(op, "targetId", "clip", clips.Keys);
                    if (op["curve"]?["keys"] is JsonArray keys)
                        foreach (var key in keys) Duration(key!.AsObject(), "at");
                    break;
                case "clipMotion":
                    if (op["motion"] is JsonObject motion) Duration(motion, "durationTicks");
                    break;
                case "trackGain":
                    if (op["envelope"] is JsonArray points)
                        foreach (var point in points) Position(point!["time"]!.AsObject(), "ticks");
                    break;
                case "transition":
                    if (op["transition"] is JsonObject transition)
                    {
                        Resolve(transition, "incomingClipId", "clip", clips.Keys);
                        Resolve(transition, "outgoingClipId", "clip", clips.Keys);
                        Duration(transition, "beforeTicks"); Duration(transition, "afterTicks");
                    }
                    break;
                case "outputFraming":
                    if (Guid.TryParseExact(Value(op, "profileId"), "D", out var profile)) profiles.Add(profile);
                    else Resolve(op, "profileId", "profile", profiles);
                    break;
            }
        }

        AssetStream GetStream(Guid assetId, int index)
        {
            var item = assetId == TitleClips.AssetId ? TitleClips.Asset : document.Assets.First(a => a.Id == assetId);
            return item.Streams.FirstOrDefault(s => s.Index == index) ?? throw AgentError("UNKNOWN_STREAM", "Select a stream listed for this asset.", AgentHandles.For("asset", assetId));
        }
        void SourceRange(JsonObject obj, Guid assetId, int streamId)
        {
            var stream = GetStream(assetId, streamId);
            var origin = stream.Start ?? new MediaTimestamp(0, stream.TimeBaseNumerator, stream.TimeBaseDenominator);
            foreach (string field in new[] { "sourceIn", "sourceOut" })
                if (obj[field] is JsonValue text)
                {
                    long offset = AgentTime.Position(text.GetValue<string>(), stream.FrameRate, source: true).Ticks;
                    obj[field] = JsonSerializer.SerializeToNode(new SourcePositionDto(EngineJson.Exact(origin.Value), origin.Numerator, origin.Denominator,
                        EngineJson.Exact(offset)), EngineJson.Options);
                }
        }
        void Position(JsonObject obj, string key)
        {
            if (obj.ContainsKey(key)) obj[key] = EngineJson.Exact(AgentTime.Position(Value(obj, key), sequence: sequence).Ticks);
        }
        void Duration(JsonObject obj, string key)
        {
            if (obj.ContainsKey(key)) obj[key] = EngineJson.Exact(AgentTime.Duration(Value(obj, key), sequence.Video.FrameRate).Ticks);
        }
        static string Value(JsonObject obj, string key) => obj[key]!.GetValue<string>();
        static Guid NewId(JsonObject obj, string key) => Guid.TryParseExact(Value(obj, key), "D", out var id) && id != Guid.Empty
            ? id : throw AgentError("INVALID_ID", $"{key} must be a fresh GUID supplied by the caller.", Value(obj, key));
        static Guid Resolve(JsonObject obj, string key, string kind, IEnumerable<Guid> scope)
        {
            Guid id = AgentHandles.Resolve(Value(obj, key), kind, scope);
            obj[key] = id.ToString();
            return id;
        }
    }

    private static object Summarize(ProjectSnapshot original, ProjectSnapshot edited, PlanResult result, EditPlan plan)
    {
        var a = original.Document.Sequences.First(s => s.Id == plan.SequenceId);
        var b = edited.Document.Sequences.First(s => s.Id == plan.SequenceId);
        var changedTracks = a.Tracks.Select(t => t.Id).Union(b.Tracks.Select(t => t.Id))
            .Where(id => !TrackEqual(a.Tracks.FirstOrDefault(t => t.Id == id), b.Tracks.FirstOrDefault(t => t.Id == id))).ToArray();
        var changedAssets = original.Document.Assets.Zip(edited.Document.Assets).Where(p => p.First.Role != p.Second.Role)
            .Select(p => new { asset = AgentHandles.For("asset", p.First.Id), before = p.First.Role, after = p.Second.Role }).ToArray();
        var changes = result.Changes;
        var renderingTracks = changedTracks.Where(id => a.Tracks.FirstOrDefault(t => t.Id == id)?.State != b.Tracks.FirstOrDefault(t => t.Id == id)?.State)
            .Concat((result.TrackGain ?? []).Select(t => t.TrackId)).ToHashSet();
        var start = changes.Where(c => c.Before is not null).Select(c => (c.Before!.Start.Ticks, c.Before.End.Ticks))
            .Concat(a.Tracks.Where(t => renderingTracks.Contains(t.Id)).SelectMany(t => t.Clips.Select(c => (c.Start.Ticks, c.End.Ticks))));
        var end = changes.Where(c => c.After is not null).Select(c => (c.After!.Start.Ticks, c.After.End.Ticks))
            .Concat(b.Tracks.Where(t => renderingTracks.Contains(t.Id)).SelectMany(t => t.Clips.Select(c => (c.Start.Ticks, c.End.Ticks))));
        var beforeRanges = Merge(start); var afterRanges = Merge(end);
        var oldWords = (a.Creator?.Words ?? []).ToDictionary(w => w.Id);
        var newWords = (b.Creator?.Words ?? []).ToDictionary(w => w.Id);
        int wordChanges = oldWords.Keys.Union(newWords.Keys).Count(id => oldWords.GetValueOrDefault(id) != newWords.GetValueOrDefault(id));
        int profiles = (a.Creator?.Profiles ?? []).Select(p => p.Id).Union((b.Creator?.Profiles ?? []).Select(p => p.Id))
            .Count(id => !ProfileEqual(a.Creator?.Profiles.FirstOrDefault(p => p.Id == id), b.Creator?.Profiles.FirstOrDefault(p => p.Id == id)));
        var oldCuts = Cuts(a); var newCuts = Cuts(b);
        int addedCuts = newCuts.Except(oldCuts).Count(), removedCuts = oldCuts.Except(newCuts).Count();
        int moved = changes.Count(c => c.Kind == "moved");
        int trimmed = changes.Count(c => c.Before is not null && c.After is not null &&
            (c.Before.SourceIn != c.After.SourceIn || c.Before.SourceOut != c.After.SourceOut));
        int created = changes.Count(c => c.Kind == "created"), deleted = changes.Count(c => c.Kind == "deleted");
        int metadata = changes.Count(c => c.Before is not null && c.After is not null &&
            (c.Before.Properties != c.After.Properties || c.Before.Title != c.After.Title || c.Before.Retime != c.After.Retime));
        long? removed;
        try { removed = UnreferencedTicks(a, b); }
        catch (Exception e) when (e is ArgumentException or OverflowException) { removed = null; }
        var beforeTime = AgentTime.Describe(a.Duration, "sequence", a.Video.FrameRate);
        var afterTime = AgentTime.Describe(b.Duration, "sequence", b.Video.FrameRate);
        return new
        {
            text = FormattableString.Invariant($"{b.Name}: {beforeTime.Timecode} → {afterTime.Timecode}; {created} clips created, {deleted} deleted, {moved} moved, {trimmed} trimmed; {addedCuts} cuts added, {removedCuts} removed; {metadata} clip property/retime, {wordChanges} caption word, {profiles} output profile changes."),
            duration = new { before = beforeTime, after = afterTime, deltaTicks = b.Duration.Ticks - a.Duration.Ticks },
            clips = new { created, deleted, moved, trimmed, metadata, total = changes.Length,
                affected = changes.Take(32).Select(c => new { handle = AgentHandles.For("clip", c.ClipId), c.Kind }).ToArray() },
            cuts = new { added = addedCuts, removed = removedCuts, basis = "Distinct per-track abutting clip boundaries; a split counts as a cut, a gap does not." },
            uniqueSourceStreamTicksNoLongerReferenced = removed,
            tracks = new { count = changedTracks.Length, affected = changedTracks.Take(32).Select(id => AgentHandles.For("track", id)).ToArray() },
            assets = new { count = changedAssets.Length, changed = changedAssets.Take(16).ToArray() },
            titles = new { moved = changes.Count(c => c.Kind == "moved" && c.Before?.Title is not null),
                created = changes.Count(c => c.Kind == "created" && c.After?.Title is not null),
                deleted = changes.Count(c => c.Kind == "deleted" && c.Before?.Title is not null),
                edited = changes.Count(c => c.Before?.Title is not null && c.After?.Title is not null && c.Before.Title != c.After.Title) },
            captions = new { storedWordsBefore = oldWords.Count, storedWordsAfter = newWords.Count, wordChanges,
                styleChanged = a.Creator?.CaptionStyle != b.Creator?.CaptionStyle,
                placementMayDiffer = a.Creator is { Words.Length: > 0 } && changes.Any(c => c.Kind != "updated" || c.Before?.SourceIn != c.After?.SourceIn || c.Before?.SourceOut != c.After?.SourceOut) },
            outputProfilesChanged = profiles, transitionChanges = result.Transitions?.Length ?? 0, trackGainChanges = result.TrackGain?.Length ?? 0,
            affectedRanges = new { before = Ranges(beforeRanges), after = Ranges(afterRanges) }
        };

        object Ranges(List<(long Start, long End)> source) => new { count = source.Count, ranges = source.Take(16).Select(r =>
            new { start = ShortTime(new(r.Start), a.Video.FrameRate), end = ShortTime(new(r.End), a.Video.FrameRate) }).ToArray() };

        static bool TrackEqual(TimelineTrack? x, TimelineTrack? y) => x is not null && y is not null &&
            x with { Clips = y.Clips, Transitions = y.Transitions } == y && x.Clips.SequenceEqual(y.Clips) && x.Transitions.SequenceEqual(y.Transitions);
        static bool ProfileEqual(OutputProfile? x, OutputProfile? y) => x is null || y is null ? x == y :
            x with { Framing = y.Framing } == y && (x.Framing ?? []).SequenceEqual(y.Framing ?? []);
        static HashSet<(Guid Track, long At)> Cuts(SequenceDocument s) => s.Tracks.SelectMany(t =>
            t.Clips.Zip(t.Clips.Skip(1)).Where(p => p.First.End == p.Second.Start).Select(p => (t.Id, p.Second.Start.Ticks))).ToHashSet();
    }

    private static long UnreferencedTicks(SequenceDocument before, SequenceDocument after)
    {
        var all = before.Tracks.SelectMany(t => t.Clips).Concat(after.Tracks.SelectMany(t => t.Clips))
            .Where(c => c.Title is null).GroupBy(c => (c.AssetId, c.StreamIndex))
            .ToDictionary(g => g.Key, g => g.First().SourceIn);
        var left = Intervals(before); var right = Intervals(after);
        long total = 0;
        foreach (var (key, old) in left)
        {
            var remaining = right.GetValueOrDefault(key) ?? [];
            int scan = 0;
            foreach (var range in old)
            {
                long count = range.End - range.Start;
                while (scan < remaining.Count && remaining[scan].End <= range.Start) scan++;
                for (int i = scan; i < remaining.Count && remaining[i].Start < range.End; i++)
                    count -= Math.Min(range.End, remaining[i].End) - Math.Max(range.Start, remaining[i].Start);
                total = checked(total + count);
            }
        }
        return total;

        Dictionary<(Guid, int), List<(long Start, long End)>> Intervals(SequenceDocument s) =>
            s.Tracks.SelectMany(t => t.Clips).Where(c => c.Title is null).GroupBy(c => (c.AssetId, c.StreamIndex))
                .ToDictionary(g => g.Key, g =>
                {
                    var origin = all[g.Key];
                    return Merge(g.Select(c => (c.SourceIn.Since(origin).Ticks, c.SourceOut.Since(origin).Ticks)));
                });
    }

    private static List<(long Start, long End)> Merge(IEnumerable<(long Start, long End)> ranges)
    {
        var result = new List<(long Start, long End)>();
        foreach (var range in ranges.OrderBy(r => r.Start).ThenBy(r => r.End))
        {
            if (result.Count == 0 || result[^1].End < range.Start) result.Add(range);
            else result[^1] = (result[^1].Start, Math.Max(result[^1].End, range.End));
        }
        return result;
    }
}
