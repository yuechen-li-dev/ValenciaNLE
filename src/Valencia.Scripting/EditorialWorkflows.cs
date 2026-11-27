using System.Collections.Immutable;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Copeland.TS.Tson;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Analysis;

namespace Valencia.Scripting;

/// <summary>Typed CLR bridge for the packaged Copeland policy. Each method plans against the current private stage.</summary>
public sealed class EditorialInputs
{
    private readonly EditorialPlanner planner;
    private readonly EditorialSettings settings;

    internal EditorialInputs(EditorialPlanner planner, EditorialSettings settings)
    {
        this.planner = planner;
        this.settings = settings;
    }

    public void Tighten() => planner.Tighten(settings.Audio ?? throw new ArgumentException("Supply audio."),
        settings.MinimumPauseMs, settings.PaddingMs, settings.MergeDistanceMs);
    public void Fillers(string[] vocabulary) => planner.Fillers(settings, vocabulary);
    public void Topic() => planner.Topic(settings);
    public void SplitEdit(bool audioLeads) => planner.SplitEdit(settings, audioLeads);
    public void Broll() => planner.Broll(settings);
    public void Duck() => planner.Duck(settings);
    public void Short(int width, int height, double baseline) => planner.Short(settings, width, height, baseline);
    public void Layout(bool splitScreen) => planner.Layout(settings, splitScreen);
    public void Creator(bool requireRanges) => planner.Creator(settings, requireRanges);
    public void Caption(int size, double baseline, long color, long highlight) => planner.Caption(settings, size, baseline, color, highlight);
    public void Title(string style, string motion, double y, double size, long color, long outline) =>
        planner.Title(settings, style, motion, y, size, color, outline);
}

public sealed record EditorialWorkflowInfo(string Id, string EntryPoint, string Description, string[] Analysis,
    string[] Required, string[] Options, object Example, bool Template = false)
{
    public int Version => 1;
    public string InputType => "EditorialSettings";
    public string OutputType => "EditorialPlanResult";
    public string Mutation => "prepare a candidate; explicit agent_plan commit";
}

public sealed record EditorialStep(string Workflow, EditorialSettings Settings);

