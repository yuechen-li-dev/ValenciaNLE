using System.Collections.Immutable;
using Valencia.Analysis;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;

namespace Valencia.Engine;

public sealed record EditorialRange(string Start, string End, double Score, string Reason, string? Topic = null);
public sealed record EditorialSettings(
    string? Sequence = null, string? Transcript = null, string? Audio = null, string? Scenes = null,
    ImmutableArray<string>? Topics = null, ImmutableArray<string>? Vocabulary = null,
    ImmutableArray<Guid>? WordIds = null, ImmutableArray<Guid>? OccurrenceIds = null,
    ImmutableArray<EditorialRange>? Ranges = null, string Mode = "retain", int ContextWords = 18, int PaddingMs = 100,
    int MinimumPauseMs = 800, int MergeDistanceMs = 150, string? Track = null, string? TrackRole = null,
    string? SpeechRole = null, string? Asset = null, string? AssetRole = null, int? Stream = null,
    string? Start = null, string? End = null, string Offset = "12f", string SourceStart = "0s",
    string Fit = "fill", double FocalX = .5, double FocalY = .5, int? Width = null, int? Height = null,
    string Target = "10m", string Minimum = "10s", string Maximum = "45s", string ShortDuration = "20s",
    int Shorts = 3, ImmutableArray<int>? MusicStreams = null, bool Normalize = true,
    double AttenuationDb = -12, int AttackMs = 80, int ReleaseMs = 300, int HoldMs = 200,
    string? Text = null, string? Subtitle = null, string? CaptionFont = null, int? CaptionSize = null,
    uint? CaptionColor = null, uint? CaptionHighlight = null, double? CaptionY = null,
    string? Observations = null, string? Subject = null, string? Clip = null, string? OtherClip = null,
    bool SourceOrder = true, string MinimumSeparation = "0s", int? CaptionWordsPerLine = null,
    string? CaptionBackdrop = null, int? CaptionFadeMs = null, ImmutableArray<EditorialRange>? RequiredRanges = null,
    ImmutableArray<EditorialRange>? ExcludedRanges = null, bool DiversifyTopics = false);

public sealed record EditorialPlanResult(EditPlan? Plan, ImmutableArray<string> Diagnostics, object? Evidence);

/// <summary>Composes existing edit primitives in a private session and emits a single plan against the live base.</summary>
public sealed class EditorialPlanner : IDisposable
{
    private readonly EditingEngine staged;
    private readonly ProjectSnapshot basis;
    private readonly CancellationToken cancellation;
    private readonly List<PlanOperation> operations = [];
    private readonly Dictionary<Guid, SourcePrecondition> sources = [];
    private readonly List<string> diagnostics = [];
    private object? evidence;
    private SequenceDocument Sequence => staged.Query().Sequence(SequenceId);
    private Guid SequenceId { get; }

    public EditorialPlanner(EditingEngine engine, ProjectSnapshot snapshot, EditorialSettings settings, CancellationToken cancellation)
    {
        basis = snapshot;
        this.cancellation = cancellation;
        SequenceId = settings.Sequence is { } reference
            ? AgentHandles.Sequence(snapshot.Document, reference).Id
            : snapshot.Document.Sequences.Length == 1
                ? snapshot.Document.Sequences[0].Id
                : throw new ArgumentException("Select a sequence explicitly.");
        staged = new EditingEngine(new EditingSession(snapshot, historyLimit: 0), engine.Policy,
            analysisDirectory: engine.Analysis.Cache.DirectoryPath);
    }

    public EditorialPlanResult Finish()
    {
        cancellation.ThrowIfCancellationRequested();
        EditPlan? plan = operations.Count == 0 ? null : new(1, basis.Document.Id, EngineJson.Exact(basis.Revision),
            SequenceId, [.. operations], "Editorial workflow v1", sources.Count == 0 ? null : [.. sources.Values.OrderBy(s => s.AssetId)]);
        return new(plan, [.. diagnostics], evidence);
    }

