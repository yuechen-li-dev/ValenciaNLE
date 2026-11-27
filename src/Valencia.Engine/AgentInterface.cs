using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Copeland.TS.Tson;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;

namespace Valencia.Engine;

public static class AgentContracts
{
    public const int Version = 1;
    public static string? RequestType(string method) => method switch
    {
        "session_info" => "AgentSession", "edit_objects" => "AgentObjects", "footage_query" => "AgentFootage",
        "agent_plan" => "AgentCandidate", "agent_history" => "AgentHistory", "agent_checkpoint" => "AgentCheckpoint",
        "observe" => "AgentObserve", _ => null
    };
    public static readonly string[] Methods = ["session_info", "edit_objects", "footage_query", "agent_plan",
        "agent_history", "agent_checkpoint", "observe"];
    internal static readonly HashSet<string> EditOperations = typeof(PlanOperation).GetCustomAttributes<JsonDerivedTypeAttribute>()
        .Select(a => (string)a.TypeDiscriminator!).ToHashSet(StringComparer.Ordinal);
}

public sealed record AgentReply(int AgentVersion, long Revision, object? Result = null, AgentIssue? Issue = null)
{
    public bool Ok => Issue is null;
}

public sealed partial class EditingEngine
{
    /// <summary>One typed domain interface for stdio MCP, standalone CLI, trusted Copeland and in-process desktop clients.</summary>
    public async Task<AgentReply> AgentAsync(string method, JsonElement request, CancellationToken cancellation = default)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            string type = AgentContracts.RequestType(method) ?? throw AgentError("UNKNOWN_METHOD", "Unknown Valencia agent method.", method);
            if (request.GetRawText().Length > 1_048_576) throw AgentError("REQUEST_LIMIT", "Agent request exceeds 1 MiB.");
            var errors = TypeProfileCompiler.Validate(EditPlanSchema.Catalog, type, request);
            if (errors.Count != 0)
            {
                if (type == "AgentCandidate" && request.ValueKind == JsonValueKind.Object &&
                    request.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Object &&
                    plan.TryGetProperty("operations", out var ops) && ops.ValueKind == JsonValueKind.Array)
                {
                    int index = 0;
                    foreach (var operation in ops.EnumerateArray())
                    {
                        if (operation.ValueKind == JsonValueKind.Object && operation.TryGetProperty("operation", out var kind) &&
                            kind.ValueKind == JsonValueKind.String && !AgentContracts.EditOperations.Contains(kind.GetString()!))
                            throw new AgentRequestException(new("UNSUPPORTED_EDIT_OPERATION", "That operation is not in the edit plan contract.",
                                "Use an operation from the AgentOperation schema.", kind.GetString(), index,
                                AgentContracts.EditOperations.Order(StringComparer.Ordinal).ToArray()));
                        index++;
                    }
                }
                throw AgentError("INVALID_REQUEST", string.Join("\n", errors.Take(8)),
                    hint: "Use the corresponding Agent contract from schema export or MCP tools/list.");
            }
            var snapshot = Session.Snapshot;
            string? project = Text(request, "projectId");
            if (project is not null && (!Guid.TryParse(project, out var id) || id != snapshot.Document.Id))
                throw AgentError("WRONG_PROJECT", "The request does not target this editing session.", project, "Use session_info to obtain the current project identity.");
            if (Text(request, "revision") is { } wireRevision)
            {
                long revision = EngineJson.Int64(wireRevision);
                if (revision != snapshot.Revision) throw new AgentRequestException(AgentStale(revision, snapshot.Revision));
            }
            object value = method switch
            {
                "session_info" => await AgentOverviewAsync(snapshot, cancellation).ConfigureAwait(false),
                "edit_objects" => Objects(snapshot, request),
                "footage_query" => await FootageAsync(snapshot, request, cancellation).ConfigureAwait(false),
                "agent_plan" => ManageCandidate(snapshot, request, cancellation),
                "agent_history" => await AgentHistoryAsync(snapshot, request, cancellation).ConfigureAwait(false),
                "agent_checkpoint" => ManageCheckpoint(snapshot, request),
                "observe" => await ObserveAsync(snapshot, request, cancellation).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Agent contract is not routed.")
            };
            return new(AgentContracts.Version, Session.Snapshot.Revision, value);
        }
        catch (AgentRequestException exception) { return new(AgentContracts.Version, Session.Snapshot.Revision, Issue: exception.Issue); }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or OverflowException
            or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException or KeyNotFoundException)
        {
            string code = exception switch
            {
                OverflowException => "TIME_RANGE", FileNotFoundException => "MISSING_MEDIA",
                UnauthorizedAccessException => "PATH_POLICY", NotSupportedException => "UNSUPPORTED_CAPABILITY",
                FormatException or JsonException => "INVALID_REQUEST", _ => "REQUEST_REJECTED"
            };
            return new(AgentContracts.Version, Session.Snapshot.Revision, Issue: new(code, exception.Message));
        }
    }

    private (bool Video, bool Audio)? agentExportEncoders;
    private async Task<object> AgentOverviewAsync(ProjectSnapshot snapshot, CancellationToken cancellation)
    {
        var project = snapshot.Document;
        object decoder;
        try
        {
            decoder = new { available = true, version = Media.FfmpegRuntime.DecoderIdentity,
                projectCodecs = project.Assets.SelectMany(a => a.Streams).Select(s => s.Codec).Distinct().Take(16)
                    .Select(name => new { name, installed = Media.FfmpegRuntime.HasDecoder(name) }).ToArray() };
        }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        { decoder = new { available = false, issue = e.Message }; }
        object export;
        try
        {
            var installed = agentExportEncoders;
            if (installed is null)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                limit.CancelAfter(TimeSpan.FromSeconds(3));
                var (stdout, _) = await Media.MediaProcess.CaptureAsync(["-hide_banner", "-encoders"], limit.Token).ConfigureAwait(false);
                var names = stdout.Split('\n').Select(row => row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    .Where(row => row.Length >= 2).Select(row => row[1]).ToHashSet(StringComparer.Ordinal);
                agentExportEncoders = installed = (names.Contains("libx264"), names.Contains("aac"));
            }
            export = new { container = "mp4", h264Libx264 = installed.Value.Video, aac = installed.Value.Audio,
                available = installed.Value.Video && installed.Value.Audio };
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception ||
            e is OperationCanceledException && !cancellation.IsCancellationRequested)
        { export = new { container = "mp4", available = false, issue = "External FFmpeg encoder check failed: " + e.Message }; }
        return new
        {
            projectId = project.Id, project.Name, snapshot.Revision, engineApi = ApiVersion, agentApi = AgentContracts.Version,
            authority = Policy is null ? "inProcess" : "headlessWorkspace", activeSequence = project.Sequences.Length == 1
                ? AgentHandles.For("sequence", project.Sequences[0].Id) : null,
            activeSequencePolicy = "The only sequence is the default; otherwise select a sequence handle. Desktop selection is not shared.",
            sequenceCount = project.Sequences.Length, assetCount = project.Assets.Length,
            sequences = project.Sequences.Take(8).Select(s => new { handle = AgentHandles.For("sequence", s.Id), s.Name, duration = ShortTime(s.Duration, s.Video.FrameRate) }).ToArray(),
            analysis = new { kinds = new[] { "audio", "scenes", "transcript", "loudness", "proxy", "index" }, lookup = "footage_query kind:summary", activeJobs = Analysis.ActiveJobs() },
            jobs = new { exports = Exports.ActiveJobs(), creator = CreatorJobs.ActiveJobs() },
            editing = new { actions = AgentContracts.Methods, operationSchema = "AgentOperation", effects = "effects_list", Session.CanUndo, Session.CanRedo },
            codec = new { decoder, export }, observations = new[] { "source", "sequence", "candidate", "paired", "sourceAudio", "mixAudio" },
            persistence = "Explicit save; history, candidates and checkpoints are in-memory for this session."
        };
    }

    private static object Objects(ProjectSnapshot snapshot, JsonElement request)
    {
        var doc = snapshot.Document;
        string kind = Text(request, "kind")!;
        string? role = Text(request, "role"), reference = Text(request, "handle");
        if (role is not null && kind is not ("track" or "asset" or "clip"))
            throw AgentError("INVALID_REQUEST", "Role is an asset/track filter (or clip track-role filter).");
        var sequence = kind is "sequence" or "asset" ? null : SelectSequence(doc, Text(request, "sequence"));
        var items = new List<(Guid Id, object Body)>();
        switch (kind)
        {
            case "sequence":
                items.AddRange(doc.Sequences.Select(s => (s.Id, (object)new { s.Name, duration = ShortTime(s.Duration, s.Video.FrameRate), s.Video.FrameRate,
                    tracks = s.Tracks.Length, markers = s.Markers.Length })));
                break;
            case "asset":
                items.AddRange(doc.Assets.Where(a => role is null || a.Role == role).Select(a => (a.Id, (object)new
                {
                    a.Name, a.Role, a.Container, missing = !File.Exists(a.SourcePath),
                    streams = a.Streams.Select(s => new { s.Index, s.Kind, s.Codec, s.Start, s.End, s.FrameRate, s.Channels, s.SampleRate }).ToArray()
                })));
                break;
            case "track":
                items.AddRange(sequence!.Tracks.Where(t => role is null || t.Role == role)
                    .Select(t => (t.Id, (object)new { t.Name, t.Role, t.Kind, t.Order, t.State, clips = t.Clips.Length })));
                break;
            case "marker":
                items.AddRange(sequence!.Markers.Select(m => (m.Id, (object)new { m.Label, at = ShortTime(m.At, sequence.Video.FrameRate) })));
                break;
            case "profile":
                items.AddRange((sequence!.Creator?.Profiles ?? []).Select(p => (p.Id, (object)new
                { p.Name, p.Width, p.Height, p.Fit, p.FrameRate, p.Start, p.End })));
                break;
            case "clip":
                var current = sequence!;
                var range = PositionRange(request, current);
                foreach (var t in current.Tracks.Where(t => role is null || t.Role == role))
                    foreach (var c in t.Clips.Where(c => c.Start.Ticks < range.End && c.End.Ticks > range.Start))
                        items.Add((c.Id, new { track = AgentHandles.For("track", t.Id), t.Role, name = c.Title?.Text ?? c.Properties.Name,
                            asset = c.Title is null ? AgentHandles.For("asset", c.AssetId) : null, stream = c.StreamIndex,
                            start = ShortTime(c.Start, current.Video.FrameRate), end = ShortTime(c.End, current.Video.FrameRate),
                            c.SourceIn, c.SourceOut, c.Retime, enabled = c.Properties.Enabled }));
                break;
        }
        if (reference is not null)
        {
            Guid id = AgentHandles.Resolve(reference, kind, items.Select(i => i.Id));
            items = items.Where(i => i.Id == id).ToList();
        }
        return Page(request, items.Select(i => new { handle = AgentHandles.For(kind, i.Id), id = reference is null ? (Guid?)null : i.Id, details = i.Body }).ToList());
    }

    private async Task<object> AgentHistoryAsync(ProjectSnapshot snapshot, JsonElement request, CancellationToken cancellation)
    {
        string action = Text(request, "action")!;
        if (action == "save")
        {
            string path = RequiredText(request, "output");
            string destination = OutputPath(path);
            if (File.Exists(destination)) throw AgentError("OUTPUT_EXISTS", "Agent save is Save As; choose a new project output.", path);
            await new Persistence.ProjectFile().SaveAsync(destination, snapshot, cancellation).ConfigureAwait(false);
            return new { action, priorRevision = snapshot.Revision, resultingRevision = snapshot.Revision, output = path };
        }
        var result = action == "undo" ? Undo(snapshot.Revision, cancellation) : Redo(snapshot.Revision, cancellation);
        if (!result.Success) throw new AgentRequestException(ReportEditFailure(result, snapshot.Revision));
        return new { action, priorRevision = snapshot.Revision, resultingRevision = result.Revision, Session.CanUndo, Session.CanRedo };
    }

    private sealed record Checkpoint(string Name, ProjectSnapshot Snapshot);
    private readonly object agentGate = new();
    private readonly Dictionary<string, Checkpoint> agentCheckpoints = new(StringComparer.OrdinalIgnoreCase);
    private object ManageCheckpoint(ProjectSnapshot snapshot, JsonElement request)
    {
        string action = Text(request, "action")!;
        lock (agentGate)
        {
            var scoped = agentCheckpoints.Values.Where(c => c.Snapshot.Document.Id == snapshot.Document.Id).ToArray();
            if (action == "list") return scoped.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(Describe).ToArray();
            string name = RequiredText(request, "name");
            if (name.Length > 80 || name != name.Trim() || name.Any(char.IsControl))
                throw AgentError("INVALID_CHECKPOINT_NAME", "Checkpoint name must be 1..80 printable characters with no surrounding whitespace.", name);
            var entry = scoped.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (action == "create")
            {
                if (entry is not null) throw AgentError("CHECKPOINT_EXISTS", "Checkpoint name already exists; choose a new name or explicitly delete the previous checkpoint.", name);
                if (agentCheckpoints.Count >= 32) throw AgentError("CHECKPOINT_LIMIT", "The session can keep 32 checkpoints; explicitly delete one before creating another.");
                var created = new Checkpoint(name, snapshot);
                agentCheckpoints.Add(snapshot.Document.Id.ToString("N") + name, created);
                return Describe(created);
            }
            if (entry is null) throw AgentError("UNKNOWN_CHECKPOINT", "No checkpoint with that name exists in this session.", name, "List current session checkpoints.");
            if (action == "inspect") return Describe(entry);
            if (action == "delete") { agentCheckpoints.Remove(snapshot.Document.Id.ToString("N") + name); return new { name, deleted = true }; }
            var result = Session.CommitStagedEdit(snapshot, entry.Snapshot.Document, Guid.NewGuid());
            if (!result.Success) throw new AgentRequestException(ReportEditFailure(result, snapshot.Revision));
            return new { action, name, checkpointRevision = entry.Snapshot.Revision, priorRevision = snapshot.Revision, resultingRevision = result.Revision };
        }

        static object Describe(Checkpoint c) => new { c.Name, c.Snapshot.Revision, persistence = "session",
            assetCount = c.Snapshot.Document.Assets.Length, sequenceCount = c.Snapshot.Document.Sequences.Length,
            sequences = c.Snapshot.Document.Sequences.Take(8).Select(s => new { handle = AgentHandles.For("sequence", s.Id), s.Name, duration = ShortTime(s.Duration, s.Video.FrameRate) }).ToArray() };
    }

    private static SequenceDocument SelectSequence(ProjectDocument document, string? reference) => reference is not null
        ? AgentHandles.Sequence(document, reference) : document.Sequences.Length == 1 ? document.Sequences[0]
            : throw AgentError("TIME_CONTEXT", "Select a sequence explicitly when the project does not have exactly one sequence.", hint: "Use edit_objects kind:sequence.");
    private static object ShortTime(SequenceTime time, FrameRate? rate = null, string space = "sequence")
    {
        var detail = AgentTime.Describe(time, space, rate);
        return new { detail.Ticks, label = detail.Timecode ?? detail.Seconds + "s" };
    }

    private static (long Start, long End) PositionRange(JsonElement request, SequenceDocument sequence)
    {
        string? at = Text(request, "at"), start = Text(request, "start"), end = Text(request, "end");
        if (at is not null && (start is not null || end is not null))
            throw AgentError("INVALID_REQUEST", "Use either a single position or a range, not both.");
        if (at is not null) { long position = AgentTime.Position(at, sequence: sequence).Ticks; return (position, checked(position + 1)); }
        long first = start is null ? 0 : AgentTime.Position(start, sequence: sequence).Ticks;
        long last = end is null ? long.MaxValue : AgentTime.Position(end, sequence: sequence).Ticks;
        if (last <= first) throw AgentError("TIME_RANGE", "Range end must follow start.");
        return (first, last);
    }
    private static object Page<T>(JsonElement request, IReadOnlyList<T> items, object? context = null)
    {
        int offset = Integer(request, "offset", 0), limit = Integer(request, "limit", 16);
        if (offset < 0 || limit is < 1 or > 64) throw AgentError("PAGE_RANGE", "Use offset >= 0 and limit 1..64.");
        int count = Math.Min(limit, Math.Max(0, items.Count - offset));
        return new { total = items.Count, offset, limit, nextOffset = count > 0 && offset + count < items.Count ? (int?)(offset + count) : null,
            items = items.Skip(offset).Take(count).ToArray(), context };
    }

    private static string? Text(JsonElement input, string name) => input.TryGetProperty(name, out var item) ? item.GetString() : null;
    private static string RequiredText(JsonElement input, string name) => Text(input, name) is { Length: > 0 } value ? value
        : throw AgentError("INVALID_REQUEST", $"This action requires {name}.");
    private static int Integer(JsonElement input, string name, int fallback) => input.TryGetProperty(name, out var item)
        ? item.TryGetInt32(out int value) ? value : throw AgentError("INVALID_REQUEST", $"{name} must be an integer.") : fallback;
    private static AgentRequestException AgentError(string code, string message, string? subject = null, string? hint = null) =>
        new(new(code, message, hint, subject));

    internal static AgentIssue? JobIssue(string state, string? error, Guid job) => state switch
    {
        "cancelled" => new("JOB_CANCELLED", "Job was cancelled before completion.", Subject: job.ToString()),
        "failed" => new("JOB_FAILED", error ?? "Background job failed.", Subject: job.ToString()),
        _ => null
    };
}
