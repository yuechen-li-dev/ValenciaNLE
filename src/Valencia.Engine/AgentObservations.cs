using System.Globalization;
using System.Text.Json;
using Valencia.Analysis;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.Engine;

public sealed record AgentObservation(string Mode, string[] Files, object Details);

public sealed partial class EditingEngine
{
    private async Task<AgentObservation> ObserveAsync(ProjectSnapshot snapshot, JsonElement request, CancellationToken cancellation)
    {
        string mode = Text(request, "mode")!;
        string output = NewPng(RequiredText(request, "output"));
        if (!await reviewSlot.WaitAsync(0, cancellation).ConfigureAwait(false))
            throw AgentError("REVIEW_BUSY", "Another observation is rendering in this session.", hint: "Wait for the current observation to finish.");
        try
        {
            if (mode is "sourceAudio" or "mixAudio")
                return await AudioObservationAsync(snapshot, request, output, cancellation).ConfigureAwait(false);
            return await Task.Run(() => VideoObservation(snapshot, request, output, cancellation), cancellation).ConfigureAwait(false);
        }
        catch (FileNotFoundException e) { throw AgentError("MISSING_MEDIA", e.Message, hint: "Relink the missing source media before rendering."); }
        catch (DllNotFoundException e) { throw AgentError("CODEC_UNAVAILABLE", e.Message, hint: "Configure the pinned FFmpeg shared libraries."); }
        catch (NotSupportedException e) { throw AgentError("UNSUPPORTED_MEDIA", e.Message, hint: "Inspect session_info and the asset stream details for supported media."); }
        finally { reviewSlot.Release(); }
    }