    public object? TakeEvidence()
    {
        var current = evidence;
        evidence = null;
        return current;
    }

    public void Tighten(string audio, int minimumPauseMs, int paddingMs, int mergeDistanceMs)
    {
        var policy = new RoughCutPolicy(MinimumRemovalMs: minimumPauseMs, PrePaddingMs: paddingMs,
            PostPaddingMs: paddingMs, MergeDistanceMs: mergeDistanceMs);
        var result = staged.RoughCutAsync(SequenceId, audio, policy, cancellation).GetAwaiter().GetResult();
        diagnostics.AddRange(result.Diagnostics.Take(8));
        evidence = new { result.AnalysisKey, removals = result.Removed.Length, result.EstimatedDuration };
        if (result.Plan is { } plan) Add(plan);
    }

    public void Fillers(EditorialSettings settings, string[] defaults)
    {
        string key = Require(settings.Transcript, "transcript");
        var artifact = Artifact(key);
        var vocabulary = settings.Vocabulary ?? [.. defaults];
        ValidateTerms(vocabulary);
        var matches = vocabulary.SelectMany(word => TranscriptQueries.EnumerateMatches(artifact, word, false, 4)
            .Where(match => match.TimingUsable && match.Range.Epoch == 0)
            .Select(match => new { vocabulary = word, ambiguous = IsAmbiguousFiller(word), match.Text, match.WordIds, match.Range }))
            .DistinctBy(match => (match.vocabulary, match.WordIds[0])).Take(64).ToArray();
        evidence = matches;
        if (settings.WordIds is not { Length: > 0 } chosen)
        {
            diagnostics.Add("Vocabulary matches are review suggestions. Supply the exact reviewed word IDs to plan a cut.");
            return;
        }
        if (chosen.Any(id => !matches.Any(match => match.WordIds.Contains(id))))
            throw new ArgumentException("Every selected filler word must be in the returned review page.");
        TextEdit(settings, key, "remove", chosen, 0);
    }

    public void Topic(EditorialSettings settings)
    {
        string key = Require(settings.Transcript, "transcript");
        var artifact = Artifact(key);
        var transcript = artifact.Transcript ?? throw new ArgumentException("A transcript artifact is required.");
        var topics = settings.Topics ?? [];
        ValidateTerms(topics);
        if (settings.ContextWords is < 0 or > 100) throw new ArgumentException("Context words must be 0..100.");
        var selected = new HashSet<Guid>();
        foreach (string topic in topics)
            foreach (var match in TranscriptQueries.EnumerateMatches(artifact, topic, false, 0).Where(x => x.TimingUsable).Take(128))
            {
                int start = Math.Max(0, match.FirstWord - settings.ContextWords);
                int end = Math.Min(transcript.Words.Length, match.FirstWord + match.WordCount + settings.ContextWords);
                foreach (var word in transcript.Words[start..end])
                    if (word.TimingUsable && word.Range.Epoch == match.Range.Epoch) selected.Add(word.Id);
            }
        foreach (var range in ReadRanges(settings.Ranges, Sequence.Video.FrameRate))
        {
            var origin = new SourcePosition(artifact.ContainerStart);
            var start = origin.Shift(range.Start);
            var end = origin.Shift(range.End);
            foreach (var word in transcript.Words)
                if (word.TimingUsable && word.Range.Epoch == 0 && word.Range.Start.CompareTo(end) < 0 && word.Range.End.CompareTo(start) > 0)
                    selected.Add(word.Id);
        }
        if (selected.Count == 0) throw new ArgumentException("No timing-usable topic matches.");
        evidence = new { words = selected.Count, topics, externalRanges = settings.Ranges?.Length ?? 0 };
        TextEdit(settings, key, settings.Mode, [.. selected.Order()], settings.PaddingMs);
    }

    private void TextEdit(EditorialSettings settings, string key, string mode, ImmutableArray<Guid> words, int padding)
    {
        var result = staged.TextEditAsync(new(SequenceId, key, mode, words, settings.OccurrenceIds, padding), cancellation)
            .GetAwaiter().GetResult();
        diagnostics.AddRange(result.Diagnostics.Take(8));
        if (result.Plan is { } plan) Add(plan);
        else evidence = new { matches = evidence, result.Occurrences };
    }

