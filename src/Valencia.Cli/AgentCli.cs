using System.Text;
using System.Text.Json;
using Valencia.Engine;

namespace Valencia.Cli;

internal static class AgentCli
{
    public static async Task<int> RunAsync(string[] arguments, CancellationToken cancellation)
    {
        if (arguments.Length < 3 || arguments[1] is not ("call" or "run"))
            throw new ArgumentException("Use agent call <method> --project <path> [--request file.json] or agent run <project> [--requests file.jsonl].");
        string? Option(string name)
        {
            int index = Array.IndexOf(arguments, name);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }
        string path = arguments[1] == "run" ? arguments[2] : Option("--project") ?? throw new ArgumentException("Specify --project.");
        await using var engine = await EditingEngine.OpenAsync(path, cancellation: cancellation).ConfigureAwait(false);
        if (arguments[1] == "call")
        {
            string request = Option("--request") is { } file ? await File.ReadAllTextAsync(file, cancellation).ConfigureAwait(false) : "{}";
            using var json = JsonDocument.Parse(request);
            var result = await engine.AgentAsync(arguments[2], json.RootElement, cancellation).ConfigureAwait(false);
            Console.WriteLine(EngineJson.Write(result));
            return result.Ok ? 0 : 2;
        }

        using var inputFile = Option("--requests") is { } requests ? File.OpenText(requests) : null;
        TextReader input = inputFile is not null ? inputFile : Console.In;
        bool failed = false;
        while (await ReadBoundedLine(input, cancellation).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            AgentReply result;
            try
            {
                using var envelope = JsonDocument.Parse(line);
                var json = envelope.RootElement;
                string method = json.GetProperty("method").GetString()!;
                result = await engine.AgentAsync(method, json.GetProperty("request"), cancellation).ConfigureAwait(false);
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                result = new(AgentContracts.Version, engine.Session.Snapshot.Revision, Issue: new("INVALID_REQUEST", e.Message,
                    "Supply one {method, request} JSON object per line."));
            }
            Console.WriteLine(EngineJson.Write(result));
            failed |= !result.Ok;
        }
        return failed ? 2 : 0;
    }

    private static async Task<string?> ReadBoundedLine(TextReader reader, CancellationToken cancellation)
    {
        var line = new StringBuilder();
        var next = new char[1];
        while (await reader.ReadAsync(next, cancellation).ConfigureAwait(false) != 0)
        {
            if (next[0] == '\n') return line.ToString();
            if (line.Length >= 1_048_576) throw new InvalidDataException("CLI agent request exceeds 1 MiB.");
            line.Append(next[0]);
        }
        return line.Length == 0 ? null : line.ToString();
    }
}
