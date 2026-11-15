using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Media;
using Valencia.Scripting;
using Copeland.TS.Tson;
using Valencia.Analysis;
using System.Collections.Immutable;

namespace Valencia.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            if (args.Length == 0 || args[0] == "--help")
            {
                Console.WriteLine("Valencia.Cli project info|create <project> [--media file]\nValencia.Cli timeline list <project>\nValencia.Cli script run <script.ts> --project <project> [--save-as file]\nValencia.Cli plan validate|apply <plan.json|plan.obj.ts> --project <project> [--save-as file]\nValencia.Cli schema export\nValencia.Cli export <project> --sequence <id> --output <file>\nValencia.Cli mcp --root <directory> --project <relative.valencia>");
                Console.WriteLine("Valencia.Cli analyze waveform|silence|activity|scenes|audio <project> --asset <id> --stream <index> [--settings file.obj.ts]\nValencia.Cli roughcut plan <project> --analysis <key> [--sequence <id>] [--policy file.obj.ts] [--output plan.json]\nValencia.Cli roughcut apply <project> --plan <file> [--save-as <file>]\nValencia.Cli schema export --analysis");
                Console.WriteLine("Valencia.Cli transcribe <project> --asset <id> --stream <index> [--model tiny.en|large-v3] [--language en]\nValencia.Cli transcript show|search <project> --analysis <key> [--query phrase]\nValencia.Cli textedit plan <project> --analysis <key> --query <phrase> --mode retain|remove [--occurrence <id>] [--output plan.json]\nValencia.Cli textedit apply <project> --plan <file> [--save-as <file>]");
                Console.WriteLine("Valencia.Cli creator run <project> --asset <id> --stream <index> --output-dir <directory> [--keywords phrase] [--proxy] [--commit --verify-history]\nValencia.Cli creator plan <project> --request <file.obj.ts> --output <plan.json>\nValencia.Cli creator apply <project> --plan <plan.json> [--save-as file]\nValencia.Cli creator export <project> --output-dir <directory>\nValencia.Cli outputs list <project>\nValencia.Cli proxy create|index build|loudness analyze <project> --asset <id> --stream <index>\nValencia.Cli captions generate <project> --analysis <key> --output <plan.json>\nValencia.Cli transcript correct <project> --edits <file.obj.ts>\nTranscription alignment: --alignment native|dtw. Trusted Copeland scripts only; MCP exposes validated plans, never arbitrary code.");
                return 0;
            }
            if (args is ["schema", "export", ..])
            {
                Console.WriteLine(TypeProfileCompiler.CanonicalArtifact(args.Contains("--analysis") ? AnalysisContracts.Catalog : EditPlanSchema.Catalog));
                return 0;
            }
            if (args[0] == "obs") return await ObsCli.RunAsync(args, cancellation.Token);
            if (args[0] == "mcp")
            {
                var policy = new WorkspacePolicy(Required(args, "--root"));
                await using var serverEngine = await EditingEngine.OpenAsync(Required(args, "--project"), policy, cancellation.Token);
                await new StdioMcpServer(serverEngine, args.Contains("--allow-obs-control")).RunAsync(Console.In, Console.Out, cancellation.Token);
                return 0;
            }
            if (args is ["project", "create", var createPath, ..])
            {
                await using var created = EditingEngine.Create(Guid.NewGuid(), "Valencia project");
                var sequence = Guid.NewGuid();
                Require(created.Execute(new EditCommand(Guid.NewGuid(), 1,
                    new CreateSequence(sequence, "Main", new(int.Parse(Option(args, "--width") ?? "1280", System.Globalization.CultureInfo.InvariantCulture),
                        int.Parse(Option(args, "--height") ?? "720", System.Globalization.CultureInfo.InvariantCulture),
                        new(int.Parse(Option(args, "--fps-num") ?? "30", System.Globalization.CultureInfo.InvariantCulture),
                            int.Parse(Option(args, "--fps-den") ?? "1", System.Globalization.CultureInfo.InvariantCulture))), 48000), 0)));
                if (Option(args, "--media") is { } media)
                {
                    var imported = MediaAssetProbe.Import(Guid.NewGuid(), media);
                    Require(created.Execute(new EditCommand(Guid.NewGuid(), 1, new AddAsset(imported.Asset), created.Session.Snapshot.Revision)));
                    Require(created.Execute(new EditTransaction(Guid.NewGuid(), 1, created.Session.Snapshot.Revision,
                        [.. InsertionCommands.Build(created.Session.Snapshot, sequence, imported, default).Select(o => new EditCommand(Guid.NewGuid(), 1, o))])));
                }
                await created.SaveAsync(createPath, cancellation.Token);
                Console.WriteLine(EngineJson.Write(created.Session.Snapshot));
                return 0;
            }
            if (CreatorCli.Handles(args)) return await CreatorCli.RunAsync(args, cancellation.Token);
            string project = args[0] switch
            {
                "project" or "timeline" or "analyze" or "roughcut" or "transcript" or "textedit" when args.Length >= 3 => args[2],
                "transcribe" when args.Length >= 2 => args[1],
                "export" when args.Length >= 2 => args[1],
                _ => Required(args, "--project")
            };
            await using var engine = await EditingEngine.OpenAsync(project, cancellation: cancellation.Token);
            if (args[0] == "transcribe")
            {
                Guid asset = Guid.Parse(Required(args, "--asset"));
                int stream = int.Parse(Required(args, "--stream"));
                var request = new AnalysisRequest(asset, stream, "transcript", Transcription: new(Model: Option(args, "--model") ?? "tiny.en", Language: Option(args, "--language") ?? "en", Alignment: Option(args, "--alignment") ?? "native"));
                var job = engine.Analysis.Start(request);
                while (engine.Analysis.Status(job).State == "running")
                {
                    Console.Error.WriteLine(EngineJson.Write(engine.Analysis.Status(job)));
                    await Task.Delay(100, cancellation.Token);
                }
                var state = await engine.Analysis.WaitAsync(job, cancellation.Token);
                if (state.State != "completed") throw new InvalidOperationException(state.Error ?? state.State);
                var artifact = await engine.Analysis.ResultAsync(state.Key, cancellation.Token);
                Console.WriteLine(EngineJson.Write(new { job = state, result = AnalysisContracts.Page(artifact, "words") }));
                return 0;
            }
            if (args is ["transcript", var operation, ..])
            {
                if (operation is not ("show" or "search")) throw new ArgumentException("Unknown transcript operation.");
                var artifact = await engine.Analysis.ResultAsync(Required(args, "--analysis"), cancellation.Token);
                int offset = int.Parse(Option(args, "--offset") ?? "0");
                int limit = int.Parse(Option(args, "--limit") ?? "64");
                object result = operation == "search"
                    ? TranscriptQueries.Search(artifact, Required(args, "--query"), offset: offset, limit: limit)
                    : AnalysisContracts.Page(artifact, Option(args, "--kind") ?? "segments", offset, limit);
                Console.WriteLine(EngineJson.Write(result));
                return 0;
            }
            if (args is ["textedit", "plan", ..])
            {
                string key = Required(args, "--analysis");
                var artifact = await engine.Analysis.ResultAsync(key, cancellation.Token);
                var ids = TranscriptQueries.UniqueMatch(artifact, Required(args, "--query")).WordIds;
                var sequence = Option(args, "--sequence") is { } seq ? Guid.Parse(seq) : engine.Query().Sequences[0].Id;
                var occurrences = Option(args, "--occurrence") is { } occurrence ? ImmutableArray.Create(Guid.Parse(occurrence)) : (ImmutableArray<Guid>?)null;
                var proposal = await engine.TextEditAsync(new(sequence, key, Option(args, "--mode") ?? "retain", ids, occurrences,
                    int.Parse(Option(args, "--padding-ms") ?? "0", System.Globalization.CultureInfo.InvariantCulture)), cancellation.Token);
                if (proposal.Plan is not null && Option(args, "--output") is { } output) await File.WriteAllTextAsync(output, EngineJson.Write(proposal.Plan), cancellation.Token);
                Console.WriteLine(EngineJson.Write(proposal));
                return proposal.Preview?.Valid == true ? 0 : 2;
            }
            if (args is ["textedit", "apply", ..])
            {
                var plan = EditPlanSchema.Parse(await File.ReadAllTextAsync(Required(args, "--plan"), cancellation.Token));
                var result = engine.Plan(plan, true, cancellation.Token);
                if (result.Committed && Option(args, "--save-as") is { } saved) await engine.SaveAsync(saved, cancellation.Token);
                Console.WriteLine(EngineJson.Write(result));
                return result.Valid ? 0 : 2;
            }
            if (args is ["analyze", var analysisKind, ..])
            {
                var request = Option(args, "--settings") is { } settingsFile
                    ? AnalysisContracts.Parse<AnalysisRequest>(await File.ReadAllTextAsync(settingsFile, cancellation.Token), "AnalysisRequest", settingsFile)
                    : new AnalysisRequest(Guid.Parse(Required(args, "--asset")), int.Parse(Required(args, "--stream")), analysisKind == "scenes" ? "scenes" : "audio");
                if (analysisKind is not ("waveform" or "silence" or "activity" or "scenes" or "audio")) throw new ArgumentException("Unknown analysis kind.");
                var id = engine.Analysis.Start(request);
                while (engine.Analysis.Status(id).State == "running")
                {
                    var status = engine.Analysis.Status(id);
                    Console.Error.WriteLine(EngineJson.Write(status));
                    await Task.Delay(100, cancellation.Token);
                }
                var completed = await engine.Analysis.WaitAsync(id, cancellation.Token);
                if (completed.State != "completed") throw new InvalidOperationException(completed.Error ?? completed.State);
                var artifact = await engine.Analysis.ResultAsync(completed.Key, cancellation.Token);
                Console.WriteLine(EngineJson.Write(new { job = completed, result = AnalysisContracts.Page(artifact,
                    analysisKind == "audio" ? "summary" : analysisKind, limit: 64) }));
                return 0;
            }
            if (args is ["roughcut", "plan", ..])
            {
                string key = Required(args, "--analysis");
                var policy = Option(args, "--policy") is { } policyFile
                    ? AnalysisContracts.Parse<RoughCutPolicy>(await File.ReadAllTextAsync(policyFile, cancellation.Token), "RoughCutPolicy", policyFile)
                    : new RoughCutPolicy();
                var sequence = Option(args, "--sequence") is { } sequenceId ? Guid.Parse(sequenceId) : engine.Query().Sequences.First().Id;
                var proposal = await engine.RoughCutAsync(sequence, key, policy, cancellation.Token);
                if (Option(args, "--output") is { } planOutput && proposal.Plan is not null)
                    await File.WriteAllTextAsync(planOutput, EngineJson.Write(proposal.Plan), cancellation.Token);
                Console.WriteLine(EngineJson.Write(proposal));
                return proposal.Valid ? 0 : 2;
            }
            if (args is ["roughcut", "apply", ..])
            {
                string planFile = Required(args, "--plan");
                var plan = EditPlanSchema.Parse(await File.ReadAllTextAsync(planFile, cancellation.Token), planFile);
                var applied = engine.Plan(plan, true, cancellation.Token);
                if (applied.Committed && Option(args, "--save-as") is { } destination) await engine.SaveAsync(destination, cancellation.Token);
                Console.WriteLine(EngineJson.Write(applied));
                return applied.Valid ? 0 : 2;
            }
            if (args is ["project", "info", ..])
            {
                Console.WriteLine(EngineJson.Write(new { apiVersion = 1, project = engine.Session.Snapshot, missingMedia = engine.MissingMedia() }));
                return 0;
            }
            if (args is ["timeline", "list", ..])
            {
                Console.WriteLine(EngineJson.Write(engine.Query().Sequences));
                return 0;
            }
            if (args is ["plan", var mode, var planPath, ..] && mode is "validate" or "apply")
            {
                var plan = EditPlanSchema.Parse(await File.ReadAllTextAsync(planPath, cancellation.Token), planPath);
                var result = engine.Plan(plan, mode == "apply", cancellation.Token);
                if (result.Committed && Option(args, "--save-as") is { } destination) await engine.SaveAsync(destination, cancellation.Token);
                Console.WriteLine(EngineJson.Write(result));
                if (!result.Valid) Console.Error.WriteLine(string.Join("\n", result.Diagnostics));
                return result.Valid ? 0 : 2;
            }
            if (args is ["script", "run", var script, ..])
            {
                using var host = new CopelandScriptHost();
                var result = await host.RunFileAsync(script, engine, Option(args, "--entry") ?? "run", cancellation.Token);
                if (result.Success && Option(args, "--save-as") is { } destination) await engine.SaveAsync(destination, cancellation.Token);
                Console.WriteLine(EngineJson.Write(result));
                if (!result.Success) Console.Error.WriteLine(EngineJson.Write(result.Diagnostics));
                return result.Success ? 0 : 3;
            }
            if (args[0] == "export")
            {
                var job = engine.Exports.Start(Guid.Parse(Required(args, "--sequence")), Required(args, "--output"));
                var status = await engine.Exports.WaitAsync(job, cancellation.Token);
                Console.WriteLine(EngineJson.Write(status));
                return status.State == "completed" ? 0 : 4;
            }
            throw new ArgumentException("Unknown command; use --help.");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(EngineJson.Write(new { code = error is OperationCanceledException ? "CANCELLED" : "REQUEST_FAILED", message = error.Message }));
            return error is OperationCanceledException ? 130 : 1;
        }
    }
    public static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    private static string Required(string[] args, string name) => Option(args, name) ?? throw new ArgumentException("Missing " + name);
    private static void Require(EditResult result)
    {
        if (!result.Success) throw new InvalidOperationException(result.Message);
    }
}
