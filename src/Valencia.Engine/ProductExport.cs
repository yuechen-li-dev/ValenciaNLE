using System.Text.Json;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.Engine;

public sealed partial class EditingEngine
{
    internal string ExportQueueRoot => Path.Combine(ProductDataRoot, "RenderQueue");
    internal void CheckExportMedia(ProjectSnapshot snapshot) => CheckMedia(snapshot);
    internal void CheckExportOutput(string path) => Policy?.Check(path);

    private static async Task<object> ExportOptionsAsync(JsonElement request, CancellationToken cancellation)
    {
        if (!request.TryGetProperty("probe", out var probe) || !probe.GetBoolean())
            return new { version = 1, presets = ExportCatalog.Presets, encoders = ExportCatalog.Encoders.Select(e => new
            {
                e.Id, e.Codec, e.Implementation, e.Execution, e.Container, e.AudioCodec, e.MaximumHeight, e.MaximumWidth,
                e.MaximumFps, e.PixelFormat, e.Options, e.Compatibility
            }).ToArray() };
        var availability = new List<EncoderAvailability>();
        foreach (var encoder in ExportCatalog.Encoders)
            availability.Add(await ExportCatalog.ProbeAsync(encoder, cancellation).ConfigureAwait(false));
        return new { version = 1, presets = ExportCatalog.Presets, encoders = availability, videoDecode = VideoDecodeProvider.Probe(),
            previewProxyPolicy = MediaProxy.Policy.ToString() };
    }

    private async Task<object> ManageRenderQueue(ProjectSnapshot snapshot, JsonElement request, CancellationToken cancellation)
    {
        string action = Text(request, "action")!;
        if (action == "list") return Exports.List();
        if (action == "enqueue")
        {
            if (!request.TryGetProperty("item", out var item)) throw AgentError("INVALID_REQUEST", "Enqueue requires one render item.");
            return await Exports.EnqueueAsync(snapshot, ParseRenderItem(snapshot, item), cancellation).ConfigureAwait(false);
        }
        if (action == "batch")
        {
            if (!request.TryGetProperty("items", out var items) || items.GetArrayLength() is < 1 or > 4)
                throw AgentError("INVALID_REQUEST", "Batch requires between one and four render items.");
            var specs = items.EnumerateArray().Select(item => ParseRenderItem(snapshot, item)).ToArray();
            if (specs.Select(s => s.Output).Distinct(StringComparer.OrdinalIgnoreCase).Count() != specs.Length)
                throw AgentError("INVALID_REQUEST", "Each batch item requires its own output path.");
            var accepted = new List<ExportStatus>();
            foreach (var spec in specs)
            {
                try { accepted.Add(await Exports.EnqueueAsync(snapshot, spec, cancellation).ConfigureAwait(false)); }
                catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException)
                {
                    return new { jobs = accepted, complete = false, error = new AgentIssue("BATCH_REJECTED", error.Message, Subject: spec.Output) };
                }
            }
            return new { jobs = accepted, complete = true };
        }
        Guid job = Guid.Parse(RequiredText(request, "job"));
        if (action == "status") return Exports.Status(job);
        if (action == "lint") return await Exports.LintAsync(job, cancellation).ConfigureAwait(false);
        if (action == "cancel")
        {
            Exports.Cancel(job);
            return Exports.Status(job);
        }
        string? destination = Text(request, "output") is { } output ? OutputPath(output) : null;
        return await Exports.RetryAsync(snapshot.Document.Id, job, destination, cancellation).ConfigureAwait(false);
    }

    private RenderSpecification ParseRenderItem(ProjectSnapshot snapshot, JsonElement item)
    {
        var sequence = AgentHandles.Sequence(snapshot.Document, RequiredText(item, "sequence"));
        string output = OutputPath(RequiredText(item, "output"));
        string? presetId = Text(item, "preset");
        bool hasCustom = item.TryGetProperty("customPreset", out var custom);
        if (presetId is not null && hasCustom) throw AgentError("INVALID_REQUEST", "Choose a built-in preset or a custom preset.");
        var preset = hasCustom ? EngineJson.Read<ExportPreset>(custom.GetRawText())
            : presetId is not null ? ExportCatalog.Preset(presetId) : null;
        Guid? profileId = Text(item, "profile") is { } profile
            ? AgentHandles.Resolve(profile, "profile", (sequence.Creator?.Profiles ?? []).Select(p => p.Id)) : null;
        string encoder = Text(item, "encoder") ?? preset?.Encoder ?? ExportCatalog.ForDestination(output).Id;
        AudioFinish? finish = item.TryGetProperty("finish", out var audio) ? EngineJson.Read<AudioFinish>(audio.GetRawText()) : preset?.Audio;
        int? quality = item.TryGetProperty("quality", out var requestedQuality) ? requestedQuality.GetInt32() : preset?.Quality;
        bool captions = item.TryGetProperty("burnCaptions", out var burn) ? burn.GetBoolean() : preset?.BurnCaptions ?? true;
        return new(sequence.Id, output, profileId, encoder, preset, finish, captions, quality);
    }
}
