using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Valencia.Analysis;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Engine;

public sealed partial class EditingEngine
{
    private sealed record Moment(SourceRange Range, string Kind, string? Text = null, double? Value = null, object? Evidence = null, bool TimingUsable = true);
    private sealed record ObservationArtifact(AnalysisArtifact Artifact, CachedAnalysis Meta);

    private async Task<object> FootageAsync(ProjectSnapshot snapshot, JsonElement request, CancellationToken cancellation)
    {
        var doc = snapshot.Document;
        string kind = Text(request, "kind") ?? (Text(request, "transcriptContains") is null ? "summary" : "matches");
        bool timeline = Text(request, "space") == "sequence";
        string? phrase = Text(request, "transcriptContains"), role = Text(request, "role");
        string? assetRef = Text(request, "asset"), sequenceRef = Text(request, "sequence");
        var sequence = timeline || role is not null || sequenceRef is not null || kind == "markers"
            ? SelectSequence(doc, sequenceRef) : null;
        var tracks = sequence?.Tracks.Where(t => role is null || t.Role == role).ToArray() ?? [];
        int stream = Integer(request, "stream", -1);
        if (request.TryGetProperty("stream", out _) && stream < 0) throw AgentError("UNKNOWN_STREAM", "Stream must be a nonnegative index.");
        var assets = assetRef is not null ? new[] { AgentHandles.Asset(doc, assetRef) } : doc.Assets.ToArray();
        if (assetRef is not null && stream >= 0 && !assets[0].Streams.Any(s => s.Index == stream))
            throw AgentError("UNKNOWN_STREAM", "That stream is not listed for this asset.", assetRef);
        if (role is not null) assets = assets.Where(a => tracks.Any(t => t.Clips.Any(c => c.AssetId == a.Id))).ToArray();
        var assetMap = assets.ToDictionary(a => a.Id);
        bool Primary(CachedAnalysis m) => (stream < 0 || m.StreamIndex == stream) &&
            (role is null || tracks.Any(t => t.Clips.Any(c => c.AssetId == m.AssetId && c.StreamIndex == m.StreamIndex)));
        bool InScope(CachedAnalysis m) => assetMap.ContainsKey(m.AssetId) && (Primary(m) ||
            m.Kind == "audio" && Text(request, "nearActivity") is not null || m.Kind == "scenes" && Text(request, "nearScene") is not null);
        var keys = ArtifactKeys(request);
        var metadata = keys.Length == 0
            ? AnalysisDiscovery.Enumerate(Analysis.Cache, cancellation).Where(InScope).OrderBy(m => m.Key, StringComparer.Ordinal).ToArray()
            : keys.Select(k => Metadata(k)).Where(InScope).OrderBy(m => m.Key, StringComparer.Ordinal).ToArray();
        var manifest = Obs?.Recording.Snapshot;
        var markInputs = kind is "markers" or "summary" ? manifest?.Markers : null;
        var highlightInputs = kind == "highlights" ? HighlightInputs() : [];
        string set = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(';',
            metadata.Select(m => m.Key + ":" + m.ContentVersion).Concat(markInputs?.Select(m => m.Id.ToString("N")) ?? [])
                .Concat(highlightInputs.Select(p => p.Key))))));
        if (Integer(request, "offset", 0) > 0 && Text(request, "artifactSet") != set)
            throw AgentError("ARTIFACT_SET_CHANGED", "Send the artifactSet returned on page one when continuing.", hint: "Restart at offset zero if the artifact set changed.");
        if (Text(request, "artifactSet") is { } expected && expected != set)
            throw AgentError("ARTIFACT_SET_CHANGED", "Available artifact versions changed; start a new query.");

        if (kind == "summary")
        {
            var selectedAssets = assets.Select(a => new
            {
                asset = AgentHandles.For("asset", a.Id), a.Name, a.Role, missing = !File.Exists(a.SourcePath),
                streams = a.Streams.Select(s => new { s.Index, s.Kind, s.Codec, s.FrameRate, s.Channels, s.SampleRate,
                    duration = s.End is { } end && s.Start is { } start
                        ? ShortTime(new SourcePosition(end).Since(new(start), TimeRounding.NearestAwayFromZero), space: "duration") : null }).ToArray(),
                analyses = metadata.Where(m => m.AssetId == a.Id).Select(m => new { m.Key, m.Kind, stream = m.StreamIndex, m.Algorithm }).Take(12).ToArray(),
                analysisCount = metadata.Count(m => m.AssetId == a.Id)
            }).ToArray();
            var details = new List<object>();
            if (keys.Length != 0)
            {
                foreach (var meta in metadata)
                {
                    var a = (await OpenArtifact(meta, cancellation).ConfigureAwait(false)).Artifact;
                    details.Add(new { meta.Key, meta.Kind, meta.StreamIndex, meta.ContentVersion, a.ContainerStart,
                        words = a.Transcript?.Words.Length, alignedWords = a.Transcript?.Words.Count(w => w.TimingUsable),
                        segments = a.Transcript?.Segments.Length, a.Transcript?.Alignment, silence = a.Silence.Length,
                        activity = a.Activity.GroupBy(r => r.Category).Select(g => new { category = g.Key, intervals = g.Count(),
                            seconds = g.Sum(i => (decimal)i.Range.End.Since(i.Range.Start, TimeRounding.NearestAwayFromZero).Ticks / SequenceTime.TicksPerSecond) }).ToArray(),
                        scenes = a.Scenes.Length, a.Loudness, diagnostics = a.Diagnostics.Take(4).ToArray() });
                }
            }
            return Page(request, selectedAssets, new { artifactSet = set, artifactDetails = details,
                markers = sequence?.Markers.Length, obsMarkerCount = manifest?.Markers.Length,
                limitations = "Cache discovery lists independent completed-artifact metadata; pin keys for coverage, activity, and provenance. Missing kinds are not inferred. Marker precision and speech alignment vary." });
        }

        var scope = kind switch
        {
            "words" or "segments" or "matches" => "transcript", "waveform" or "silence" or "activity" => "audio",
            "scenes" => "scenes", "loudness" => "loudness", _ => null
        };
        bool activity = Text(request, "nearActivity") is not null, scene = Text(request, "nearScene") is not null;
        var selected = metadata.Where(m => m.Kind == scope && Primary(m) || activity && m.Kind == "audio" || scene && m.Kind == "scenes").ToArray();
        if (scope is not null && !selected.Any(m => m.Kind == scope && Primary(m)))
            throw AgentError("ANALYSIS_MISSING", $"No {scope} artifact matches the asset/stream filter.", hint: $"Use start_analysis with kind:{scope}, then pass its artifact key.");
        if (selected.Length > 12) throw AgentError("ANALYSIS_LIMIT", "More than 12 relevant artifacts matched; pin the artifact keys or narrow the asset/stream.");
        var opened = new List<ObservationArtifact>();
        foreach (var meta in selected)
        {
            CheckSource(assetMap[meta.AssetId]);
            try { opened.Add(await OpenArtifact(meta, cancellation).ConfigureAwait(false)); }
            catch (AgentRequestException e) when (keys.Length == 0 && e.Issue.Code == "ANALYSIS_STALE") { }
        }
        if (scope is not null && !opened.Any(a => a.Artifact.Kind == scope && Primary(a.Meta)))
            throw AgentError("ANALYSIS_STALE", "No current analysis of that kind remained.", hint: "Analyze the current source again.");
        var rows = new List<(long Sort, Guid Asset, int Stream, Moment? Moment, ObservationArtifact? Artifact, object? Marker,
            long? At, long? End, Guid? Clip, Guid? Track)>();
        int unaligned = 0;
        long sequenceStart = timeline ? Boundary("start", sequence!.Video.FrameRate, sequence, 0) : 0;
        long sequenceEnd = timeline ? Boundary("end", sequence!.Video.FrameRate, sequence, long.MaxValue) : long.MaxValue;
        if (sequenceEnd <= sequenceStart) throw AgentError("TIME_RANGE", "Query end must follow start.");
        long Distance(string field) => Text(request, field) is { } time ? AgentTime.Duration(time).Ticks : 0;
        long nearActivity = Distance("nearActivity"), nearScene = Distance("nearScene"), separation = Distance("minimumSeparation");
        double? rms = request.TryGetProperty("minimumRms", out var v) ? v.GetDouble() : null;
        if (rms is not null && (!(rms is >= 0 and <= 1) || !double.IsFinite(rms.Value)))
            throw AgentError("INVALID_REQUEST", "minimumRms must be a unit amplitude in 0..1.");
        if (rms is not null && kind != "activity" && !activity) throw AgentError("INVALID_REQUEST", "minimumRms requires activity or nearActivity.");
        if (phrase is not null && kind is not ("matches" or "segments" or "words"))
            throw AgentError("INVALID_REQUEST", "transcriptContains is a transcript predicate.");
        var activityIndices = opened.Where(a => a.Artifact.Kind == "audio").GroupBy(a => a.Artifact.AssetId)
            .ToDictionary(g => g.Key, g =>
            {
                var sorted = g.SelectMany(x => x.Artifact.Activity.Where(r => r.Category == "active" && (rms is null || r.Rms >= rms))
                    .Select(r => (r.Range.Start, r.Range.End, source: x.Meta.Key))).OrderBy(r => r.Start, SourceOrder).ToArray();
                for (int i = 1; i < sorted.Length; i++)
                    if (sorted[i - 1].End.CompareTo(sorted[i].End) > 0)
                        sorted[i] = (sorted[i].Start, sorted[i - 1].End, sorted[i - 1].source);
                return sorted;
            });
        var sceneIndices = opened.Where(a => a.Artifact.Kind == "scenes").GroupBy(a => a.Artifact.AssetId)
            .ToDictionary(g => g.Key, g => g.SelectMany(x => x.Artifact.Scenes.Select(r => (At: new SourcePosition(r.Timestamp), source: x.Meta.Key)))
                .OrderBy(r => r.At, SourceOrder).ToArray());

        foreach (var item in opened.Where(a => a.Artifact.Kind == scope && Primary(a.Meta)))
        {
            var artifact = item.Artifact; var asset = assetMap[artifact.AssetId];
            FrameRate? clock = asset.Streams.FirstOrDefault(s => s.Index == artifact.StreamIndex)?.FrameRate;
            var origin = new SourcePosition(artifact.ContainerStart);
            long start = timeline ? 0 : Boundary("start", clock, null, long.MinValue);
            long end = timeline ? long.MaxValue : Boundary("end", clock, null, long.MaxValue);
            if (end <= start) throw AgentError("TIME_RANGE", "Query end must follow start.");
            foreach (var moment in Moments(artifact, kind, phrase))
            {
                cancellation.ThrowIfCancellationRequested();
                if (rms is not null && kind == "activity" && moment.Value is { } value && value < rms) continue;
                string? activityKey = activity ? NearActivity(artifact.AssetId, moment.Range) : null;
                string? sceneKey = scene ? NearScene(artifact.AssetId, moment.Range) : null;
                if (activity && activityKey is null || scene && sceneKey is null) continue;
                var match = !activity && !scene ? moment : moment with { Evidence = new { moment.Evidence, activityKey, sceneKey } };
                long from = moment.Range.Start.Since(origin, TimeRounding.Floor).Ticks;
                long to = moment.Range.End.Since(origin, TimeRounding.Ceiling).Ticks;
                if (!timeline)
                {
                    if (from < end && (to > start || to == from && from >= start))
                        rows.Add((from, asset.Id, artifact.StreamIndex, match, item, null, null, null, null, null));
                    continue;
                }
                if (!moment.TimingUsable) { unaligned++; continue; }
                foreach (var track in tracks)
                foreach (var clip in track.Clips.Where(c => c.AssetId == asset.Id && c.StreamIndex == artifact.StreamIndex))
                {
                    if (clip.Retime?.Hold is not null) { unaligned++; continue; }
                    var low = Max(moment.Range.Start, clip.SourceIn); var high = Min(moment.Range.End, clip.SourceOut);
                    bool point = moment.Range.Start.EquivalentTo(moment.Range.End);
                    if (low.CompareTo(high) > 0 || !point && low.EquivalentTo(high) || point && low.CompareTo(clip.SourceOut) >= 0) continue;
                    try
                    {
                        long a = clip.MapSourceBoundary(low).Ticks, b = clip.MapSourceBoundary(high).Ticks;
                        if (a < sequenceEnd && (b > sequenceStart || b == a && a >= sequenceStart))
                            rows.Add((a, asset.Id, artifact.StreamIndex, match, item, null, a, b, clip.Id, track.Id));
                    }
                    catch (ArgumentException) { unaligned++; }
                }
            }
        }
        if (kind == "markers")
        {
            string? label = Text(request, "markerLabel"), markerKind = Text(request, "markerKind");
            long first = sequence is null ? 0 : Boundary("start", sequence.Video.FrameRate, sequence, 0);
            long last = sequence is null ? long.MaxValue : Boundary("end", sequence.Video.FrameRate, sequence, long.MaxValue);
            if (markerKind is null or "sequence")
                foreach (var m in sequence?.Markers ?? [])
                    if (m.At.Ticks >= first && m.At.Ticks < last && (label is null || m.Label.Contains(label, StringComparison.OrdinalIgnoreCase)))
                        rows.Add((m.At.Ticks, Guid.Empty, -1, null, null, new { kind = "sequence", handle = AgentHandles.For("marker", m.Id), m.Label,
                            time = ShortTime(m.At, sequence!.Video.FrameRate) }, null, null, null, null));
            if (!timeline && manifest is not null && Text(request, "start") is null && Text(request, "end") is null)
                foreach (var m in manifest.Markers.Where(m => (markerKind is null || m.Kind == markerKind) && (label is null || m.Note.Contains(label, StringComparison.OrdinalIgnoreCase))))
                    rows.Add(((long)decimal.Round(m.OutputDurationMs * (SequenceTime.TicksPerSecond / 1000), 0), Guid.Empty, -1, null, null,
                        new { kind = m.Kind, handle = AgentHandles.For("marker", m.Id), m.Note, m.OutputDurationMs, space = "obsOutputElapsed", m.Precision, m.ContinuousObservation, m.Paused },
                        null, null, null, null));
        }
        if (kind == "highlights")
        {
            if (timeline || Text(request, "start") is not null || Text(request, "end") is not null)
                throw AgentError("TIME_CONTEXT", "Highlight results use the creator proposal's source clock; sequence/range projection is not available.");
            foreach (var proposal in highlightInputs)
                foreach (var highlight in proposal.Candidates)
                    rows.Add((highlight.Start.Ticks, Guid.Empty, -1, null, null, new { kind, proposal = proposal.Key, highlight.Id,
                        start = ShortTime(highlight.Start, space: "sourceContainerOffset"), end = ShortTime(highlight.End, space: "sourceContainerOffset"),
                        highlight.Score, highlight.Evidence, scoreMeaning = "Heuristic ranking, not a probability." }, null, null, null, null));
        }
        if (kind == "loudness")
        {
            if (timeline || Text(request, "start") is not null || Text(request, "end") is not null)
                throw AgentError("TIME_CONTEXT", "Integrated source loudness is a whole-stream measurement, not a timed interval.");
            foreach (var item in opened.Where(a => a.Artifact.Loudness is not null))
                rows.Add((0, item.Artifact.AssetId, item.Artifact.StreamIndex, null, null,
                    new { kind, item.Meta.Key, asset = AgentHandles.For("asset", item.Artifact.AssetId),
                        stream = item.Artifact.StreamIndex, item.Artifact.Loudness }, null, null, null, null));
        }
        if (timeline && rows.Count == 0 && unaligned > 0 && kind is "matches" or "segments" or "words")
            throw new AgentRequestException(new("ALIGNMENT_UNAVAILABLE", "The matching transcript observations cannot be placed on this sequence precisely.",
                "Inspect source-space results and their timingUsable flags, or generate a fresh aligned transcript.", Constraints: new { unalignedOccurrences = unaligned }));

        var ordered = rows.OrderBy(r => r.Sort).ThenBy(r => r.Asset).ThenBy(r => r.Stream);
        var lastByClock = new Dictionary<Guid, long>();
        int offset = Integer(request, "offset", 0), limit = Integer(request, "limit", 16), count = 0;
        if (offset < 0 || limit is < 1 or > 64) throw AgentError("PAGE_RANGE", "Use offset >= 0 and limit 1..64.");
        var page = new List<object>(limit);
        foreach (var row in ordered)
        {
            Guid clockId = timeline ? Guid.Empty : row.Asset;
            if (separation > 0 && lastByClock.TryGetValue(clockId, out long previous) && (decimal)row.Sort - previous < separation) continue;
            lastByClock[clockId] = row.Sort;
            int index = count++;
            if (index < offset || page.Count == limit) continue;
            if (row.Marker is not null) { page.Add(row.Marker); continue; }
            var m = row.Moment!;
            var item = row.Artifact!;
            var origin = new SourcePosition(item.Artifact.ContainerStart);
            var clock = assetMap[row.Asset].Streams.FirstOrDefault(s => s.Index == row.Stream)?.FrameRate;
            var from = m.Range.Start.Since(origin, TimeRounding.Floor);
            var to = m.Range.End.Since(origin, TimeRounding.Ceiling);
            bool rounded = !origin.Shift(from).EquivalentTo(m.Range.Start) || !origin.Shift(to).EquivalentTo(m.Range.End);
            page.Add(new { m.Kind, asset = AgentHandles.For("asset", row.Asset), stream = row.Stream, item.Meta.Key, m.Text, m.Value,
                m.Evidence, m.TimingUsable, source = new { start = ShortTime(from, clock, "sourceContainerOffset"),
                    end = ShortTime(to, clock, "sourceContainerOffset"), origin = item.Artifact.ContainerStart,
                    exactSourceRange = rounded ? m.Range : null, roundedOutward = rounded, m.Range.Epoch },
                sequence = row.At is { } at ? new { start = ShortTime(new(at), sequence!.Video.FrameRate),
                    end = ShortTime(new(row.End!.Value), sequence.Video.FrameRate), clip = AgentHandles.For("clip", row.Clip!.Value),
                    track = AgentHandles.For("track", row.Track!.Value) } : null });
        }
        return new { kind, space = timeline ? "sequence" : "sourceContainerOffset", total = count, offset, limit,
            nextOffset = page.Count > 0 && offset + page.Count < count ? (int?)(offset + page.Count) : null, items = page,
            artifactSet = set, provenance = opened.Select(a => a.Meta).ToArray(), unalignedOccurrences = unaligned };

        long Boundary(string field, FrameRate? rate, SequenceDocument? context, long fallback) => Text(request, field) is { } text
            ? AgentTime.Position(text, rate, context, !timeline && kind != "markers").Ticks : fallback;
        string? NearActivity(Guid asset, SourceRange r)
        {
            if (!activityIndices.TryGetValue(asset, out var index)) return null;
            var low = r.Start.Shift(new(-nearActivity)); var high = r.End.Shift(new(nearActivity));
            int first = 0, last = index.Length;
            while (first < last)
            {
                int middle = first + (last - first) / 2;
                if (index[middle].Start.CompareTo(high) <= 0) first = middle + 1;
                else last = middle;
            }
            return first > 0 && index[first - 1].End.CompareTo(low) >= 0 ? index[first - 1].source : null;
        }
        string? NearScene(Guid asset, SourceRange r)
        {
            if (!sceneIndices.TryGetValue(asset, out var index)) return null;
            var low = r.Start.Shift(new(-nearScene)); var high = r.End.Shift(new(nearScene));
            int first = Array.BinarySearch(index, (low, ""), Comparer<(SourcePosition At, string source)>.Create((a, b) => a.At.CompareTo(b.At)));
            if (first < 0) first = ~first;
            return first < index.Length && index[first].At.CompareTo(high) <= 0 ? index[first].source : null;
        }
    }

    private static readonly IComparer<SourcePosition> SourceOrder = Comparer<SourcePosition>.Create((a, b) => a.CompareTo(b));
    private static SourcePosition Max(SourcePosition a, SourcePosition b) => a.CompareTo(b) < 0 ? b : a;
    private static SourcePosition Min(SourcePosition a, SourcePosition b) => a.CompareTo(b) > 0 ? b : a;

    private CreatorProposal[] HighlightInputs() { lock (creatorGate) return creatorPlans.Values.ToArray(); }

    private static IEnumerable<Moment> Moments(AnalysisArtifact a, string kind, string? phrase)
    {
        bool TextMatches(string text) => phrase is null || text.Contains(phrase, StringComparison.OrdinalIgnoreCase);
        return kind switch
        {
            "words" => (a.Transcript?.Words ?? []).Where(w => TextMatches(w.Text)).Select(w => new Moment(w.Range, kind,
                ClipText(w.Text), Evidence: new { w.Id }, TimingUsable: w.TimingUsable)),
            "segments" => (a.Transcript?.Segments ?? []).Where(s => TextMatches(s.Text)).Select(s => new Moment(s.Range, kind,
                ClipText(s.Text), Evidence: new { s.Id, s.WordCount }, TimingUsable: s.WordCount > 0 &&
                a.Transcript!.Words.Skip(s.FirstWord).Take(s.WordCount).All(w => w.TimingUsable))),
            "matches" => TranscriptQueries.EnumerateMatches(a, phrase ?? throw AgentError("INVALID_REQUEST", "Matches requires transcriptContains."), context: 2)
                .Select(m => new Moment(m.Range, kind, ClipText(m.Text), Evidence: new { m.FirstWord, m.WordCount, wordIds = m.WordIds.Take(16).ToArray() }, TimingUsable: m.TimingUsable)),
            "silence" => a.Silence.Select(r => new Moment(r, kind)),
            "activity" => a.Activity.Select(r => new Moment(r.Range, r.Category, Value: r.Rms)),
            "scenes" => a.Scenes.Select(s => new Moment(new(new(s.Timestamp), new(s.Timestamp), s.Epoch), kind, Value: s.Score)),
            "waveform" => a.Waveform.IsEmpty ? [] : a.Waveform[^1].Bins.Select(b => new Moment(new(b.Start, b.End, b.Epoch), kind,
                Evidence: new { b.Samples, channels = b.Channels.Take(8).ToArray(), aggregation = "coarsest cached level" })),
            _ => []
        };
    }

    private static string ClipText(string text) => text.Length <= 220 ? text : text[..219] + "…";

    private static string[] ArtifactKeys(JsonElement request)
    {
        if (!request.TryGetProperty("artifacts", out var array)) return [];
        var keys = array.EnumerateArray().Select(a => a.GetString()!).Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length > 12) throw AgentError("ANALYSIS_LIMIT", "Provide at most 12 distinct analysis keys.");
        return keys;
    }

    private CachedAnalysis Metadata(string key)
    {
        if (key.Length != 64 || !key.All(char.IsAsciiHexDigit))
            throw AgentError("ANALYSIS_KEY", "A cache key must be a 64-character hex string.", key);
        return AnalysisDiscovery.Read(Path.Combine(Analysis.Cache.DirectoryPath, key.ToUpperInvariant() + ".vca"))
            ?? throw AgentError("ANALYSIS_MISSING", "Analysis artifact is missing or unreadable.", key, "Start the relevant analysis again.");
    }

    private async Task<ObservationArtifact> OpenArtifact(CachedAnalysis descriptor, CancellationToken cancellation)
    {
        try
        {
            var artifact = await Analysis.ResultAsync(descriptor.Key, cancellation).ConfigureAwait(false);
            if (Metadata(descriptor.Key).ContentVersion != descriptor.ContentVersion)
                throw AgentError("ARTIFACT_SET_CHANGED", "Analysis content changed while this query was running.", descriptor.Key);
            return new(artifact, descriptor);
        }
        catch (FileNotFoundException e) { throw AgentError("ANALYSIS_MISSING", e.Message, descriptor.Key, "Analyze the current source again."); }
        catch (InvalidDataException e) { throw AgentError("ANALYSIS_STALE", e.Message, descriptor.Key, "Analyze the current source again."); }
    }
}
