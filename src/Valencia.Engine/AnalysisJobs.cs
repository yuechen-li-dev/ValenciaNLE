using Valencia.Analysis;
using System.Diagnostics;
using Valencia.Media;

namespace Valencia.Engine;

public sealed record AnalysisRequest(Guid AssetId, int StreamIndex, string Kind, AudioSettings? Audio = null, SceneSettings? Scenes = null,
    TranscriptionSettings? Transcription = null, OcrSettings? Ocr = null);
public sealed record AnalysisStatus(Guid JobId, Guid AssetId, string Key, string State, double Progress, long ProcessedUnits, bool CacheHit, string? Error,
    double CacheReadMs = 0, double AnalysisMs = 0, double CacheWriteMs = 0)
{
    public AgentIssue? Issue => EditingEngine.JobIssue(State, Error, JobId);
}

/// <summary>Focused analysis queue: one decode pass, equivalent-request deduplication, 32 retained status records.</summary>
public sealed class AnalysisJobs(EditingEngine engine, AnalysisCache cache) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Job> jobs = [];
    private bool disposed;
    public AnalysisCache Cache => cache;
    public Guid Start(AnalysisRequest request)
    {
        var asset = engine.Query().Assets.First(a => a.Id == request.AssetId);
        engine.Policy?.Check(asset.SourcePath);
        var audio = request.Audio ?? new AudioSettings();
        var scenes = request.Scenes ?? new SceneSettings();
        if (request.Kind is not ("audio" or "scenes" or "transcript" or "loudness" or "proxy" or "index" or "ocr")) throw new ArgumentException("Unknown analysis kind.");
        var transcription = request.Transcription ?? new TranscriptionSettings();
        transcription.Validate();
        audio.Validate();
        scenes.Validate();
        string fingerprint = AnalysisCache.Fingerprint(asset.SourcePath);
        string parameters = request.Kind == "transcript" ? AnalysisJson.Write(transcription) : request.Kind == "audio" ? AnalysisJson.Write(audio) : AnalysisJson.Write(scenes);
        string algorithm = request.Kind == "transcript" ? WhisperTranscriber.Algorithm : request.Kind == "audio" ? MediaAnalyzer.AudioAlgorithm : request.Kind == "loudness" ? "FFmpeg7.1/loudnorm/BS.1770-4/v1" : request.Kind is "proxy" or "index" ? "media-resource-v1/" + request.Kind : MediaAnalyzer.SceneAlgorithm;
        var ocr = request.Ocr ?? new OcrSettings();
        OcrProvider? provider = null;
        if (request.Kind == "ocr")
        {
            ocr.Validate();
            provider = OcrProvider.Open(ocr.Language);
            algorithm = provider.Algorithm;
            parameters = AnalysisJson.Write(new { ocr, provider.ExecutableSha256, provider.ModelSha256 });
        }
        string key = AnalysisCache.Key(asset.Id, fingerprint, request.StreamIndex, algorithm, parameters);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var equivalent = jobs.FirstOrDefault(p => p.Value.Status.Key == key && !p.Value.Work.IsCompleted);
            if (equivalent.Value is not null) return equivalent.Key;
            if (jobs.Values.Any(j => !j.Work.IsCompleted)) throw new InvalidOperationException("Another analysis is running; wait or cancel it.");
            while (jobs.Count >= 32)
            {
                var first = jobs.First();
                first.Value.Cancellation.Dispose();
                jobs.Remove(first.Key);
            }
            Guid id = Guid.NewGuid();
            var job = new Job(new(id, asset.Id, key, "running", 0, 0, false, null));
            jobs.Add(id, job);
            job.Work = Task.Run(async () =>
            {
                try
                {
                    var token = job.Cancellation.Token;
                    var timer = Stopwatch.StartNew();
                    var artifact = await cache.ReadAsync(key, token).ConfigureAwait(false);
                    if (request.Kind == "index") artifact = null; // The native decoder/sample index is process-local.
                    if (request.Kind == "proxy" && artifact?.Proxy is { } proxy && MediaProxy.Resolve(asset with { ProxyKey = proxy.Key }, asset.Streams.First(s => s.Index == request.StreamIndex)) is null) artifact = null;
                    double readMs = timer.Elapsed.TotalMilliseconds, analysisMs = 0, writeMs = 0;
                    bool hit = artifact is not null;
                    if (artifact is null)
                    {
                        var progress = new ProgressSink(p => { lock (gate) job.Status = job.Status with { Progress = p.Fraction, ProcessedUnits = p.ProcessedUnits }; });
                        timer.Restart();
                        artifact = request.Kind switch
                        {
                            "audio" => MediaAnalyzer.Audio(asset, request.StreamIndex, audio, key, fingerprint, progress, token),
                            "scenes" => MediaAnalyzer.Scenes(asset, request.StreamIndex, scenes, key, fingerprint, progress, token),
                            "loudness" => LoudnessAnalyzer.Source(asset, request.StreamIndex, key, fingerprint, token),
                            "ocr" => await TesseractOcr.RunAsync(asset, request.StreamIndex, ocr, provider!, key, fingerprint, parameters, progress, token).ConfigureAwait(false),
                            "proxy" or "index" => MediaResourceAnalyzer.Run(asset, request.StreamIndex, request.Kind, key, fingerprint, token),
                            _ => WhisperTranscriber.Run(asset, request.StreamIndex, transcription, key, fingerprint, cache, progress, token)
                        };
                        analysisMs = timer.Elapsed.TotalMilliseconds;
                        token.ThrowIfCancellationRequested();
                        if (AnalysisCache.Fingerprint(asset.SourcePath) != fingerprint) throw new IOException("Source changed during analysis.");
                        timer.Restart();
                        await cache.PublishAsync(artifact, token).ConfigureAwait(false);
                        writeMs = timer.Elapsed.TotalMilliseconds;
                    }
                    lock (gate) job.Status = job.Status with { State = "completed", Progress = 1, ProcessedUnits = artifact.ProcessedUnits, CacheHit = hit,
                        CacheReadMs = readMs, AnalysisMs = analysisMs, CacheWriteMs = writeMs };
                }
                catch (OperationCanceledException) { lock (gate) job.Status = job.Status with { State = "cancelled" }; }
                catch (Exception error) { lock (gate) job.Status = job.Status with { State = "failed", Error = error.Message }; }
            });
            return id;
        }
    }
    public AnalysisStatus Status(Guid id) { lock (gate) return jobs[id].Status; }
    public AnalysisStatus[] ActiveJobs() { lock (gate) return jobs.Values.Select(j => j.Status).Where(s => s.State == "running").ToArray(); }
    public void Cancel(Guid id) { lock (gate) jobs[id].Cancellation.Cancel(); }
    public async Task<AnalysisStatus> WaitAsync(Guid id, CancellationToken cancellation = default)
    {
        Task work;
        lock (gate) work = jobs[id].Work;
        await work.WaitAsync(cancellation).ConfigureAwait(false);
        return Status(id);
    }
    public async Task<AnalysisArtifact> ResultAsync(string key, CancellationToken cancellation = default)
    {
        var result = await cache.ReadAsync(key, cancellation).ConfigureAwait(false) ?? throw new FileNotFoundException("Analysis artifact absent, evicted or corrupt; analyze again.");
        var asset = engine.Query().Assets.First(a => a.Id == result.AssetId);
        engine.Policy?.Check(asset.SourcePath);
        if (AnalysisCache.Fingerprint(asset.SourcePath) != result.Fingerprint) throw new InvalidDataException("Analysis is stale: source fingerprint changed.");
        return result;
    }
    public async ValueTask DisposeAsync()
    {
        Job[] remaining;
        lock (gate)
        {
            disposed = true;
            remaining = [.. jobs.Values];
            foreach (var job in remaining) job.Cancellation.Cancel();
        }
        await Task.WhenAll(remaining.Select(j => j.Work)).ConfigureAwait(false);
        foreach (var job in remaining) job.Cancellation.Dispose();
    }
    private sealed class Job(AnalysisStatus status)
    {
        public AnalysisStatus Status = status;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Work = Task.CompletedTask;
    }
    private sealed class ProgressSink(Action<AnalysisProgress> callback) : IProgress<AnalysisProgress>
    {
        public void Report(AnalysisProgress value) => callback(value);
    }
}