    public void SplitEdit(EditorialSettings settings, bool audioLeads)
    {
        var track = Track(settings, TrackKind.Audio);
        var cut = At(settings.Start);
        if (!Sequence.Tracks.Where(t => t.Kind == TrackKind.Video).Any(t =>
            t.Clips.Any(c => c.End == cut) && t.Clips.Any(c => c.Start == cut)))
            throw new ArgumentException("Select an abutting picture cut.");
        var outgoing = track.Clips.SingleOrDefault(c => c.End == cut);
        var incoming = track.Clips.SingleOrDefault(c => c.Start == cut);
        if (outgoing is null || incoming is null || outgoing.Retime is not null || incoming.Retime is not null)
            throw new ArgumentException("The selected audio track needs an original-speed cut aligned to the picture cut.");
        long amount = PositiveDuration(settings.Offset);
        var next = new SequenceTime(checked(cut.Ticks + (audioLeads ? -amount : amount)));
        if (next.Ticks <= outgoing.Start.Ticks || next.Ticks >= incoming.End.Ticks)
            throw new ArgumentException("The offset would consume a neighbouring audio clip.");
        var delta = new SequenceTime(next.Ticks - cut.Ticks);
        var outgoingEnd = outgoing.SourceOut.Shift(delta);
        var incomingStart = incoming.SourceIn.Shift(delta);
        if (audioLeads) CheckHandle(incoming, incomingStart, true);
        else CheckHandle(outgoing, outgoingEnd, false);
        var left = new TrimPlan(outgoing.Id, EngineJson.Exact(outgoing.Start.Ticks), Position(outgoing.SourceIn), Position(outgoingEnd));
        var right = new TrimPlan(incoming.Id, EngineJson.Exact(next.Ticks), Position(incomingStart), Position(incoming.SourceOut));
        Add(audioLeads ? [left, right] : [right, left]);
        evidence = new { pictureCut = cut.Ticks, audioCut = next.Ticks, track = AgentHandles.For("track", track.Id) };
    }

    private void CheckHandle(TimelineClip clip, SourcePosition point, bool leading)
    {
        var stream = staged.Query().Assets.Single(a => a.Id == clip.AssetId).Streams.Single(s => s.Index == clip.StreamIndex);
        var boundary = leading ? stream.Start : stream.End;
        if (boundary is not { } known || (leading ? point.CompareTo(new(known)) < 0 : point.CompareTo(new(known)) > 0))
            throw new ArgumentException("The source does not have a known handle for that split-edit offset.");
    }

    public void Broll(EditorialSettings settings)
    {
        var track = Track(settings, TrackKind.Video);
        var asset = ResolveAsset(settings);
        var stream = asset.Streams.SingleOrDefault(s => s.Kind == TrackKind.Video && (settings.Stream is null || settings.Stream == s.Index))
            ?? throw new ArgumentException("Select a unique video source stream.");
        var start = At(settings.Start);
        var end = At(settings.End);
        if (end.Ticks <= start.Ticks || track.Clips.Any(c => c.Start.Ticks < end.Ticks && c.End.Ticks > start.Ticks))
            throw new ArgumentException("Place B-roll into a clear, positive interval on an overlay track.");
        if (!Sequence.Tracks.Any(t => t.Kind == TrackKind.Video && t.Order < track.Order &&
            t.Clips.Any(c => c.Start.Ticks < end.Ticks && c.End.Ticks > start.Ticks)))
            throw new ArgumentException("The selected track is not above primary video in the requested interval.");
        if (stream.Start is not { } origin || stream.End is not { } last)
            throw new ArgumentException("B-roll needs known source bounds.");
        var sourceIn = new SourcePosition(origin).Shift(AgentTime.Position(settings.SourceStart, stream.FrameRate, source: true));
        var sourceOut = sourceIn.Shift(new(end.Ticks - start.Ticks));
        if (sourceIn.CompareTo(new(origin)) < 0 || sourceOut.CompareTo(new(last)) > 0)
            throw new ArgumentException("B-roll source range extends outside the stream.");
        var transform = Fit(settings.Fit, stream, Sequence.Video, settings.FocalX, settings.FocalY);
        Pin(new(asset.Id, AnalysisCache.Fingerprint(asset.SourcePath)));
        var clip = new ClipDto(Identity("broll", track.Id, asset.Id, start.Ticks), asset.Id, stream.Index,
            EngineJson.Exact(start.Ticks), Position(sourceIn), Position(sourceOut), new ClipProperties(asset.Name, Video: transform));
        Add([new InsertPlan(track.Id, clip)]);
    }