public static class EditorialWorkflows
{
    public const string Method = "editorial_workflow";
    private static readonly CopelandScriptHost Host = new();
    private static readonly Lazy<string> Source = new(() =>
    {
        using var stream = typeof(EditorialWorkflows).Assembly.GetManifestResourceStream("Valencia.Editorial.Workflows.ts")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static ImmutableArray<EditorialWorkflowInfo> Catalog { get; } =
    [
        new("tighten-pauses", "tightenPauses", "Remove long pauses from aligned tracks.", ["audio"], ["audio"],
            ["minimumPauseMs", "paddingMs", "mergeDistanceMs"], new { audio = "<audio key>", minimumPauseMs = 800 }),
        new("remove-fillers", "removeFillers", "Review vocabulary matches and optionally cut reviewed word IDs.", ["transcript"], ["transcript"],
            ["vocabulary", "wordIds", "occurrenceIds"], new { transcript = "<transcript key>", vocabulary = new[] {"um", "well"} }),
        new("topic-cut", "topicCut", "Retain or remove timed phrases or externally marked ranges with context.", ["transcript"], ["transcript"],
            ["topics", "ranges", "mode", "contextWords", "paddingMs", "occurrenceIds"], new { transcript = "<transcript key>", topics = new[] {"backup plan"} }),
        new("j-cut", "jCut", "Lead an abutting picture cut with audio.", [], ["start"],
            ["track", "trackRole", "offset"], new { start = "12s", trackRole = "mic", offset = "f6" }),
        new("l-cut", "lCut", "Carry outgoing audio past an abutting picture cut.", [], ["start"],
            ["track", "trackRole", "offset"], new { start = "12s", trackRole = "mic", offset = "f6" }),
        new("insert-broll", "insertBroll", "Overlay a source range on a clear video track while preserving primary audio.", [], ["start", "end"],
            ["track", "trackRole", "asset", "assetRole", "stream", "sourceStart", "fit", "focalX", "focalY"],
            new { trackRole = "b-roll", assetRole = "detail", start = "4s", end = "7s", sourceStart = "10s" }),
        new("duck-speech", "duckSpeech", "Build a background gain envelope from timed speech-stream activity.", ["audio"], ["audio"],
            ["track", "trackRole", "speechRole", "attenuationDb", "attackMs", "holdMs", "releaseMs"],
            new { audio = "<audio key>", trackRole = "music", speechRole = "mic", attenuationDb = -14 }),
        new("make-short", "makeShort", "Frame a range as a vertical output using the existing sequence mix and optional captions.", [], ["start", "end"],
            ["transcript", "width", "height", "focalX", "focalY", "fit", "captionY", "text", "observations", "subject", "trackRole"], new { start = "10s", end = "30s", focalX = .65 }, true),
        new("highlight-reel", "highlightReel", "Assemble ranked source moments in source or montage order.", ["transcript", "audio"], ["transcript", "audio", "ranges"],
            ["target", "minimum", "maximum", "shorts", "shortDuration", "musicStreams", "trackRole", "normalize", "sourceOrder", "minimumSeparation",
                "requiredRanges", "excludedRanges", "diversifyTopics"],
            new { transcript = "<transcript key>", audio = "<audio key>", ranges = new[] { new { start = "10s", end = "30s", score = .9, reason = "Reviewed explanation" } } }),
        new("creator", "creatorWorkflow", "Build a ranked landscape proposal and vertical profiles from existing source analysis.", ["transcript", "audio"], ["transcript", "audio"],
            ["scenes", "topics", "ranges", "target", "minimum", "maximum", "shorts", "shortDuration", "width", "height", "musicStreams", "trackRole", "normalize", "sourceOrder", "minimumSeparation",
                "requiredRanges", "excludedRanges", "diversifyTopics"],
            new { transcript = "<transcript key>", audio = "<audio key>", topics = new[] { "important announcement" }, target = "10m", shorts = 3 }),
        new("creator-title", "creatorTitle", "Add an original Valencia title card.", [], ["start", "end", "text"],
            ["subtitle", "track", "trackRole", "focalX"], new { start = "0s", end = "2s", text = "Chapter one" }, true),
        new("lower-third", "lowerThird", "Add an original Valencia lower third.", [], ["start", "end", "text"],
            ["subtitle", "track", "trackRole", "focalX"], new { start = "5s", end = "8s", text = "Dana", subtitle = "Host" }, true),
        new("word-highlight", "wordHighlight", "Apply a word-highlight style to source-aligned captions.", [], [],
            ["transcript", "captionFont", "captionSize", "captionColor", "captionHighlight", "captionY"], new { captionSize = 48 }, true),
        new("tutorial-short", "tutorialShort", "Compose the vertical and word-highlight templates into one plan.", [], ["start", "end"],
            ["transcript", "width", "height", "focalX", "focalY", "captionFont", "captionSize", "captionY"], new { start = "5s", end = "20s", transcript = "<transcript key>" }, true),
        new("facecam-over-gameplay", "facecam", "Place a video clip on a safe-area overlay over the primary picture.", [], ["clip"],
            ["focalX", "focalY"], new { clip = "<facecam clip>", focalX = .78, focalY = .24 }, true),
        new("split-screen", "splitScreen", "Place two overlapping picture clips side by side.", [], ["clip", "otherClip"],
            [], new { clip = "<left clip>", otherClip = "<right clip>" }, true)
    ];

    public static EditorialWorkflowInfo Info(string id) => Catalog.FirstOrDefault(entry => entry.Id == id)
        ?? throw new ArgumentException($"Unknown editorial workflow '{id}'.");

    public static EditorialSettings ReadSettings(JsonElement settings)
    {
        var issues = TypeProfileCompiler.Validate(EditPlanSchema.Catalog, "EditorialSettings", settings);
        if (issues.Count != 0) throw new ArgumentException(string.Join("\n", issues.Take(8)));
        return EngineJson.Read<EditorialSettings>(settings.GetRawText());
    }

    public static EditorialSettings ReadSettings(string text, string path)
    {
        if (text.Length > 1_048_576) throw new ArgumentException("Editorial settings exceed 1 MiB.");
        if (path.EndsWith(".obj.ts", StringComparison.OrdinalIgnoreCase))
            return ReadSettings(TypeProfileCompiler.ReadTypedValue(text, path, EditPlanSchema.Catalog, "EditorialSettings"));
        using var document = JsonDocument.Parse(text);
        return ReadSettings(document.RootElement);
    }

    public static Task<EditorialPlanResult> BuildAsync(EditingEngine engine, ProjectSnapshot snapshot, string id,
        EditorialSettings settings, CancellationToken cancellation = default) =>
        BuildAsync(engine, snapshot, [new(id, settings)], cancellation);

    public static async Task<EditorialPlanResult> BuildAsync(EditingEngine engine, ProjectSnapshot snapshot,
        IReadOnlyList<EditorialStep> steps, CancellationToken cancellation = default)
    {
        if (steps.Count is < 1 or > 8) throw new ArgumentException("Use 1–8 editorial workflow steps.");
        var sequence = steps.Select(step => step.Settings.Sequence).FirstOrDefault(x => x is not null);
        if (steps.Any(step => step.Settings.Sequence is not null && step.Settings.Sequence != sequence))
            throw new ArgumentException("Use the same sequence reference throughout a composition.");
        using var planner = new EditorialPlanner(engine, snapshot, steps[0].Settings with { Sequence = sequence }, cancellation);
        var evidence = new List<object>();
        foreach (var step in steps)
        {
            var workflow = Info(step.Workflow);
            var result = await Host.RunEditorialAsync(Source.Value, workflow.EntryPoint, engine, new(planner, step.Settings), cancellation)
                .ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (!result.Success)
                throw new ArgumentException(string.Join(" | ", result.Diagnostics.Select(d => $"{d.Code} {d.Line}:{d.Column} {d.Message}")));
            if (steps.Count > 1) evidence.Add(new { workflow = step.Workflow, observations = planner.TakeEvidence() });
        }
        var prepared = planner.Finish();
        return steps.Count == 1 ? prepared : prepared with { Evidence = evidence };
    }

    public static async Task<AgentReply> InvokeAsync(EditingEngine engine, JsonElement request, CancellationToken cancellation = default)
    {
        var snapshot = engine.Session.Snapshot;
        try
        {
            if (request.GetRawText().Length > 1_048_576) throw new ArgumentException("Editorial request exceeds 1 MiB.");
            var errors = TypeProfileCompiler.Validate(EditPlanSchema.Catalog, "EditorialInvocation", request);
            if (errors.Count != 0) throw new ArgumentException(string.Join("\n", errors.Take(8)));
            string action = request.GetProperty("action").GetString()!;
            if (action == "list") return new(AgentContracts.Version, snapshot.Revision, Catalog);
            if (action == "info") return new(AgentContracts.Version, snapshot.Revision, Info(request.GetProperty("workflow").GetString()!));
            if (action == "providers") return new(AgentContracts.Version, snapshot.Revision, Providers());
            if (!request.TryGetProperty("projectId", out var project) || !Guid.TryParse(project.GetString(), out Guid projectId) ||
                projectId != snapshot.Document.Id)
                return new(AgentContracts.Version, snapshot.Revision, Issue: new("WRONG_PROJECT", "Use the active project identity."));
            if (!request.TryGetProperty("revision", out var revision) || EngineJson.Int64(revision.GetString()!) != snapshot.Revision)
                return new(AgentContracts.Version, snapshot.Revision, Issue: new("STALE_REVISION", "Use the current project revision."));
            if (action == "submit-observations")
            {
                var batch = EngineJson.Read<EditorialObservationBatch>(request.GetProperty("observations").GetRawText());
                var stored = await engine.StoreEditorialObservationsAsync(snapshot, batch, cancellation).ConfigureAwait(false);
                return new(AgentContracts.Version, engine.Session.Snapshot.Revision, stored);
            }
            string workflow = action == "compose" ? "composition" : request.GetProperty("workflow").GetString()!;
            var steps = action == "compose"
                ? request.GetProperty("steps").EnumerateArray().Select(step => new EditorialStep(step.GetProperty("workflow").GetString()!,
                    ReadSettings(step.GetProperty("settings")))).ToArray()
                : [new EditorialStep(workflow, request.TryGetProperty("settings", out var input) ? ReadSettings(input) : new())];
            var result = await BuildAsync(engine, snapshot, steps, cancellation).ConfigureAwait(false);
            if (!ReferenceEquals(engine.Session.Snapshot, snapshot))
                return new(AgentContracts.Version, engine.Session.Snapshot.Revision, Issue: new("STALE_REVISION", "The project changed during planning."));
            if (result.Plan is not { } plan)
                return new(AgentContracts.Version, snapshot.Revision, new { result.Diagnostics, result.Evidence, changed = false });
            var prepare = JsonSerializer.SerializeToElement(new { action = "prepare", projectId, revision = snapshot.Revision,
                canonicalPlan = plan }, EngineJson.Options);
            var prepared = await engine.AgentAsync("agent_plan", prepare, cancellation).ConfigureAwait(false);
            if (!prepared.Ok) return prepared;
            bool includePlan = request.TryGetProperty("includePlan", out var include) && include.GetBoolean();
            string planSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(EngineJson.Write(plan))));
            return new(AgentContracts.Version, prepared.Revision, new { workflow, version = 1, preparation = prepared.Result,
                result.Diagnostics, result.Evidence, planSha256, plan = includePlan ? plan : null, changed = true });
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException or JsonException or IOException)
        {
            return new(AgentContracts.Version, engine.Session.Snapshot.Revision, Issue: new("EDITORIAL_REQUEST", error.Message));
        }
    }

    private static object Providers()
    {
        var configured = Environment.GetEnvironmentVariable(OcrProvider.EnvironmentKey);
        return new
        {
            transcriptTopics = new { mode = "local lexical", version = TranscriptTopics.Algorithm, requires = "aligned transcript" },
            ocr = new { mode = "optional Tesseract", configured = configured is not null && Path.IsPathFullyQualified(configured) && File.Exists(configured),
                setting = OcrProvider.EnvironmentKey },
            external = new { mode = "validated import", kinds = new[] { "topics", "visual", "speakers", "subjects", "beats", "annotations" } }
        };
    }
}