    private AgentObservation VideoObservation(ProjectSnapshot live, JsonElement request, string output, CancellationToken cancellation)
    {
        string mode = Text(request, "mode")!;
        bool source = mode == "source", paired = mode == "paired";
        string? candidateRef = Text(request, "candidate"), otherRef = Text(request, "other"), profileRef = Text(request, "profile");
        if (source && (candidateRef is not null || otherRef is not null || profileRef is not null || Text(request, "sequence") is not null)
            || mode == "sequence" && candidateRef is not null || !paired && otherRef is not null)
            throw AgentError("OBSERVATION_SCOPE", "Source, sequence, and candidate modes must not mix their coordinate or snapshot selectors.");
        int maximum = Integer(request, "maxFrames", 6), columns = Integer(request, "columns", 3), edge = Integer(request, "maxEdge", 320);
        if (maximum is < 1 or > 12 || columns is < 1 or > 4 || edge is < 160 or > 640)
            throw AgentError("OBSERVATION_LIMIT", "Use maxFrames 1..12, columns 1..4, and maxEdge 160..640.");
        string Stamp(string label, string? handle = null) => $"Revision {live.Revision}  |  " + label + (handle is null ? "" : "  |  " + handle);

        if (source)
        {
            var asset = AgentHandles.Asset(live.Document, RequiredText(request, "asset"));
            var stream = GetObservationStream(asset, request, TrackKind.Video);
            CheckSource(asset);
            var origin = new SourcePosition(stream.Start ?? new MediaTimestamp(0, stream.TimeBaseNumerator, stream.TimeBaseDenominator));
            long? duration = stream.End is { } end ? new SourcePosition(end).Since(origin, TimeRounding.Ceiling).Ticks : null;
            var times = PickTimes(request, stream.FrameRate, null, maximum, duration);
            var handle = AgentHandles.For("asset", asset.Id);
            var rendered = AgentReviewImage.Sheet(times.Length, Math.Min(columns, times.Length), edge, Stamp($"Source video · stream {stream.Index}", handle), i =>
            {
                var requested = times[i];
                var decoded = SourceFrameThumbnail.Read(asset, stream.Index, requested, cancellation);
                var pts = new SourcePosition(decoded.SourceTimestamp);
                var actual = AgentTime.Describe(pts.Since(origin, TimeRounding.NearestAwayFromZero), "sourceStreamOffset", stream.FrameRate);
                var labels = new[] { "Decoded " + (actual.Timecode ?? actual.Seconds + "s") + (actual.Frame is { } f ? $"  f{f}" : ""),
                    handle, $"stream {stream.Index} · {stream.Codec}", ClipText(asset.Name) };
                return new(decoded.Rgba, decoded.Width, decoded.Height, labels, new { requested = AgentTime.Describe(requested, "sourceStreamOffset", stream.FrameRate),
                    decoded = actual, decodedPts = decoded.SourceTimestamp, sourceOrigin = origin.Timestamp, asset = handle, stream = stream.Index });
            }, output, cancellation);
            return new(mode, [output], new { baseRevision = live.Revision, rendered.Width, rendered.Height, rendered.Samples });
        }

        if (Text(request, "asset") is not null || request.TryGetProperty("stream", out _))
            throw AgentError("OBSERVATION_SCOPE", "Use the source mode when selecting an asset or stream.");
        AgentCandidate? selected = mode is "candidate" or "paired" ? FindCandidate(live, RequiredText(request, "candidate")) : null;
        AgentCandidate? other = otherRef is null ? null : FindCandidate(live, otherRef);
        var left = other?.Snapshot ?? live;
        var right = selected?.Snapshot ?? live;
        var sequenceRef = Text(request, "sequence") ?? (selected is not null ? AgentHandles.For("sequence", selected.Plan.SequenceId) : null);
        var before = SelectSequence(left.Document, sequenceRef);
        var after = SelectSequence(right.Document, AgentHandles.For("sequence", before.Id));
        if (selected is not null && selected.Plan.SequenceId != before.Id || other is not null && other.Plan.SequenceId != before.Id)
            throw AgentError("CANDIDATE_SCOPE", "Observation and candidates must address the same sequence.");
        string correspondence = Text(request, "correspondence") ?? "sequence";
        if (!paired && Text(request, "correspondence") is not null || correspondence == "sequence" && Text(request, "anchorClip") is not null)
            throw AgentError("OBSERVATION_SCOPE", "Content correspondence and anchorClip are for paired review only.");
        string? beforeOutput = paired ? NewPng(RequiredText(request, "beforeOutput")) : null;
        if (beforeOutput is not null && string.Equals(beforeOutput, output, StringComparison.OrdinalIgnoreCase))
            throw AgentError("OUTPUT_CONFLICT", "Paired observation requires two different output paths.");
        var beforeProfile = paired ? FindProfile(before, profileRef) : null;
        var afterProfile = FindProfile(after, profileRef);
        var primary = mode == "candidate" ? after : before;
        var requestedTimes = PickTimes(request, primary.Video.FrameRate, primary, maximum,
            paired ? Math.Max(before.Duration.Ticks, after.Duration.Ticks) : primary.Duration.Ticks,
            paired ? null : afterProfile);
        var leftTimes = requestedTimes.Select(t => SnapSample(t, before, beforeProfile)).ToArray();
        SequenceTime?[] rightTimes = requestedTimes.Select(t => SnapSample(t, after, afterProfile)).ToArray();
        if (!paired && rightTimes.Any(t => t is null))
            throw AgentError("TIME_RANGE", "A requested sample lies outside the selected sequence or output profile.");
        var notes = new string?[requestedTimes.Length];
        var mapped = new SequenceTime?[requestedTimes.Length];
        if (paired && correspondence == "content")
        {
            Guid anchorId = AgentHandles.Resolve(RequiredText(request, "anchorClip"), "clip", before.Tracks.SelectMany(t => t.Clips).Select(c => c.Id));
            var anchor = before.Tracks.SelectMany(t => t.Clips).First(c => c.Id == anchorId);
            if (anchor.Title is not null || anchor.Retime?.Hold is not null)
                throw AgentError("AMBIGUOUS_REFERENCE", "Content correspondence requires a non-title, non-freeze source clip.");
            var beforeTrack = before.Tracks.First(t => t.Clips.Contains(anchor));
            var target = after.Tracks.Where(t => t.Kind == beforeTrack.Kind).SelectMany(t => t.Clips)
                .Where(c => c.AssetId == anchor.AssetId && c.StreamIndex == anchor.StreamIndex && c.Retime?.Hold is null).ToArray();
            for (int i = 0; i < requestedTimes.Length; i++)
            {
                rightTimes[i] = null;
                if (leftTimes[i] is not { } t || t.Ticks < anchor.Start.Ticks || t.Ticks >= anchor.End.Ticks)
                { notes[i] = "Sample is outside the anchor clip"; continue; }
                var position = anchor.MapToSource(t);
                var matches = target.Where(c => c.SourceIn.CompareTo(position) <= 0 && position.CompareTo(c.SourceOut) < 0).ToArray();
                if (matches.Length != 1) { notes[i] = matches.Length == 0 ? "Source content removed" : "Multiple retained occurrences"; continue; }
                try
                {
                    mapped[i] = matches[0].MapToTimeline(position);
                    // Content comparisons intentionally evaluate the mapped source point, not the preceding timeline frame.
                    rightTimes[i] = InWindow(mapped[i]!.Value, after, afterProfile) ? mapped[i] : null;
                    if (rightTimes[i] is null) notes[i] = "Mapped content is outside the profile window";
                }
                catch (ArgumentException) { notes[i] = "Source is not on the target edit grid"; }
            }
        }

        var leftImage = paired ? WriteSequence(left, before, beforeProfile, leftTimes, beforeOutput!, Stamp("Before composite", other?.Handle)) : default;
        var rightImage = WriteSequence(right, after, afterProfile, rightTimes, output,
            Stamp(mode == "sequence" ? "Current composite" : "Candidate composite", selected?.Handle));
        object rightDetails = new { rightImage.Width, rightImage.Height, rightImage.Samples };
        object? leftDetails = paired ? new { leftImage.Width, leftImage.Height, leftImage.Samples } : null;
        return new(mode, paired ? [beforeOutput!, output] : [output], new { baseRevision = live.Revision, candidate = selected?.Handle,
            other = other?.Handle, correspondence = paired ? correspondence : null, summary = selected?.Summary, otherSummary = other?.Summary,
            before = leftDetails, after = rightDetails, mapping = paired ? requestedTimes.Select((at, i) => new
            { requested = at, before = leftTimes[i], after = rightTimes[i], mapped = mapped[i], note = notes[i] }).ToArray() : null });

        (int Width, int Height, object[] Samples) WriteSequence(ProjectSnapshot state, SequenceDocument sequence, OutputProfile? profile,
            SequenceTime?[] times, string path, string header)
        {
            CheckMedia(state);
            var query = new TimelineQueries(state);
            using var evaluator = new SequenceEvaluator(state, sequence.Id, profile);
            var tracks = sequence.Tracks.ToDictionary(t => t.Id);
            return AgentReviewImage.Sheet(times.Length, Math.Min(columns, times.Length), edge, header, i =>
            {
                if (times[i] is not { } time)
                {
                    string note = notes[i] ?? "Outside the evaluated sequence";
                    return new(null, 0, 0, [note], new { requested = requestedTimes[i], unavailable = note });
                }
                var info = AgentTime.Describe(time, "sequence", profile?.FrameRate ?? sequence.Video.FrameRate);
                var frame = evaluator.Video(time, cancellation, includeReviewMetadata: true);
                var layers = query.Evaluate(sequence.Id, time).Where(c => c.Kind == TrackKind.Video).Reverse()
                    .Select(c => new { clip = AgentHandles.For("clip", c.Clip.Id), track = AgentHandles.For("track", c.TrackId),
                        role = tracks[c.TrackId].Role, tracks[c.TrackId].Name,
                        asset = c.Clip.Title is null ? AgentHandles.For("asset", c.Asset.Id) : null,
                        c.Stream.Index, pts = frame.DecodedByClip!.TryGetValue(c.Clip.Id, out var pts) ? (MediaTimestamp?)pts : null }).ToArray();
                var labels = new List<string> { $"{info.Timecode}  f{info.Frame}" + (mode == "paired" && correspondence == "content" ? " · content" : "") };
                if (layers.Length == 0) labels.Add("No visible video clips");
                foreach (var layer in layers.Take(2))
                {
                    labels.Add(layer.clip);
                    labels.Add((layer.role ?? layer.Name) + " · " + layer.track);
                }
                if (layers is [{ asset: { } assetHandle }]) labels.Add(assetHandle);
                return new(frame.Rgba, frame.Width, frame.Height, labels, new { requested = requestedTimes[i], actual = info, layers,
                    caption = frame.ReviewCaption is { } line ? new { words = line.Words.Select(w => w.Text).ToArray(), line.ActiveWord } : null });
            }, path, cancellation);
        }
    }