    private static VideoEffects Fit(string mode, AssetStream source, VideoFormat target, double x, double y)
    {
        if (mode is not ("fit" or "fill") || !Unit(x) || !Unit(y)) throw new ArgumentException("Use fit/fill and focal coordinates in 0..1.");
        if (mode == "fit" || source.Width is not > 0 || source.Height is not > 0) return new();
        double sourceAspect = (double)source.Width.Value / source.Height.Value;
        double targetAspect = (double)target.Width / target.Height;
        double horizontal = Math.Max(0, 1 - targetAspect / sourceAspect);
        double vertical = Math.Max(0, 1 - sourceAspect / targetAspect);
        return new(CropLeft: horizontal * x, CropRight: horizontal * (1 - x),
            CropTop: vertical * y, CropBottom: vertical * (1 - y));
    }

    public void Duck(EditorialSettings settings)
    {
        var background = Track(settings, TrackKind.Audio);
        var artifact = Artifact(Require(settings.Audio, "audio"));
        if (artifact.Kind != "audio" || settings.AttenuationDb is < -60 or > 0 || !double.IsFinite(settings.AttenuationDb) ||
            new[] { settings.AttackMs, settings.HoldMs, settings.ReleaseMs }.Any(value => value is < 0 or > 10000))
            throw new ArgumentException("Ducking uses a complete audio artifact, attenuation −60..0 dB, and times of 0..10000 ms.");
        var speech = Sequence.Tracks.Where(t => t.Kind == TrackKind.Audio && t.Id != background.Id &&
            (settings.SpeechRole is null || t.Role == settings.SpeechRole) &&
            t.Clips.Any(c => c.AssetId == artifact.AssetId && c.StreamIndex == artifact.StreamIndex)).ToArray();
        if (speech.Length != 1) throw new ArgumentException("Resolve one speech track for the audio artifact.");
        var clips = speech[0].Clips.Where(c => c.AssetId == artifact.AssetId && c.StreamIndex == artifact.StreamIndex).ToArray();
        if (clips.Any(c => c.Retime is not null)) throw new ArgumentException("Ducking requires original-speed speech clips.");
        if (Sequence.Creator?.Audio.FirstOrDefault(a => a.TrackId == background.Id) is { Envelope.IsEmpty: false })
            throw new ArgumentException("The background track already has a manual gain envelope.");
        var spans = clips.Select(c => new RetainedSegment(c.Id, AnalysisTime.Normalize(c.SourceIn, artifact.ContainerStart),
            AnalysisTime.Normalize(c.SourceOut, artifact.ContainerStart), c.Start)).ToImmutableArray();
        var points = CreatorPlanning.Ducking(artifact, spans, settings.AttenuationDb, settings.AttackMs, settings.ReleaseMs, settings.HoldMs);
        Pin(new(artifact.AssetId, artifact.Fingerprint));
        Add([new TrackGainPlan(background.Id, Envelope: points)]);
        evidence = new { activityIntervals = artifact.Activity.Count(x => x.Category == "active"), envelopePoints = points.Length };
    }

