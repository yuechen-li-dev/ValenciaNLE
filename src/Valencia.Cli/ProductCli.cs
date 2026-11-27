using System.Text.Json;
using Valencia.Analysis;
using Valencia.Core;
using Valencia.Engine;
using Valencia.Persistence;

namespace Valencia.Cli;

internal static class ProductCli
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellation)
    {
        if (args is ["doctor"])
        {
            var diagnostic = await ProductDoctor.CheckAsync(cancellation: cancellation);
            Console.WriteLine(EngineJson.Write(diagnostic));
            return diagnostic.Checks.Any(c => c.Essential && !c.Available) ? 2 : 0;
        }
        if (args is ["update", "check"])
        {
            Console.WriteLine(EngineJson.Write(await CheckUpdateAsync(cancellation)));
            return 0;
        }
        if (args is ["models", "list"])
        {
            Console.WriteLine(EngineJson.Write(WhisperModels.List()));
            return 0;
        }
        if (args is ["models", "download", var model])
        {
            var installed = await WhisperModels.DownloadAsync(model, new DownloadProgress(), cancellation);
            Console.WriteLine(EngineJson.Write(installed));
            return 0;
        }
        if (args is ["models", "remove", var removeModel])
        {
            Console.WriteLine(EngineJson.Write(new { removed = WhisperModels.RemoveManaged(removeModel), model = removeModel }));
            return 0;
        }
        string root = Path.Combine(ValenciaData.Root, "Recovery");
        if (args is ["recovery", "list"])
        {
            Console.WriteLine(EngineJson.Write(await RecoveryRun.ScanAsync(root, cancellation)));
            return 0;
        }
        if (args is ["recovery", "inspect", var recovery])
        {
            var loaded = await RecoveryRun.OpenAsync(root, recovery, cancellation);
            Console.WriteLine(EngineJson.Write(new { loaded.Metadata, loaded.Integrity, loaded.MissingMedia }));
            return 0;
        }
        if (args is ["recovery", "recover", var id, "--output", var output])
        {
            var loaded = await RecoveryRun.OpenAsync(root, id, cancellation);
            using var lease = ProjectWriteLease.Acquire(output);
            await lease.SaveAsync(loaded.Snapshot, null, cancellation);
            Console.WriteLine(EngineJson.Write(new { output = lease.Path, loaded.Snapshot.Revision, loaded.MissingMedia }));
            return 0;
        }
        if (args is ["recovery", "discard", var discard])
        {
            RecoveryRun.Discard(root, discard);
            Console.WriteLine(EngineJson.Write(new { discarded = discard }));
            return 0;
        }
        throw new ArgumentException("Run doctor, models list/download/remove, recovery list/inspect/recover/discard, or update check.");
    }

    private static async Task<object> CheckUpdateAsync(CancellationToken cancellation)
    {
        const string releases = "https://github.com/yuechen-li-dev/ValenciaNLE/releases";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Valencia/" + ProductDoctor.Version);
        try
        {
            using var response = await client.GetAsync("https://api.github.com/repos/yuechen-li-dev/ValenciaNLE/releases/latest", cancellation);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new { current = ProductDoctor.Version, state = "no-public-stable-release", releases };
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellation), cancellationToken: cancellation);
            var tag = json.RootElement.GetProperty("tag_name").GetString()!;
            bool known = Version.TryParse(tag.TrimStart('v'), out var newest) &&
                         Version.TryParse(ProductDoctor.Version.Split('-')[0], out var current);
            if (!known) return new { current = ProductDoctor.Version, latest = tag, state = "unrecognized-version", releases };
            var installed = Version.Parse(ProductDoctor.Version.Split('-')[0]);
            bool update = newest! > installed || newest == installed && ProductDoctor.Version.Contains('-');
            return new { current = ProductDoctor.Version, latest = tag, state = update ? "update-available" : "current", releases };
        }
        catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            return new { current = ProductDoctor.Version, state = "unavailable", issue = error.Message, releases };
        }
    }

    private sealed class DownloadProgress : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress progress) =>
            Console.Error.WriteLine($"{progress.DownloadedBytes}/{progress.TotalBytes} bytes");
    }
}
