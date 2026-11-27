using System.Text.Json;
using System.Text.Json.Nodes;
using Copeland.TS.Tson;
using Valencia.Core;
using Valencia.Engine;
using Valencia.Media;

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
        "obs_connect", "obs_status", "obs_record", "obs_marker", "obs_import", "effects_list", "source_frame", "review_timeline", .. AgentContracts.Methods];

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
                    result = new { protocolVersion = ProtocolVersion, capabilities = new { tools = new { } }, serverInfo = new { name = "Valencia", version = "M3b" },
                        instructions = "Start with session_info (no arguments), edit_objects and footage_query. This stdio process owns a separate headless session; nothing is saved implicitly." };
                }
                else
                {
                    if (!ready) throw new ArgumentException("Initialize and notifications/initialized are required.");
                    result = method switch
                    {
                        "ping" => new { },
                        "tools/list" => new { tools = ToolNames.Select(name => new { name, description = DescribeTool(name), inputSchema = InputSchema(name) }) },
                        "tools/call" => await Call(request.GetProperty("params"), cancellation).ConfigureAwait(false),
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

    private async Task<object> Call(JsonElement parameters, CancellationToken cancellation)
    {
        string name = parameters.GetProperty("name").GetString()!;
        if (!ToolNames.Contains(name)) throw new ArgumentException("Unknown tool.");
        var arguments = parameters.TryGetProperty("arguments", out var payload) ? payload : JsonSerializer.SerializeToElement(new { });
        if (AgentContracts.RequestType(name) is not null)
        {
            AgentReply reply = await engine.AgentAsync(name, arguments, cancellation).ConfigureAwait(false);
            string json = EngineJson.Write(reply);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 131072)
            {
                reply = new(AgentContracts.Version, reply.Revision, Issue: new("RESPONSE_LIMIT", "Agent response exceeds 128 KiB.",
                    "Request fewer samples or a smaller page."));
                json = EngineJson.Write(reply);
            }
            var content = new List<object> { new { type = "text", text = json } };
            if (reply.Result is AgentObservation images && arguments.TryGetProperty("inlineImage", out var inline) && inline.ValueKind == JsonValueKind.True)
            {
                long bytes = images.Files.Sum(path => new FileInfo(path).Length);
                if (bytes <= 512 * 1024)
                    foreach (string path in images.Files)
                        content.Add(new { type = "image", mimeType = "image/png", data = Convert.ToBase64String(await File.ReadAllBytesAsync(path, cancellation)) });
                else
                    content.Add(new { type = "text", text = "PNG previews exceed the 512-KiB inline budget. The rendered PNG files are available at the returned paths." });
            }
            return new { content, structuredContent = new { apiVersion = 1, result = reply }, isError = !reply.Ok };
        }
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
                "source_frame" => SourceFrame(arguments, cancellation),
                "review_timeline" => await Review(arguments, cancellation).ConfigureAwait(false),
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
            string code = error switch
            {
                UnauthorizedAccessException => "PATH_POLICY", FileNotFoundException when name is "analysis_result" or "transcript_query" => "ANALYSIS_MISSING",
                FileNotFoundException => "MISSING_MEDIA", KeyNotFoundException => "UNKNOWN_REFERENCE",
                NotSupportedException => "UNSUPPORTED_CAPABILITY", _ => "TOOL_REJECTED"
            };
            value = new { apiVersion = 1, code, message = error.Message, revision = EngineJson.Exact(engine.Session.Snapshot.Revision) };
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
    private SourceFrameInfo SourceFrame(JsonElement arguments, CancellationToken cancellation)
    {
        bool hasX = arguments.TryGetProperty("x", out var x), hasY = arguments.TryGetProperty("y", out var y);
        if (hasX != hasY) throw new ArgumentException("Supply both x and y for a source point.");
        return engine.SourceFrame(arguments.GetProperty("assetId").GetGuid(), arguments.GetProperty("stream").GetInt32(),
            new(EngineJson.Int64(arguments.GetProperty("atTicks").GetString()!)), arguments.GetProperty("output").GetString()!,
            arguments.TryGetProperty("maxEdge", out var edge) ? edge.GetInt32() : 640,
            hasX ? new(x.GetDouble(), y.GetDouble()) : null, cancellation);
    }
    private Task<ReviewResult> Review(JsonElement arguments, CancellationToken cancellation) =>
        engine.ReviewAsync(arguments.GetProperty("projectId").GetGuid(), arguments.GetProperty("sequenceId").GetGuid(),
            EngineJson.Int64(arguments.GetProperty("revision").GetString()!), ReviewSelection.Parse(arguments.GetProperty("selection")),
            arguments.GetProperty("output").GetString()!,
            arguments.TryGetProperty("plan", out var plan) ? EditPlanSchema.Parse(plan.GetRawText()) : null, cancellation);
    private object Cancel(JsonElement arguments)
    {
        Guid id = Guid.Parse(arguments.GetProperty("jobId").GetString()!);
        engine.Exports.Cancel(id);
        return engine.Exports.Status(id);
    }
    private static JsonObject InputSchema(string tool)
    {
        if (AgentContracts.RequestType(tool) is { } requestType)
        {
            var catalog = EditPlanSchema.Catalog;
            var root = catalog.Definitions.OfType<TsonTypeAliasDefinition>().Single(definition => definition.Name == requestType);
            return AttachDefinitions(Shape(root.Shape), catalog);
        }
        var properties = new JsonObject { ["projectId"] = new JsonObject { ["type"] = "string" } };
        var required = new JsonArray("projectId");
        void Field(string name, bool mandatory = false)
        {
            properties[name] = new JsonObject { ["type"] = "string" };
            if (mandatory) required.Add(name);
        }
        if (tool == "timeline_query") { Field("sequenceId"); Field("atTicks"); Field("rangeStart"); Field("rangeEnd"); }
        if (tool == "project_info") properties["compact"] = new JsonObject { ["type"] = "boolean" };
        if (tool == "source_frame")
        {
            Field("assetId", true); Field("atTicks", true); Field("output", true);
            properties["stream"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 };
            required.Add("stream");
            properties["maxEdge"] = new JsonObject { ["type"] = "integer", ["minimum"] = 64, ["maximum"] = 1024 };
            foreach (string axis in new[] { "x", "y" }) properties[axis] = new JsonObject { ["type"] = "number", ["minimum"] = 0 };
        }
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
        if (tool == "review_timeline")
        {
            Field("sequenceId", true); Field("revision", true); Field("output", true);
            properties["selection"] = new JsonObject { ["$ref"] = "#/$defs/ReviewSelection" };
            properties["plan"] = new JsonObject { ["$ref"] = "#/$defs/EditPlan" };
            required.Add("selection");
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
        if (tool is "validate_edit_plan" or "apply_edit_plan" or "review_timeline")
        {
            AttachDefinitions(result, EditPlanSchema.Catalog);
        }
        if (tool is "start_analysis" or "create_roughcut" or "create_textedit" or "start_creator" or "correct_transcript" or "obs_connect")
        {
            AttachDefinitions(result, AnalysisContracts.Catalog);
        }
        return result;
    }
    private static JsonObject AttachDefinitions(JsonObject root, TsonCatalog catalog)
    {
        var index = catalog.Definitions.OfType<TsonTypeAliasDefinition>().ToDictionary(d => d.Name);
        var definitions = new JsonObject();
        Visit(root);
        if (definitions.Count > 0) root["$defs"] = definitions;
        return root;

        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["$ref"] is JsonValue reference)
                {
                    string name = reference.GetValue<string>()["#/$defs/".Length..];
                    if (!definitions.ContainsKey(name))
                    {
                        var definition = Shape(index[name].Shape);
                        definitions[name] = definition;
                        Visit(definition);
                    }
                }
                foreach (var item in obj) Visit(item.Value);
            }
            else if (node is JsonArray array)
                foreach (var item in array) Visit(item);
        }
    }

    private static string DescribeTool(string name) => name switch
    {
        "session_info" => "Start here. No arguments required. Read the attached headless project ID, revision, default sequence, analysis kinds, jobs and codec/observation capabilities. Example: {}.",
        "edit_objects" => "Read paged sequence, asset, track, clip, marker or profile handles. Optional revision pins the read; use role for all matches and at or start/end in sequence time for clips. Example with projectId: kind:'clip', at:'1s', limit:8.",
        "footage_query" => "Read independent cached analyses as a bounded footage index. Start with kind:'summary'; then select asset/stream, artifact keys, source-container or sequence time, kind, and predicates. Requires projectId and revision. Example: kind:'matches', transcriptContains:'let’s go', minimumSeparation:'8s'. Continue with nextOffset and artifactSet.",
        "agent_plan" => "Prepare an immutable dry-run candidate, inspect, list, compare, discard or explicitly commit it. Requires projectId/revision; prepare takes a sequence handle and typed operations; compare takes candidate and other. Example: action:'prepare', plan:{sequence:<sequence>,operations:[{operation:'delete',clipId:<clip>}]}. Commit mutates this session; stale candidates never rebase silently.",
        "agent_history" => "Undo or redo one transaction in this running headless session; action:'save' writes a snapshot to output. Requires projectId/revision, and workspace-relative .valencia output for save. Example: action:'undo'. History is not loaded from disk.",
        "agent_checkpoint" => "Create, list, inspect, restore or delete named checkpoints. Use durable:true to retain them across restarts. Restore is an undoable commit. Requires projectId/revision and name except for list.",
        "recovery" => "List, inspect, recover, or discard completed interrupted autosaves within the workspace. Recover writes a new project at output. Requires projectId, revision, and an ID except for list.",
        "export_options" => "List versioned export presets and encoder implementations, or run live device/encoder checks with probe:true. Requires projectId.",
        "render_queue" => "Queue immutable revision-pinned exports, list or inspect jobs, cancel, retry, and lint. Pass item for enqueue, items for batch, and job for status/cancel/retry/lint.",
        "model_options" => "Inspect optional model identity, supported backend, installation state, pinned download size and SHA-256. No arguments are required.",
        "observe" => "Render labelled PNGs of original frames, current/candidate composites, paired comparison, cached source audio or bounded rendered mix. Requires projectId/revision, mode, new workspace PNG output. Example: mode:'sequence', at:['1s','2s'], output:'review.png', inlineImage:true. Source requires asset+stream; paired requires candidate+beforeOutput. Returns actual sample metadata.",
        "project_info" => "Read project, revision and missing media; projectId required. Set compact:true to avoid full document output; large results are rejected. No mutation. Prefer session_info to discover identity.",
        "timeline_query" => "Read sequences, tracks, active clips or an intersecting range. Requires projectId; sequenceId selects a sequence. atTicks or rangeStart/rangeEnd are exact sequence ticks as strings. Example with sequenceId: atTicks:'0'. No mutation.",
        "validate_edit_plan" => "Dry-run a canonical v1 plan without mutating. Requires projectId, revision and a matching plan. Positions in ticks unless represented as rational source positions. Inspect valid, changes and issues. Prefer agent_plan prepare for readable time and handles.",
        "apply_edit_plan" => "Atomically commit a canonical v1 plan to this session, not to disk. Requires projectId, current revision and matching plan. Returns resulting revision or structured validation issues. Stale/invalid plans do not apply.",
        "run_export" => "Start H.264/AAC export from the requested revision. Requires projectId/revision, sequenceId and workspace-relative output; optional profileId. Background job; poll export_status with jobId. No project edits.",
        "export_status" => "Read a background export job using projectId and returned jobId. Includes progress, errors or terminal state.",
        "cancel_export" => "Request cancellation of an export using projectId and jobId; poll export_status for the terminal state. No project edit.",
        "start_analysis" => "Start or reuse an independent source artifact. Requires projectId and typed request with assetId, streamIndex, kind (e.g. 'audio'). Background job; poll analysis_status, then use its key with footage_query or analysis_result.",
        "analysis_status" => "Read analysis job state, key, progress and error using projectId and jobId. Use the key only once complete.",
        "analysis_result" => "Read cached artifact metadata or a bounded page. Requires projectId and key; optional kind, offset, limit, level. Example: kind:'silence', limit:16. Artifacts can be evicted or stale.",
        "cancel_analysis" => "Request cancellation of a source analysis using projectId and jobId; inspect analysis_status. No project edit.",
        "create_roughcut" => "Preview a silence-based ripple plan from a cached audio artifact. Requires projectId, revision, sequenceId and key; optional typed policy. Returns a proposal; no commit. Ranges use source alignment.",
        "transcript_query" => "Read/search a cached transcript using projectId and key. Supply query (e.g. 'important announcement'), or rangeStart/rangeEnd as container-relative tick strings; offset/limit bound detail. No edit.",
        "create_textedit" => "Build a plan that retains or removes timestamped transcript words. Requires projectId/revision and typed request naming sequence, transcript and word IDs. Rejects unaligned or unsupported source layouts. Does not commit.",
        "start_creator" => "Start background planning for a long edit and vertical profiles. Requires projectId/revision and typed request with sequence and completed transcript/audio keys. Time fields use exact ticks. Poll creator_status; does not commit.",
        "creator_status" => "Read planner job state and a compact proposal reference/summary using projectId and jobId; inspect terminal errors or use the plan key with apply_creator.",
        "cancel_creator" => "Cancel a background creator job by projectId and jobId. No edit; poll creator_status for completion.",
        "apply_creator" => "Explicitly commit a completed creator plan using projectId, revision and proposal key. Mutates only this session, rejects stale revision.",
        "caption_plan" => "Build an uncommitted caption plan using projectId, revision, sequenceId and aligned transcript key. Keep source timing; review then commit the plan.",
        "correct_transcript" => "Publish a corrected analysis artifact without changing project timeline or original artifact. Requires projectId and typed corrections request; returns the new artifact key.",
        "obs_connect" => "Connect to OBS explicitly. Requires projectId and typed request with workspace-relative recording root and manifest; optional endpoint. Does not start recording.",
        "obs_status" => "Read connected OBS state and manifest summary with projectId. Connect first; no mutation.",
        "obs_record" => "Control connected OBS with projectId and action (e.g. 'start' or 'stop'). Explicit server permission required. This changes external OBS recording state, not timeline revision.",
        "obs_marker" => "Log an approximate OBS output-duration marker with projectId, note, and optional kind. Example: note:'Highlight'. Requires connected recording workflow; timestamp is not media PTS.",
        "obs_import" => "Import completion-proven OBS files into the current project. Requires projectId and revision; uses attached manifest. Mutates project assets and returns current revision.",
        "effects_list" => "List supported versioned clip effects and presets; requires projectId. No project mutation.",
        "source_frame" => "Write an ungraded source PNG. Requires projectId, assetId, stream, atTicks (stream-start offset), and new workspace output. Optional maxEdge and paired x/y pixel-edge coordinates. Returns actual decoded PTS.",
        "review_timeline" => "Write current or v1-plan composited PNG/MP4. Requires projectId/revision, sequenceId, typed selection and new workspace output; optional canonical plan. Example: selection:{kind:'frame',atTicks:['0']}. Bounded; no edit.",
        _ => throw new ArgumentException("Unknown tool name.")
    };
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