    public void Short(EditorialSettings settings, int width, int height, double captionY)
    {
        var start = At(settings.Start);
        var end = At(settings.End);
        if (end.Ticks <= start.Ticks || end.Ticks > Sequence.Duration.Ticks) throw new ArgumentException("Choose an existing sequence range.");
        if (settings.Transcript is { } transcript)
        {
            var generated = staged.CreateCaptionsAsync(SequenceId, transcript, cancellation).GetAwaiter().GetResult();
            Add(generated.Plan);
        }
        var creator = CurrentCreator();
        int outputWidth = settings.Width ?? width;
        int outputHeight = settings.Height ?? height;
        var framing = settings.Observations is { } subjectKey
            ? FrameSubject(settings, subjectKey, start, end, outputWidth, outputHeight) : null;
        var profile = new OutputProfile(Identity("short", start.Ticks, end.Ticks), settings.Text ?? "vertical-short",
            outputWidth, outputHeight, Sequence.Video.FrameRate, settings.Fit, settings.FocalX,
            settings.FocalY, settings.CaptionY ?? captionY, start, end, framing);
        Add([new CreatorPlan(creator with { Profiles = [.. creator.Profiles.Where(p => p.Id != profile.Id), profile] })]);
    }

    private ImmutableArray<FramingPoint>? FrameSubject(EditorialSettings settings, string key, SequenceTime start, SequenceTime end,
        int outputWidth, int outputHeight)
    {
        var artifact = Artifact(key);
        if (artifact.Kind != "subjects") throw new ArgumentException("Reframing requires a subject-observation artifact.");
        if (settings.Fit == "fit") throw new ArgumentException("Subject tracking requires a fill or center crop.");
        var stream = staged.Query().Assets.Single(a => a.Id == artifact.AssetId).Streams.Single(s => s.Index == artifact.StreamIndex);
        if (stream.Width is not > 0 || stream.Height is not > 0 || outputWidth <= 0 || outputHeight <= 0)
            throw new ArgumentException("Subject tracking requires known positive source and output dimensions.");
        double fill = Math.Max((double)outputWidth / stream.Width.Value, (double)outputHeight / stream.Height.Value);
        double scaledWidth = stream.Width.Value * fill;
        double scaledHeight = stream.Height.Value * fill;
        var track = Sequence.Tracks.Where(t => t.Kind == TrackKind.Video && (settings.TrackRole is null || t.Role == settings.TrackRole))
            .Where(t => t.Clips.Any(c => c.AssetId == artifact.AssetId && c.StreamIndex == artifact.StreamIndex)).ToArray();
        if (track.Length != 1) throw new ArgumentException("Resolve one picture role for these subject observations.");
        var clips = track[0].Clips.Where(c => c.AssetId == artifact.AssetId && c.StreamIndex == artifact.StreamIndex
            && c.Retime is null && c.Start.Ticks < end.Ticks && c.End.Ticks > start.Ticks).ToArray();
        var rows = (artifact.Observations ?? []).Where(o => o.Box is not null && o.Confidence is >= .60 && o.Track is not null).ToArray();
        var target = settings.Subject ?? rows.GroupBy(o => o.Track).OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault()?.Key;
        var samples = new List<SubjectSample>();
        foreach (var observation in rows.Where(o => o.Track == target))
        {
            foreach (var clip in clips)
            {
                if (observation.Range.Start.CompareTo(clip.SourceIn) < 0 || observation.Range.Start.CompareTo(clip.SourceOut) >= 0) continue;
                var at = clip.MapToTimeline(observation.Range.Start);
                var box = observation.Box!;
                if (at.Ticks >= start.Ticks && at.Ticks < end.Ticks)
                    samples.Add(new(at, CropPosition(box.X + box.Width / 2, scaledWidth, outputWidth),
                        CropPosition(box.Y + box.Height / 2, scaledHeight, outputHeight)));
            }
        }
        Pin(new(artifact.AssetId, artifact.Fingerprint));
        if (samples.Count == 0)
        {
            diagnostics.Add("No confident subject samples in the selected interval. Manual focal position retained.");
            return null;
        }
        var result = SubjectFraming.Build(start, end, samples, settings.FocalX, settings.FocalY);
        evidence = new { observations = key, subject = target, samples = samples.Count, keys = result.Length };
        return result;

        static double CropPosition(double center, double scaled, double viewport) =>
            scaled <= viewport + .01 ? .5 : Math.Clamp((center * scaled - viewport / 2) / (scaled - viewport), 0, 1);
    }

