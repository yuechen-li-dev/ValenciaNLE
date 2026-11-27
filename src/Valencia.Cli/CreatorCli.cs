using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Valencia.Analysis;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Media;

namespace Valencia.Cli;

internal static class CreatorCli
{
    public static bool Handles(string[] args) => args[0] is "creator" or "proxy" or "index" or "loudness" or "captions" or "outputs"
        || args is ["transcript", "correct", ..];
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellation)
    {
        if (args.Length < 3) throw new ArgumentException("Use creator plan|run|apply|export <project>, captions generate <project>, outputs list <project>, proxy create <project>, index build <project>, loudness analyze <project>.");
        await using var engine = await EditingEngine.OpenAsync(args[2], cancellation: cancellation);
        string? Option(string name) => Program.Option(args, name);
        string Required(string name) => Option(name) ?? throw new ArgumentException("Missing " + name);
        var sequence = Option("--sequence") is { } selected ? Guid.Parse(selected) : engine.Query().Sequences[0].Id;
        if (args[0] == "outputs") { Console.WriteLine(EngineJson.Write(engine.Query().Sequence(sequence).Creator?.Profiles ?? [])); return 0; }
        if (args[0] is "index" or "proxy" or "loudness")
        {
            if (args[0] == "loudness" && Option("--asset") is null)
            {
                Console.WriteLine(EngineJson.Write(await LoudnessAnalyzer.SequenceAsync(engine.Session.Snapshot, sequence, cancellation)));
                return 0;
            }
            Guid asset = Guid.Parse(Required("--asset")); int stream = int.Parse(Required("--stream"), CultureInfo.InvariantCulture);
            var result = await Analyze(engine, new(asset, stream, args[0]), cancellation);
            if (args[0] == "proxy" && args.Contains("--attach"))
            {
                var attached = engine.AttachProxy(asset, result.Proxy!.Key, engine.Session.Snapshot.Revision, cancellation);
                if (!attached.Success) throw new ArgumentException(attached.Message);
                if (Option("--save-as") is { } save) await engine.SaveAsync(save, cancellation);
            }
            Console.WriteLine(EngineJson.Write(AnalysisContracts.Page(result)));
            return 0;
        }
        if (args[0] == "transcript")
        {
            var request = AnalysisContracts.Parse<TranscriptCorrections>(await File.ReadAllTextAsync(Required("--edits"), cancellation), "TranscriptCorrections");
            var corrected = await engine.CorrectTranscriptAsync(request.Key, request.Edits.ToDictionary(c => c.WordId, c => c.Text), cancellation);
            Console.WriteLine(EngineJson.Write(AnalysisContracts.Page(corrected, "words")));
            return 0;
        }
        if (args is ["creator", "apply", ..])
        {
            var plan = EditPlanSchema.Parse(await File.ReadAllTextAsync(Required("--plan"), cancellation));
            var result = engine.Plan(plan, true, cancellation);
            if (result.Committed && Option("--save-as") is { } save) await engine.SaveAsync(save, cancellation);
            Console.WriteLine(EngineJson.Write(result));
            return result.Valid ? 0 : 2;
        }
        if (args is ["creator", "export", ..])
        {
            Console.WriteLine(EngineJson.Write(await engine.ExportProfilesAsync(sequence, Required("--output-dir"), cancellation, Option("--encoder"))));
            return 0;
        }
        if (args[0] == "captions" || args is ["creator", "plan", ..])
        {
            var proposal = args[0] == "captions" ? await engine.CreateCaptionsAsync(sequence, Required("--analysis"), cancellation)
                : await engine.CreateCreatorAsync(AnalysisContracts.Parse<CreatorRequest>(await File.ReadAllTextAsync(Required("--request"), cancellation), "CreatorRequest", Required("--request")), cancellation);
            if (Option("--output") is { } output) await File.WriteAllTextAsync(output, EngineJson.Write(proposal.Plan), cancellation);
            Console.WriteLine(EngineJson.Write(EditingEngine.CreatorSummary(proposal)));
            return proposal.Preview.Valid ? 0 : 2;
        }
        if (args is not ["creator", "run", ..]) throw new ArgumentException("Unknown creator operation.");
        var timer = Stopwatch.StartNew();
        Guid assetId = Guid.Parse(Required("--asset")); int audioIndex = int.Parse(Required("--stream"), CultureInfo.InvariantCulture);
        var assetSource = engine.Query().Assets.First(a => a.Id == assetId);
        // Inspect original media before background indexing or optional proxies. No device/UI is required headlessly.
        var first = Stopwatch.StartNew();
        var inspectAt = engine.Query().Sequence(sequence).Tracks.Where(t => t.Kind == TrackKind.Video && t.State.Visible)
            .SelectMany(t => t.Clips).Where(c => c.Properties.Enabled).Select(c => c.Start).MinBy(t => t.Ticks);
        using (var evaluator = new SequenceEvaluator(engine.Session.Snapshot, sequence))
        {
            var frame = evaluator.Video(inspectAt, cancellation);
            if (assetSource.Streams.Any(s => s.Kind == TrackKind.Video) && frame.SourceTimestamps is not { IsEmpty: false })
                throw new InvalidDataException("Initial source inspection did not decode a video frame.");
            _ = evaluator.Audio(inspectAt, 0, 1024, cancellation);
        }
        double firstFrameMs = first.Elapsed.TotalMilliseconds;
        var stages = new List<object>();
        foreach (var stream in assetSource.Streams)
        {
            var index = await Analyze(engine, new(assetId, stream.Index, "index"), cancellation);
            stages.Add(AnalysisContracts.Page(index));
        }
        if (args.Contains("--proxy") && assetSource.Streams.FirstOrDefault(s => s.Kind == TrackKind.Video) is { } video)
        {
            var proxy = await Analyze(engine, new(assetId, video.Index, "proxy"), cancellation);
            var attached = engine.AttachProxy(assetId, proxy.Proxy!.Key, engine.Session.Snapshot.Revision, cancellation);
            if (!attached.Success) throw new ArgumentException(attached.Message);
            stages.Add(AnalysisContracts.Page(proxy));
        }
        var audio = await Analyze(engine, new(assetId, audioIndex, "audio"), cancellation);
        var transcript = await Analyze(engine, new(assetId, audioIndex, "transcript", Transcription: new(Alignment: "dtw")), cancellation);
        AnalysisArtifact? scenes = null;
        if (assetSource.Streams.FirstOrDefault(s => s.Kind == TrackKind.Video) is { } videoStream)
            scenes = await Analyze(engine, new(assetId, videoStream.Index, "scenes", Scenes: new(EveryNthFrame: 150)), cancellation);
        var loudness = await Analyze(engine, new(assetId, audioIndex, "loudness"), cancellation);
        stages.AddRange(new[] { AnalysisContracts.Page(audio), AnalysisContracts.Page(transcript), AnalysisContracts.Page(loudness) });
        if (scenes is not null) stages.Add(AnalysisContracts.Page(scenes));
        string directory = Path.GetFullPath(Required("--output-dir")); Directory.CreateDirectory(directory);
        string Ticks(string value)
        {
            decimal ticks = decimal.Parse(value, CultureInfo.InvariantCulture) * SequenceTime.TicksPerSecond;
            if (ticks != decimal.Truncate(ticks)) throw new ArgumentException("Duration must be exactly representable in sequence ticks.");
            return checked((long)ticks).ToString(CultureInfo.InvariantCulture);
        }
        var creatorRequest = new CreatorRequest(sequence, transcript.Key, audio.Key, scenes?.Key, TargetTicks: Ticks(Option("--target-seconds") ?? "600"),
            MinimumTicks: Ticks(Option("--min-seconds") ?? "10"), MaximumTicks: Ticks(Option("--max-seconds") ?? "45"),
            ShortTicks: Ticks(Option("--short-seconds") ?? "20"), Width: int.Parse(Option("--width") ?? "1280", CultureInfo.InvariantCulture),
            Height: int.Parse(Option("--height") ?? "720", CultureInfo.InvariantCulture),
            Keywords: Option("--keywords")?.Split(';', StringSplitOptions.RemoveEmptyEntries).ToImmutableArray(), Normalize: !args.Contains("--no-normalize"));
        if (Option("--recording-manifest") is { } recordingManifest)
            creatorRequest = creatorRequest with { Markers = ObsWorkflow.PlannerMarkers(Valencia.Obs.ObsManifestFile.Load(recordingManifest), assetId, args.Contains("--use-approximate-markers")) };
        if (Option("--music-streams") is { } music)
            creatorRequest = creatorRequest with { MusicStreamIndices = music.Length == 0 ? [] : music.Split(',').Select(i => int.Parse(i, CultureInfo.InvariantCulture)).ToImmutableArray() };
        var creator = await engine.CreateCreatorAsync(creatorRequest, cancellation);
        string planFile = Path.Combine(directory, "creator-plan.json");
        await File.WriteAllTextAsync(planFile, EngineJson.Write(creator.Plan), cancellation);
        await File.WriteAllTextAsync(Path.Combine(directory, "creator-request.json"), EngineJson.Write(creatorRequest), cancellation);
        var preview = EditingEngine.CreatorSummary(creator);
        if (!args.Contains("--commit")) { Console.WriteLine(EngineJson.Write(new { committed = false, planFile, preview, firstFrameMs, stages })); return 0; }
        var commitResult = engine.ApplyCreator(creator.Key, cancellation);
        if (!commitResult.Committed) throw new ArgumentException(EngineJson.Write(commitResult));
        if (args.Contains("--verify-history"))
        {
            var expected = engine.Session.Snapshot.Document;
            if (!engine.Undo(engine.Session.Snapshot.Revision, cancellation).Success || !engine.Redo(engine.Session.Snapshot.Revision, cancellation).Success
                || !DocumentSemantics.Equal(expected, engine.Session.Snapshot.Document)) throw new InvalidOperationException("Creator undo/redo did not restore exact semantics.");
        }
        string project = Option("--save-as") ?? Path.Combine(directory, "creator.valencia");
        await engine.SaveAsync(project, cancellation);
        var reopened = await engine.OpenIntoAsync(project, engine.Session.Snapshot.Revision, cancellation);
        if (!reopened.Success) throw new InvalidOperationException(reopened.Message);
        var exports = await engine.ExportProfilesAsync(sequence, directory, cancellation, Option("--encoder"));
        var summary = new { committed = true, firstFrameMs, elapsedSeconds = timer.Elapsed.TotalSeconds, planFile, project, preview, stages, exports };
        await File.WriteAllTextAsync(Path.Combine(directory, "creator-result.json"), EngineJson.Write(summary), cancellation);
        Console.WriteLine(EngineJson.Write(summary));
        return 0;
    }
    private static async Task<AnalysisArtifact> Analyze(EditingEngine engine, AnalysisRequest request, CancellationToken cancellation)
    {
        var id = engine.Analysis.Start(request);
        while (engine.Analysis.Status(id).State == "running")
        {
            Console.Error.WriteLine(EngineJson.Write(new { stage = request.Kind, status = engine.Analysis.Status(id) }));
            await Task.Delay(250, cancellation);
        }
        var result = await engine.Analysis.WaitAsync(id, cancellation);
        if (result.State != "completed") throw new InvalidOperationException(result.Error ?? result.State);
        return await engine.Analysis.ResultAsync(result.Key, cancellation);
    }
}
