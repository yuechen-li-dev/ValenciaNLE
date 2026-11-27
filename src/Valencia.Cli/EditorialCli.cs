using System.Text.Json;
using Valencia.Engine;
using Valencia.Scripting;

namespace Valencia.Cli;

internal static class EditorialCli
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellation)
    {
        if (args is ["workflow", "list"])
        {
            Console.WriteLine(EngineJson.Write(EditorialWorkflows.Catalog));
            return 0;
        }
        if (args is ["workflow", "info", var name])
        {
            Console.WriteLine(EngineJson.Write(EditorialWorkflows.Info(name)));
            return 0;
        }
        if (args is not ["workflow", "prepare", var workflow, ..])
            throw new ArgumentException("Use workflow list, workflow info <id>, or workflow prepare <id>.");
        string path = Program.Option(args, "--settings") ?? throw new ArgumentException("Specify --settings.");
        if (new FileInfo(path).Length > 1_048_576) throw new ArgumentException("Editorial settings exceed 1 MiB.");
        var settings = EditorialWorkflows.ReadSettings(await File.ReadAllTextAsync(path, cancellation), path);
        await using var engine = await EditingEngine.OpenAsync(
            Program.Option(args, "--project") ?? throw new ArgumentException("Specify --project."), cancellation: cancellation);
        var snapshot = engine.Session.Snapshot;
        if (Program.Option(args, "--revision") is { } revision && EngineJson.Int64(revision) != snapshot.Revision)
            throw new ArgumentException("The saved project revision changed.");
        var result = await EditorialWorkflows.BuildAsync(engine, snapshot, workflow, settings, cancellation);
        if (result.Plan is null)
        {
            Console.WriteLine(EngineJson.Write(result));
            return 0;
        }
        var preview = engine.Plan(result.Plan, false, cancellation);
        if (preview.Valid && Program.Option(args, "--plan-out") is { } output)
        {
            await using var file = new FileStream(engine.OutputPath(output), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(file, result.Plan, EngineJson.Options, cancellation);
        }
        Console.WriteLine(EngineJson.Write(new { workflow, preview.Valid, preview.ExpectedRevision, preview.ResultingDuration,
            operations = result.Plan.Operations.Length, result.Diagnostics, result.Evidence }));
        return preview.Valid ? 0 : 2;
    }
}