    public void Layout(EditorialSettings settings, bool splitScreen)
    {
        var foreground = FindVideo(Require(settings.Clip, "clip"));
        if (foreground.Clip.Properties.Video?.Transform is not null)
            throw new ArgumentException("The selected picture already has a manual transform.");
        if (splitScreen)
        {
            var other = FindVideo(Require(settings.OtherClip, "otherClip"));
            if (other.Track.Id == foreground.Track.Id || !Overlaps(other.Clip, foreground.Clip) || other.Clip.Properties.Video?.Transform is not null)
                throw new ArgumentException("Split-screen needs overlapping clips on separate tracks with no manual transform.");
            Add([Placement(foreground.Clip, .25, .5, .49), Placement(other.Clip, .75, .5, .49)]);
            return;
        }
        if (!Sequence.Tracks.Where(t => t.Kind == TrackKind.Video && t.Order < foreground.Track.Order)
            .SelectMany(t => t.Clips).Any(c => Overlaps(c, foreground.Clip)))
            throw new ArgumentException("The facecam must be above an overlapping picture track.");
        double x = settings.FocalX == .5 ? .79 : settings.FocalX;
        double y = settings.FocalY == .5 ? .27 : settings.FocalY;
        if (x is < .20 or > .80 || y is < .20 or > .80) throw new ArgumentException("Keep the facecam within the 20–80% safe placement range.");
        Add([Placement(foreground.Clip, x, y, .33)]);

        static bool Overlaps(TimelineClip left, TimelineClip right) => left.Start.Ticks < right.End.Ticks && right.Start.Ticks < left.End.Ticks;
        static TransformPlan Placement(TimelineClip clip, double x, double y, double size) =>
            new("clip", clip.Id, new([new(0, x, y, size, Interpolation: "hold")]));
    }

    private (TimelineTrack Track, TimelineClip Clip) FindVideo(string handle)
    {
        var items = Sequence.Tracks.Where(t => t.Kind == TrackKind.Video).SelectMany(t => t.Clips.Where(c => c.Title is null)
            .Select(c => (Track: t, Clip: c))).ToArray();
        var id = AgentHandles.Resolve(handle, "clip", items.Select(item => item.Clip.Id));
        return items.Single(item => item.Clip.Id == id);
    }

    public void Creator(EditorialSettings settings, bool requireRanges)
    {
        var ranges = ReadRanges(settings.Ranges, Sequence.Video.FrameRate);
        var required = ReadRanges(settings.RequiredRanges, Sequence.Video.FrameRate);
        var excluded = ReadRanges(settings.ExcludedRanges, Sequence.Video.FrameRate);
        if (requireRanges && ranges.IsEmpty && required.IsEmpty) throw new ArgumentException("Supply ranked or required highlight ranges.");
        var request = new CreatorRequest(SequenceId, Require(settings.Transcript, "transcript"), Require(settings.Audio, "audio"),
            settings.Scenes, EngineJson.Exact(PositiveDuration(settings.Target)), EngineJson.Exact(PositiveDuration(settings.Minimum)),
            EngineJson.Exact(PositiveDuration(settings.Maximum)), EngineJson.Exact(PositiveDuration(settings.ShortDuration)), settings.Shorts,
            settings.Width ?? Sequence.Video.Width, settings.Height ?? Sequence.Video.Height, settings.Topics,
            SourceOrder: settings.SourceOrder, Normalize: settings.Normalize, MusicStreamIndices: settings.MusicStreams,
            MinimumSeparationTicks: EngineJson.Exact(AgentTime.Duration(settings.MinimumSeparation, Sequence.Video.FrameRate).Ticks));
        if (settings.MusicStreams is null && settings.TrackRole is { } role)
            request = request with { MusicStreamIndices = [.. Track(settings with { Track = null, TrackRole = role }, TrackKind.Audio)
                .Clips.Select(c => c.StreamIndex).Distinct()] };
        var selection = ranges.IsEmpty && required.IsEmpty && excluded.IsEmpty && !settings.DiversifyTopics
            ? null : new CreatorSelection(ranges, required, excluded, settings.DiversifyTopics);
        var proposal = staged.CreateCreatorAsync(request, cancellation, selection: selection).GetAwaiter().GetResult();
        Add(proposal.Plan);
        diagnostics.AddRange(proposal.Diagnostics.Take(8));
        evidence = new { proposal.EstimatedDuration, proposal.Outputs, candidateCount = proposal.Candidates.Length,
            selected = proposal.Segments.Take(32), candidates = proposal.Candidates.Take(24), proposal.Loudness };
    }

