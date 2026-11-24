using System.Text.Json;
using System.Text.Json.Nodes;
using Copeland.TS.Tson;
using Valencia.Core;
using Valencia.Engine;

namespace Valencia.Cli;

/// <summary>Optional, bounded MCP 2025-11-25 stdio adapter. No executable-script tool or server-initiated requests.</summary>
public sealed class StdioMcpServer(EditingEngine engine, bool allowObsControl = false)
{
    private const string ProtocolVersion = "2025-11-25";
    private bool initialized;
    private bool ready;
    private static readonly string[] ToolNames = ["project_info", "timeline_query", "validate_edit_plan", "apply_edit_plan", "run_export", "export_status", "cancel_export",
        "start_analysis", "analysis_status", "analysis_result", "cancel_analysis", "create_roughcut", "transcript_query", "create_textedit",
        "start_creator", "creator_status", "cancel_creator", "apply_creator", "caption_plan", "correct_transcript",
        "obs_connect", "obs_status", "obs_record", "obs_marker", "obs_import", "effects_list"];

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            string? line = await ReadBoundedLine(input, cancellation).ConfigureAwait(false);
            if (line is null) return;
            JsonElement? id = null;
            try
            {
                using var message = JsonDocument.Parse(line);
                var request = message.RootElement;
                if (request.TryGetProperty("id", out var requestId)) id = requestId.Clone();
                string method = request.GetProperty("method").GetString()!;
                if (request.GetProperty("jsonrpc").GetString() != "2.0") throw new ArgumentException("JSON-RPC 2.0 required.");
                if (method == "notifications/initialized") { ready = initialized; continue; }
                if (id is null) continue;
                object result;
                if (method == "initialize")
                {
                    string requested = request.GetProperty("params").GetProperty("protocolVersion").GetString()!;
                    if (requested != ProtocolVersion) throw new ArgumentException("Supported MCP protocol: " + ProtocolVersion);
                    initialized = true;
                    result = new { protocolVersion = ProtocolVersion, capabilities = new { tools = new { } }, serverInfo = new { name = "Valencia", version = "M2e" } };
                }
                else
                {
                    if (!ready) throw new ArgumentException("Initialize and notifications/initialized are required.");
                    result = method switch
                    {
                        "ping" => new { },
                        "tools/list" => new { tools = ToolNames.Select(name => new { name, description = "Valencia engine v1: " + name, inputSchema = InputSchema(name) }) },
                        "tools/call" => Call(request.GetProperty("params")),
                        _ => throw new ArgumentException("Unsupported MCP method.")
                    };
                }
                await output.WriteLineAsync(EngineJson.Write(new { jsonrpc = "2.0", id = id.Value, result })).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                string response = "{\"jsonrpc\":\"2.0\",\"id\":" + (id?.GetRawText() ?? "null")
                    + ",\"error\":{\"code\":-32602,\"message\":" + JsonSerializer.Serialize(error.Message) + "}}";
                await output.WriteLineAsync(response).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellation).ConfigureAwait(false);
        }
    }

    private object Call(JsonElement parameters)
    {
        string name = parameters.GetProperty("name").GetString()!;
        if (!ToolNames.Contains(name)) throw new ArgumentException("Unknown tool.");
        var arguments = parameters.GetProperty("arguments");
        object value;
        bool failed = false;
        try
        {
            if (Guid.Parse(arguments.GetProperty("projectId").GetString()!) != engine.Session.Snapshot.Document.Id)
                throw new ArgumentException("Project identity mismatch.");
            value = name switch
            {
                "project_info" => BoundedQuery(ProjectInfo(arguments)),
                "timeline_query" => BoundedQuery(Query(arguments)),
                "effects_list" => new { version = EffectCatalog.Version, effects = EffectCatalog.Effects },
                "validate_edit_plan" or "apply_edit_plan" => Plan(arguments, name == "apply_edit_plan"),
                "run_export" => new { jobId = StartExport(arguments) },
                "export_status" => engine.Exports.Status(Guid.Parse(arguments.GetProperty("jobId").GetString()!)),
                "cancel_export" => Cancel(arguments),
                "start_analysis" => new { jobId = engine.Analysis.Start(AnalysisContracts.Parse<AnalysisRequest>(arguments.GetProperty("request").GetRawText(), "AnalysisRequest")) },
                "analysis_status" => engine.Analysis.Status(Guid.Parse(arguments.GetProperty("jobId").GetString()!)),
                "cancel_analysis" => CancelAnalysis(arguments),
                "analysis_result" => AnalysisPage(arguments),
                "create_roughcut" => CreateRoughCut(arguments),
                "transcript_query" => TranscriptQuery(arguments),
                "create_textedit" => CreateTextEdit(arguments),
                "start_creator" => StartCreator(arguments),
                "creator_status" => CreatorStatus(arguments),
                "cancel_creator" => CancelCreator(arguments),
                "apply_creator" => ApplyCreator(arguments),
                "caption_plan" => Captions(arguments),
                "correct_transcript" => CorrectTranscript(arguments),
                "obs_connect" => ConnectObs(arguments),
                "obs_status" => ObsStatus(),
                "obs_record" => ControlObs(arguments),
                "obs_marker" => RequireObs().MarkAsync(arguments.GetProperty("note").GetString()!,
                    arguments.TryGetProperty("kind", out var markerKind) ? markerKind.GetString()! : "highlight").GetAwaiter().GetResult(),
                "obs_import" => ImportObs(arguments),
                _ => throw new ArgumentException("Unknown tool.")
            };
            failed = value is PlanResult { Valid: false } or RoughCutProposal { Valid: false };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            failed = true;
            value = new { apiVersion = 1, code = "TOOL_REJECTED", message = error.Message, revision = EngineJson.Exact(engine.Session.Snapshot.Revision) };
        }
        return new { content = new[] { new { type = "text", text = EngineJson.Write(value) } }, structuredContent = new { apiVersion = 1, result = value }, isError = failed };
    }
    private object StartCreator(JsonElement arguments)
    {
        long revision = EngineJson.Int64(arguments.GetProperty("revision").GetString()!);
        var request = AnalysisContracts.Parse<CreatorRequest>(arguments.GetProperty("request").GetRawText(), "CreatorRequest");
        return new { jobId = engine.CreatorJobs.Start(request, revision) };
    }
    private ObsWorkflow RequireObs() => engine.Obs ?? throw new InvalidOperationException("Connect OBS explicitly first.");
    private object ConnectObs(JsonElement arguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("OBS credential storage requires Windows.");
        var request = AnalysisContracts.Parse<ObsConnectRequest>(arguments.GetProperty("request").GetRawText(), "ObsConnectRequest");
        var stored = Valencia.Obs.ObsCredentials.Load();
        var options = request.Endpoint is { } endpoint ? new Valencia.Obs.ObsConnectionOptions(new Uri(endpoint))
            : stored?.Options ?? new(new Uri("ws://127.0.0.1:4455"));
        _ = engine.ConnectObsAsync(options, Environment.GetEnvironmentVariable("VALENCIA_OBS_PASSWORD") ?? stored?.Password,
            request.Manifest, request.RecordingRoot, allowObsControl).GetAwaiter().GetResult();
        return ObsStatus();
    }
    private object ObsStatus()
    {
        var obs = RequireObs();
        var snapshot = obs.Recording.Snapshot;
        return new { connection = obs.Recording.Client.Status, version = obs.Recording.Client.Version,
            recording = obs.Recording.Client.Status.State == Valencia.Obs.ObsConnectionState.Connected
                ? Valencia.Obs.ObsDiscoveryService.RecordStatusAsync(obs.Recording.Client).GetAwaiter().GetResult() : null,
            snapshot.SessionId, snapshot.State, snapshot.ContinuousObservation, markerCount = snapshot.Markers.Length,
            files = snapshot.Files, recentEvents = snapshot.Events.TakeLast(8), controlAuthorized = allowObsControl };
    }
    private object ControlObs(JsonElement arguments)
    {
        var obs = RequireObs();
        string action = arguments.GetProperty("action").GetString()!;
        var task = action switch
        {
            "start" => obs.StartAsync(), "stop" => obs.StopAsync(), "pause" => obs.PauseAsync(true), "resume" => obs.PauseAsync(false),
            _ => throw new ArgumentException("Supported recording actions: start, stop, pause, resume.")
        };
        task.GetAwaiter().GetResult();
        return ObsStatus();
    }
    private object ImportObs(JsonElement arguments)
    {
        long revision = EngineJson.Int64(arguments.GetProperty("revision").GetString()!);
        return RequireObs().ImportAsync(revision).GetAwaiter().GetResult();
    }
    private object CreatorStatus(JsonElement arguments)
    {
        var status = engine.CreatorJobs.Status(Guid.Parse(arguments.GetProperty("jobId").GetString()!));
        return new { status, proposal = status.PlanKey is { } key ? EditingEngine.CreatorSummary(engine.CreatorProposal(key)) : null };
    }
    private object CancelCreator(JsonElement arguments)
    {
        Guid id = Guid.Parse(arguments.GetProperty("jobId").GetString()!);
        engine.CreatorJobs.Cancel(id); return engine.CreatorJobs.Status(id);
    }
    private object ApplyCreator(JsonElement arguments)
    {
        string key = arguments.GetProperty("key").GetString()!;
        string revision = arguments.GetProperty("revision").GetString()!;
        if (revision != engine.CreatorProposal(key).Plan.ProjectRevision) throw new ArgumentException("Creator envelope revision differs from plan.");
        return engine.ApplyCreator(key);
    }
    private object Captions(JsonElement arguments)
    {
        if (EngineJson.Int64(arguments.GetProperty("revision").GetString()!) != engine.Session.Snapshot.Revision) throw new ArgumentException("Stale caption revision.");
        return EditingEngine.CreatorSummary(engine.CreateCaptionsAsync(Guid.Parse(arguments.GetProperty("sequenceId").GetString()!),
            arguments.GetProperty("key").GetString()!).GetAwaiter().GetResult());
    }
    private object CorrectTranscript(JsonElement arguments)
    {
        var request = AnalysisContracts.Parse<TranscriptCorrections>(arguments.GetProperty("request").GetRawText(), "TranscriptCorrections");
        var result = engine.CorrectTranscriptAsync(request.Key, request.Edits.ToDictionary(e => e.WordId, e => e.Text)).GetAwaiter().GetResult();
        return AnalysisContracts.Page(result, "words");
    }
    private object Query(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("sequenceId", out var sequence)) return engine.Query().Sequences;
        Guid id = Guid.Parse(sequence.GetString()!);
        if (arguments.TryGetProperty("atTicks", out var at)) return engine.Query().Evaluate(id, new(EngineJson.Int64(at.GetString()!)));
        if (arguments.TryGetProperty("rangeStart", out var start)) return engine.Query().Intersecting(id,
            new(EngineJson.Int64(start.GetString()!)), new(EngineJson.Int64(arguments.GetProperty("rangeEnd").GetString()!)));
        return engine.Query().Tracks(id);
    }
    private object ProjectInfo(JsonElement arguments)
    {
        var snapshot = engine.Session.Snapshot;
        if (!arguments.TryGetProperty("compact", out var compact) || !compact.GetBoolean())
            return new { apiVersion = 1, snapshot, missingMedia = engine.MissingMedia() };
        return new
        {
            apiVersion = 1, compact = true, snapshot.Document.Id, snapshot.Document.Name, snapshot.Revision,
            assetCount = snapshot.Document.Assets.Length, sequenceCount = snapshot.Document.Sequences.Length,
            assets = snapshot.Document.Assets.Take(128).Select(a => new { a.Id, a.Name, streams = a.Streams.Select(s => new { s.Index, s.Kind, s.Codec }) }),
            sequences = snapshot.Document.Sequences.Take(128).Select(s => new { s.Id, s.Name, s.Video, s.AudioSampleRate, s.Duration,
                trackCount = s.Tracks.Length, clipCount = s.Tracks.Sum(t => t.Clips.Length), captionWordCount = s.Creator?.Words.Length ?? 0 }),
            missingMedia = engine.MissingMedia().Take(128)
        };
    }
    private static JsonElement BoundedQuery(object value)
    {
        string json = EngineJson.Write(value);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 131072) throw new ArgumentException("Query exceeds the 128-KiB MCP response budget. Use project_info compact:true, a sequenceId, and a narrower time/range query. Full snapshots remain available through the trusted engine/CLI.");
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
    private object CancelAnalysis(JsonElement arguments)
    {
        var id = Guid.Parse(arguments.GetProperty("jobId").GetString()!);
        engine.Analysis.Cancel(id);
        return engine.Analysis.Status(id);
    }
    private object TranscriptQuery(JsonElement arguments)
    {
        var artifact = engine.Analysis.ResultAsync(arguments.GetProperty("key").GetString()!).GetAwaiter().GetResult();
        int offset = arguments.TryGetProperty("offset", out var value) ? value.GetInt32() : 0;
        int limit = arguments.TryGetProperty("limit", out value) ? value.GetInt32() : 64;
        if (arguments.TryGetProperty("rangeStart", out var begin))
        {
            var start = new Valencia.Core.Timeline.SourcePosition(artifact.ContainerStart).Shift(new(EngineJson.Int64(begin.GetString()!)));
            var end = new Valencia.Core.Timeline.SourcePosition(artifact.ContainerStart).Shift(new(EngineJson.Int64(arguments.GetProperty("rangeEnd").GetString()!)));
            return TranscriptQueries.Range(artifact, start, end, offset, limit);
        }
        return arguments.TryGetProperty("query", out var query)
            ? TranscriptQueries.Search(artifact, query.GetString()!, offset: offset, limit: limit)
            : AnalysisContracts.Page(artifact, "words", offset, limit);
    }
    private object CreateTextEdit(JsonElement arguments)
    {
        if (EngineJson.Int64(arguments.GetProperty("revision").GetString()!) != engine.Session.Snapshot.Revision) throw new ArgumentException("Stale revision.");
        var request = AnalysisContracts.Parse<TextEditRequest>(arguments.GetProperty("request").GetRawText(), "TextEditRequest");
        return engine.TextEditAsync(request).GetAwaiter().GetResult();
    }
    private object AnalysisPage(JsonElement arguments)
    {
        var artifact = engine.Analysis.ResultAsync(arguments.GetProperty("key").GetString()!).GetAwaiter().GetResult();
        return AnalysisContracts.Page(artifact,
            arguments.TryGetProperty("kind", out var kind) ? kind.GetString()! : "summary",
            arguments.TryGetProperty("offset", out var offset) ? offset.GetInt32() : 0,
            arguments.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 64,
            arguments.TryGetProperty("level", out var level) ? level.GetInt32() : 0);
    }
    private object CreateRoughCut(JsonElement arguments)
    {
        if (EngineJson.Int64(arguments.GetProperty("revision").GetString()!) != engine.Session.Snapshot.Revision)
            throw new ArgumentException("Stale revision.");
        var policy = arguments.TryGetProperty("policy", out var value)
            ? AnalysisContracts.Parse<RoughCutPolicy>(value.GetRawText(), "RoughCutPolicy") : new RoughCutPolicy();
        return engine.RoughCutAsync(Guid.Parse(arguments.GetProperty("sequenceId").GetString()!),
            arguments.GetProperty("key").GetString()!, policy).GetAwaiter().GetResult();
    }
    private PlanResult Plan(JsonElement arguments, bool commit)
    {
        var plan = EditPlanSchema.Parse(arguments.GetProperty("plan").GetRawText());
        if (plan.ProjectRevision != arguments.GetProperty("revision").GetString()) throw new ArgumentException("Envelope/plan revisions differ.");
        return engine.Plan(plan, commit);
    }
    private Guid StartExport(JsonElement arguments)
    {
        if (EngineJson.Int64(arguments.GetProperty("revision").GetString()!) != engine.Session.Snapshot.Revision)
            throw new ArgumentException("Stale revision.");
        return engine.Exports.Start(Guid.Parse(arguments.GetProperty("sequenceId").GetString()!), arguments.GetProperty("output").GetString()!,
            arguments.TryGetProperty("profileId", out var profile) ? profile.GetGuid() : null);
    }
    private object Cancel(JsonElement arguments)
    {
        Guid id = Guid.Parse(arguments.GetProperty("jobId").GetString()!);
        engine.Exports.Cancel(id);
        return engine.Exports.Status(id);
    }
    private static JsonObject InputSchema(string tool)
    {
        var properties = new JsonObject { ["projectId"] = new JsonObject { ["type"] = "string" } };
        var required = new JsonArray("projectId");
        void Field(string name, bool mandatory = false)
        {
            properties[name] = new JsonObject { ["type"] = "string" };
            if (mandatory) required.Add(name);
        }
        if (tool == "timeline_query") { Field("sequenceId"); Field("atTicks"); Field("rangeStart"); Field("rangeEnd"); }
        if (tool == "project_info") properties["compact"] = new JsonObject { ["type"] = "boolean" };
        if (tool == "obs_connect")
        {
            properties["request"] = new JsonObject { ["$ref"] = "#/$defs/ObsConnectRequest" };
            required.Add("request");
        }
        if (tool == "obs_record") Field("action", true);
        if (tool == "obs_marker") { Field("note", true); Field("kind"); }
        if (tool == "obs_import") Field("revision", true);
        if (tool is "validate_edit_plan" or "apply_edit_plan")
        {
            Field("revision", true);
            properties["plan"] = new JsonObject { ["$ref"] = "#/$defs/EditPlan" };
            required.Add("plan");
        }
        if (tool == "run_export") { Field("sequenceId", true); Field("revision", true); Field("output", true); Field("profileId"); }
        if (tool is "export_status" or "cancel_export") Field("jobId", true);
        if (tool is "analysis_status" or "cancel_analysis") Field("jobId", true);
        if (tool == "start_analysis")
        {
            properties["request"] = new JsonObject { ["$ref"] = "#/$defs/AnalysisRequest" };
            required.Add("request");
        }
        if (tool == "analysis_result")
        {
            Field("key", true);
            Field("kind");
            foreach (string name in new[] { "offset", "limit", "level" }) properties[name] = new JsonObject { ["type"] = "integer" };
        }
        if (tool == "create_roughcut")
        {
            Field("key", true); Field("sequenceId", true); Field("revision", true);
            properties["policy"] = new JsonObject { ["$ref"] = "#/$defs/RoughCutPolicy" };
        }
        if (tool == "transcript_query")
        {
            Field("key", true); Field("query");
            Field("rangeStart"); Field("rangeEnd");
            properties["offset"] = new JsonObject { ["type"] = "integer" };
            properties["limit"] = new JsonObject { ["type"] = "integer" };
        }
        if (tool == "create_textedit")
        {
            Field("revision", true);
            properties["request"] = new JsonObject { ["$ref"] = "#/$defs/TextEditRequest" };
            required.Add("request");
        }
        if (tool == "start_creator")
        {
            Field("revision", true); properties["request"] = new JsonObject { ["$ref"] = "#/$defs/CreatorRequest" }; required.Add("request");
        }
        if (tool is "creator_status" or "cancel_creator") Field("jobId", true);
        if (tool == "apply_creator") { Field("key", true); Field("revision", true); }
        if (tool == "caption_plan") { Field("key", true); Field("sequenceId", true); Field("revision", true); }
        if (tool == "correct_transcript") { properties["request"] = new JsonObject { ["$ref"] = "#/$defs/TranscriptCorrections" }; required.Add("request"); }
        var result = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
        if (tool is "validate_edit_plan" or "apply_edit_plan")
        {
            var definitions = new JsonObject();
            foreach (var definition in EditPlanSchema.Catalog.Definitions.OfType<TsonTypeAliasDefinition>()) definitions[definition.Name] = Shape(definition.Shape);
            result["$defs"] = definitions;
        }
        if (tool is "start_analysis" or "create_roughcut" or "create_textedit" or "start_creator" or "correct_transcript" or "obs_connect")
        {
            var definitions = new JsonObject();
            foreach (var definition in AnalysisContracts.Catalog.Definitions.OfType<TsonTypeAliasDefinition>()) definitions[definition.Name] = Shape(definition.Shape);
            result["$defs"] = definitions;
        }
        return result;
    }
    private static JsonObject Shape(TsonTypeReference type)
    {
        switch (type.Kind)
        {
            case TsonTypeKind.Reference: return new() { ["$ref"] = "#/$defs/" + type.NominalName };
            case TsonTypeKind.Array: return new() { ["type"] = "array", ["items"] = Shape(type.ElementType!) };
            case TsonTypeKind.Literal: return new() { ["type"] = "string", ["const"] = type.LiteralValue };
            case TsonTypeKind.Union: return new() { ["anyOf"] = new JsonArray(type.Alternatives.Select(a => (JsonNode)Shape(a)).ToArray()) };
            case TsonTypeKind.Structural:
                var properties = new JsonObject();
                var required = new JsonArray();
                foreach (var field in type.Fields)
                {
                    properties[field.Name] = Shape(field.Type);
                    if (!field.Optional) required.Add(field.Name);
                }
                return new() { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
            default: return new() { ["type"] = type.Kind.ToString().ToLowerInvariant() };
        }
    }
    private static async Task<string?> ReadBoundedLine(TextReader input, CancellationToken cancellation)
    {
        var text = new System.Text.StringBuilder();
        var character = new char[1];
        while (await input.ReadAsync(character, cancellation).ConfigureAwait(false) != 0)
        {
            if (character[0] == '\n') return text.ToString();
            if (character[0] != '\r') text.Append(character[0]);
            if (text.Length > 1_048_576) throw new InvalidDataException("MCP frame exceeds 1 MiB.");
        }
        return text.Length == 0 ? null : text.ToString();
    }
}