    private async Task<AgentObservation> AudioObservationAsync(ProjectSnapshot snapshot, JsonElement request, string output, CancellationToken cancellation)
    {
        string mode = Text(request, "mode")!;
        if (request.TryGetProperty("at", out _) || request.TryGetProperty("candidate", out _) && mode == "sourceAudio" ||
            Text(request, "profile") is not null || Text(request, "other") is not null)
            throw AgentError("OBSERVATION_SCOPE", "Audio observations require a range; use sourceAudio for cached stream data or mixAudio for a sequence.");
        int width = Integer(request, "maxEdge", 864);
        if (width is < 480 or > 1200) throw AgentError("OBSERVATION_LIMIT", "Audio width (maxEdge) must be 480..1200 pixels.");
        const int height = 233;
        var keys = ArtifactKeys(request);
        var bins = new List<AudioReviewBin>();
        var annotations = new List<(long From, long To, string Label, string Kind)>();
        string title, footer, space;
        long first, last;
        object provenance;
        string? candidateRef = Text(request, "candidate");
        if (mode == "sourceAudio")
        {
            var asset = AgentHandles.Asset(snapshot.Document, RequiredText(request, "asset"));
            var stream = GetObservationStream(asset, request, TrackKind.Audio);
            CheckSource(asset);
            var candidates = keys.Length == 0 ? AnalysisDiscovery.Enumerate(Analysis.Cache, cancellation)
                .Where(x => x.AssetId == asset.Id && x.StreamIndex == stream.Index).OrderBy(x => x.Key).ToArray()
                : keys.Select(Metadata).Where(x => x.AssetId == asset.Id && x.StreamIndex == stream.Index).ToArray();
            var audio = await Select("audio", required: true).ConfigureAwait(false);
            var transcript = await Select("transcript").ConfigureAwait(false);
            var loudness = await Select("loudness").ConfigureAwait(false);
            var artifact = audio!.Artifact;
            var origin = new SourcePosition(artifact.ContainerStart);
            if (artifact.Waveform.IsDefaultOrEmpty || artifact.Waveform[0].Bins.IsDefaultOrEmpty)
                throw AgentError("ANALYSIS_MISSING", "The selected audio artifact has no waveform bins.", audio.Meta.Key);
            first = AgentTime.Position(RequiredText(request, "start"), source: true).Ticks;
            last = AgentTime.Position(RequiredText(request, "end"), source: true).Ticks;
            ValidateAudioRange(first, last, long.MaxValue);
            long T(SourcePosition p) => p.Since(origin, TimeRounding.NearestAwayFromZero).Ticks;
            var matching = artifact.Waveform.Select(level => level.Bins.Where(b => T(b.Start) < last && T(b.End) > first))
                .FirstOrDefault(items => items.Take(width + 1).Count() <= width) ?? artifact.Waveform[^1].Bins.Where(b => T(b.Start) < last && T(b.End) > first);
            var available = matching.Where(b => !b.Channels.IsEmpty).ToArray();
            int groupSize = Math.Max(1, (available.Length + width - 1) / width);
            foreach (var group in available.Chunk(groupSize))
                bins.Add(new(Position(T(group[0].Start)), Position(T(group[^1].End)),
                    group.Min(b => b.Channels.Min(c => c.Minimum)), group.Max(b => b.Channels.Max(c => c.Maximum)),
                    Math.Sqrt(group.Sum(b => b.Samples * b.Channels.Average(c => c.Rms * c.Rms)) / Math.Max(1, group.Sum(b => b.Samples)))));
            if (bins.Count == 0) throw AgentError("TIME_RANGE", "No cached waveform bins cover this source range.");
            foreach (var silence in artifact.Silence.Where(r => T(r.Start) < last && T(r.End) > first).Take(256))
                annotations.Add((T(silence.Start), T(silence.End), "", "silence"));
            foreach (var activity in artifact.Activity.Where(r => r.Category == "active" && T(r.Range.Start) < last && T(r.Range.End) > first).Take(256))
                annotations.Add((T(activity.Range.Start), T(activity.Range.End), "", "activity"));
            foreach (var word in (transcript?.Artifact.Transcript?.Words ?? []).Where(w => w.TimingUsable && T(w.Range.Start) < last && T(w.Range.End) > first).Take(96))
                annotations.Add((T(word.Range.Start), T(word.Range.End), ClipText(word.Text), "word"));
            if (Text(request, "sequence") is { } seq)
            {
                var sequence = AgentHandles.Sequence(snapshot.Document, seq);
                foreach (var t in sequence.Tracks)
                    foreach (var c in t.Clips.Where(c => c.AssetId == asset.Id && c.StreamIndex == stream.Index))
                        foreach (long tick in new[] { T(c.SourceIn), T(c.SourceOut) }.Where(p => p >= first && p < last).Take(256))
                            if (annotations.Count < 800) annotations.Add((tick, tick, t.Role ?? t.Name, "cut"));
            }
            space = "sourceContainerOffset";
            title = $"{asset.Role ?? "Source"} {AgentHandles.For("asset", asset.Id)} · stream {stream.Index} · cached audio";
            footer = loudness?.Artifact.Loudness?.IntegratedLufs is { } lufs
                ? FormattableString.Invariant($"Whole-stream measured {lufs:0.0} LUFS · plotted levels are RMS, not LUFS") : "Cached sample peaks and RMS; no integrated loudness claim";
            provenance = new { audio = audio.Meta.Key, transcript = transcript?.Meta.Key, loudness = loudness?.Artifact.Loudness, artifact.ContainerStart };

            async Task<ObservationArtifact?> Select(string kind, bool required = false)
            {
                var found = candidates.Where(x => x.Kind == kind).ToArray();
                if (found.Length > 1) throw AgentError("AMBIGUOUS_REFERENCE", $"More than one {kind} artifact applies; pin the desired keys.");
                if (found.Length == 0)
                {
                    if (required) throw AgentError("ANALYSIS_MISSING", "Generate audio waveform analysis for this stream.");
                    return null;
                }
                return await OpenArtifact(found[0], cancellation).ConfigureAwait(false);
            }
        }
        else
        {
            if (Text(request, "asset") is not null || request.TryGetProperty("stream", out _) || keys.Length > 0)
                throw AgentError("OBSERVATION_SCOPE", "Mix audio is computed by the sequence evaluator, not source artifacts.");
            var candidate = candidateRef is null ? null : FindCandidate(snapshot, candidateRef);
            var state = candidate?.Snapshot ?? snapshot;
            var sequence = SelectSequence(state.Document, Text(request, "sequence") ??
                (candidate is null ? null : AgentHandles.For("sequence", candidate.Plan.SequenceId)));
            if (candidate is not null && sequence.Id != candidate.Plan.SequenceId)
                throw AgentError("CANDIDATE_SCOPE", "Audio observation must address the candidate's sequence.");
            first = AgentTime.Position(RequiredText(request, "start"), sequence: sequence).Ticks;
            last = AgentTime.Position(RequiredText(request, "end"), sequence: sequence).Ticks;
            ValidateAudioRange(first, last, sequence.Duration.Ticks);
            if (last - first > 30 * SequenceTime.TicksPerSecond)
                throw AgentError("OBSERVATION_LIMIT", "Rendered mix is limited to 30 seconds per observation.");
            CheckMedia(state);
            await Task.Run(() =>
            {
                int rate = sequence.AudioSampleRate;
                long sampleStart = TimelineTime.SampleIndex(new(first), rate, TimeRounding.Ceiling);
                long sampleEnd = TimelineTime.SampleIndex(new(last), rate, TimeRounding.Ceiling);
                if (sampleEnd <= sampleStart) throw AgentError("TIME_RANGE", "The range contains no mix samples.");
                using var evaluator = new SequenceEvaluator(state, sequence.Id);
                int stride = (int)Math.Max(1, (sampleEnd - sampleStart + width - 1) / width);
                for (long at = sampleStart; at < sampleEnd; at += stride)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(stride, sampleEnd - at);
                    float[] samples = evaluator.Audio(default, at, count, cancellation);
                    float low = samples[0], high = samples[0]; double sum = 0;
                    foreach (float sample in samples) { low = Math.Min(low, sample); high = Math.Max(high, sample); sum += sample * sample; }
                    bins.Add(new(Position(TimelineTime.FromSamples(at, rate).Ticks), Position(TimelineTime.FromSamples(at + count, rate).Ticks),
                        low, high, Math.Sqrt(sum / samples.Length)));
                }
                foreach (var t in sequence.Tracks)
                    foreach (long cut in t.Clips.SelectMany(c => new[] { c.Start.Ticks, c.End.Ticks }).Distinct().Where(x => x >= first && x < last).Take(256))
                        if (annotations.Count < 256) annotations.Add((cut, cut, t.Role ?? t.Name, "cut"));
                var words = (sequence.Creator?.Words ?? []).ToLookup(w => w.AssetId);
                var seen = new HashSet<(Guid, long, long)>();
                foreach (var c in sequence.Tracks.SelectMany(t => t.Clips).Where(c => c.Retime?.Hold is null && c.Start.Ticks < last && c.End.Ticks > first))
                    foreach (var w in words[c.AssetId])
                    {
                        if (annotations.Count >= 352) break;
                        var a = Max(c.SourceIn, w.Start); var b = Min(c.SourceOut, w.End);
                        if (a.CompareTo(b) >= 0) continue;
                        try
                        {
                            long begin = c.MapSourceBoundary(a).Ticks, end = c.MapSourceBoundary(b).Ticks;
                            if (begin < last && end > first && seen.Add((w.Id, begin, end))) annotations.Add((begin, end, ClipText(w.Text), "word"));
                        }
                        catch (ArgumentException) { /* Alignment is not exact on this retained clip's source grid. */ }
                    }
            }, cancellation).ConfigureAwait(false);
            space = "sequence";
            var roles = string.Join(" + ", sequence.Tracks.Where(t => t.Kind == TrackKind.Audio && !t.State.Muted).Take(4).Select(t => t.Role ?? t.Name));
            title = $"Rev {snapshot.Revision} · edited stereo mix · {roles}";
            footer = "Rendered sample peaks and RMS after gains, ducking and limiter; not integrated LUFS or a listening test";
            provenance = new { revision = snapshot.Revision, candidate = candidate?.Handle, sequence = AgentHandles.For("sequence", sequence.Id),
                tracks = sequence.Tracks.Where(t => t.Kind == TrackKind.Audio).Take(32).Select(t => new { handle = AgentHandles.For("track", t.Id), t.Name, t.Role, t.State.Muted }).ToArray() };
        }
        var marks = annotations.Select(a => new AudioReviewMark(Position(a.From), Position(a.To), a.Label, a.Kind)).ToArray();
        string relative = FormattableString.Invariant($"{(decimal)first / SequenceTime.TicksPerSecond:0.###}s–{(decimal)last / SequenceTime.TicksPerSecond:0.###}s");
        AgentReviewImage.Audio(width, height, title + " · " + relative, footer, bins, marks, output, cancellation);
        return new(mode, [output], new { width, height, space, start = new SequenceTime(first), end = new SequenceTime(last),
            provenance, waveformBins = bins.Count, marks = annotations.Take(64).Select(a => new { start = new SequenceTime(a.From),
                end = new SequenceTime(a.To), a.Kind, a.Label }).ToArray(), markCount = annotations.Count });
        double Position(long tick) => (double)((decimal)tick - first) / (last - first);
    }

    private static void ValidateAudioRange(long first, long last, long duration)
    {
        if (first < 0 || last <= first || last > duration) throw AgentError("TIME_RANGE", "Choose a nonempty audio range within its clock.");
    }

    private static SequenceTime[] PickTimes(JsonElement request, FrameRate? clock, SequenceDocument? sequence, int maximum, long? duration,
        OutputProfile? profile = null)
    {
        if (request.TryGetProperty("at", out var array))
        {
            if (Text(request, "start") is not null || Text(request, "end") is not null || Text(request, "cadence") is not null)
                throw AgentError("INVALID_REQUEST", "Use explicit at samples or a time range and cadence, not both.");
            if (array.GetArrayLength() == 0 || array.GetArrayLength() > maximum)
                throw AgentError("OBSERVATION_LIMIT", "Explicit sample count must be between 1 and maxFrames.");
            return array.EnumerateArray().Select(p => AgentTime.Position(p.GetString()!, clock, sequence, sequence is null)).ToArray();
        }
        long start = Text(request, "start") is { } first ? AgentTime.Position(first, clock, sequence, sequence is null).Ticks : profile?.Start.Ticks ?? 0;
        long end = Text(request, "end") is { } last ? AgentTime.Position(last, clock, sequence, sequence is null).Ticks :
            Math.Min(duration ?? long.MaxValue, profile?.End?.Ticks ?? long.MaxValue);
        if (end <= start || duration is { } d && end > d) throw AgentError("TIME_RANGE", "Choose a nonempty sample range within the observation window.");
        long step = Text(request, "cadence") is { } cadence ? AgentTime.Duration(cadence, clock).Ticks : (long)(((Int128)end - start + maximum - 1) / maximum);
        if (step <= 0) throw AgentError("TIME_RANGE", "Cadence must be positive.");
        var list = new List<SequenceTime>();
        for (long at = start; at < end && list.Count < maximum;)
        {
            list.Add(new(at));
            if (step >= end - at) break;
            at += step;
        }
        return [.. list];
    }

    private static bool InWindow(SequenceTime at, SequenceDocument sequence, OutputProfile? profile) =>
        at.Ticks >= (profile?.Start.Ticks ?? 0) && at.Ticks < Math.Min(sequence.Duration.Ticks, profile?.End?.Ticks ?? long.MaxValue);
    private static SequenceTime? SnapSample(SequenceTime at, SequenceDocument sequence, OutputProfile? profile)
    {
        if (!InWindow(at, sequence, profile)) return null;
        long origin = profile?.Start.Ticks ?? 0;
        long relative = TimelineTime.SnapToFrame(new(at.Ticks - origin), profile?.FrameRate ?? sequence.Video.FrameRate, TimeRounding.Floor).Ticks;
        return new(checked(origin + relative));
    }

    private static OutputProfile? FindProfile(SequenceDocument sequence, string? reference)
    {
        if (reference is null) return null;
        var profiles = sequence.Creator?.Profiles ?? [];
        var id = AgentHandles.Resolve(reference, "profile", profiles.Select(p => p.Id));
        return profiles.First(p => p.Id == id);
    }

    private void CheckSource(MediaAsset asset)
    {
        Policy?.Check(asset.SourcePath);
        if (!File.Exists(asset.SourcePath)) throw AgentError("MISSING_MEDIA", "Source media file is unavailable.", AgentHandles.For("asset", asset.Id),
            "Relink the asset before observing it.");
    }

    private static AssetStream GetObservationStream(MediaAsset asset, JsonElement request, TrackKind kind)
    {
        int id = Integer(request, "stream", -1);
        return asset.Streams.FirstOrDefault(s => s.Index == id && s.Kind == kind)
            ?? throw AgentError("UNKNOWN_STREAM", $"Choose a {kind.ToString().ToLowerInvariant()} stream listed for this asset.", AgentHandles.For("asset", asset.Id));
    }

    private string NewPng(string name)
    {
        string path = OutputPath(name);
        if (!Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw AgentError("INVALID_OUTPUT", "Observation output must have a .png extension.", name);
        if (File.Exists(path)) throw AgentError("OUTPUT_EXISTS", "Observation output already exists; choose a different name.", name);
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw AgentError("INVALID_OUTPUT", "Observation output directory does not exist.", name);
        return path;
    }
}