    public void Caption(EditorialSettings settings, int size, double y, long color, long highlight)
    {
        var creator = CurrentCreator();
        if (creator.Words.IsEmpty && settings.Transcript is { } transcript)
        {
            var generated = staged.CreateCaptionsAsync(SequenceId, transcript, cancellation).GetAwaiter().GetResult();
            Add(generated.Plan);
            creator = CurrentCreator();
        }
        if (creator.Words.IsEmpty) throw new ArgumentException("Supply a transcript or create captions before selecting a caption style.");
        var style = new CaptionStyle(settings.CaptionFont ?? creator.CaptionStyle.Font, settings.CaptionSize ?? size,
            settings.CaptionColor ?? checked((uint)color), settings.CaptionHighlight ?? checked((uint)highlight), settings.CaptionY ?? y,
            settings.CaptionWordsPerLine ?? creator.CaptionStyle.WordsPerLine, settings.CaptionBackdrop ?? creator.CaptionStyle.Backdrop,
            settings.CaptionFadeMs ?? creator.CaptionStyle.FadeMs);
        Add([new CreatorPlan(creator with { CaptionStyle = style })]);
    }

    public void Title(EditorialSettings settings, string style, string motion, double y, double size, long color, long outline)
    {
        var start = At(settings.Start);
        var end = At(settings.End);
        long animation = Math.Min((end.Ticks - start.Ticks) / 4, 63_504_000_000);
        var track = settings.Track is null && settings.TrackRole is null ? null : (Guid?)Track(settings, TrackKind.Video).Id;
        Add([new TitlePlan(Identity("title", start.Ticks, end.Ticks, style), Require(settings.Text, "text"),
            start.Ticks, end.Ticks, motion, animation, animation, settings.FocalX, y, size,
            checked((uint)color), checked((uint)outline), style, settings.Subtitle ?? "", track)]);
    }

    private CreatorSettings CurrentCreator() => Sequence.Creator ?? new(1, [], new(), [], []);
    private SequenceTime At(string? time) => AgentTime.Position(Require(time, "time"), sequence: Sequence);
    private long PositiveDuration(string time)
    {
        string normalized = time.EndsWith('f') && time.Length > 1 ? "f" + time[..^1] : time;
        long ticks = AgentTime.Duration(normalized, Sequence.Video.FrameRate).Ticks;
        return ticks > 0 ? ticks : throw new ArgumentException("Use a positive duration.");
    }
    private static bool Unit(double number) => double.IsFinite(number) && number >= 0 && number <= 1;
    private AnalysisArtifact Artifact(string key) => staged.Analysis.ResultAsync(key, cancellation).GetAwaiter().GetResult();
    private Guid Identity(string name, params object[] parts) =>
        CreatorPlanning.Id($"editorial-v1|{basis.Document.Id}|{basis.Revision}|{SequenceId}|{operations.Count}|{name}|{string.Join("|", parts)}");
    private static SourcePositionDto Position(SourcePosition p) =>
        new(EngineJson.Exact(p.Timestamp.Value), p.Timestamp.Numerator, p.Timestamp.Denominator, EngineJson.Exact(p.Offset.Ticks));
    private static string Require(string? text, string field) => string.IsNullOrWhiteSpace(text) ? throw new ArgumentException($"Supply {field}.") : text;
    private static bool IsAmbiguousFiller(string word) => word.Trim().ToLowerInvariant() is "like" or "well" or "so" or "right" or "you know" or "i mean";
    private static void ValidateTerms(ImmutableArray<string> terms)
    {
        if (terms.Length > 32 || terms.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 128))
            throw new ArgumentException("Use at most 32 nonempty terms of up to 128 characters.");
    }

    private TimelineTrack Track(EditorialSettings settings, TrackKind kind)
    {
        var tracks = Sequence.Tracks.Where(t => t.Kind == kind).ToArray();
        if (settings.Track is { } handle)
            return tracks.Single(t => t.Id == AgentHandles.Resolve(handle, "track", tracks.Select(a => a.Id)));
        var matches = tracks.Where(t => settings.TrackRole is null || t.Role == settings.TrackRole).ToArray();
        if (matches.Length != 1) throw new ArgumentException("Select a unique track handle or role.");
        return matches[0];
    }

    private MediaAsset ResolveAsset(EditorialSettings settings)
    {
        var assets = staged.Query().Assets;
        if (settings.Asset is { } reference) return assets.Single(a => a.Id == AgentHandles.Resolve(reference, "asset", assets.Select(a => a.Id)));
        var matches = assets.Where(a => a.Role == settings.AssetRole && settings.AssetRole is not null).ToArray();
        if (matches.Length != 1) throw new ArgumentException("Select a unique asset handle or role.");
        return matches[0];
    }

    private static ImmutableArray<HighlightCandidate> ReadRanges(ImmutableArray<EditorialRange>? input, FrameRate rate)
    {
        if (input is not { } ranges) return [];
        if (ranges.Length > 128) throw new ArgumentException("Use at most 128 externally ranked source ranges.");
        return [.. ranges.Select(r =>
        {
            var start = AgentTime.Position(r.Start, rate, source: true);
            var end = AgentTime.Position(r.End, rate, source: true);
            if (start.Ticks < 0 || end.Ticks <= start.Ticks || !double.IsFinite(r.Score) || string.IsNullOrWhiteSpace(r.Reason)
                || r.Reason.Length > 256 || r.Topic is { Length: > 128 })
                throw new ArgumentException("Invalid editorial range, score or provenance.");
            return new HighlightCandidate(CreatorPlanning.Id($"editorial-range|{start.Ticks}|{end.Ticks}"),
                start, end, r.Score, ["External editorial judgment: " + r.Reason], r.Topic);
        }).OrderByDescending(r => r.Score).ThenBy(r => r.Start.Ticks).DistinctBy(r => r.Id)];
    }

    private void Pin(SourcePrecondition source)
    {
        if (sources.TryGetValue(source.AssetId, out var earlier) && earlier.Fingerprint != source.Fingerprint)
            throw new ArgumentException("The source changed during editorial composition.");
        sources[source.AssetId] = source;
    }

    private void Add(EditPlan plan)
    {
        foreach (var source in plan.SourcePreconditions ?? []) Pin(source);
        Add(plan.Operations);
    }

    private void Add(ImmutableArray<PlanOperation> stage)
    {
        cancellation.ThrowIfCancellationRequested();
        if (stage.IsEmpty) return;
        if (operations.Count + stage.Length > 500) throw new ArgumentException("The composed edit exceeds the 500-operation budget.");
        var current = staged.Session.Snapshot;
        var plan = new EditPlan(1, current.Document.Id, EngineJson.Exact(current.Revision), SequenceId, stage);
        var result = staged.Plan(plan, true, cancellation);
        if (!result.Valid) throw new InvalidOperationException(string.Join(" | ", result.Diagnostics));
        if (stage is [CreatorPlan replacement] && operations.LastOrDefault() is CreatorPlan)
            operations[^1] = replacement;
        else
            operations.AddRange(stage);
    }

    public void Dispose() => staged.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
