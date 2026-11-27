using System.Diagnostics;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Media;

if (args.Length < 2) throw new ArgumentException("Usage: Valencia.Qualification <source> <output-directory>");
string source = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
using var stopping = new CancellationTokenSource(TimeSpan.FromMinutes(10));
var token = stopping.Token;
var importedAt = Stopwatch.StartNew();
var imported = MediaAssetProbe.Import(Guid.NewGuid(), source);
double importMs = importedAt.Elapsed.TotalMilliseconds;
var video = imported.Asset.Streams.First(s => s.Kind == TrackKind.Video);
Guid sequenceId = Guid.NewGuid();
await using var engine = EditingEngine.Create(Guid.NewGuid(), "M2e source qualification");
void Apply(EditOperation operation)
{
    var result = engine.Execute(new EditCommand(Guid.NewGuid(), 1, operation, engine.Session.Snapshot.Revision), token);
    if (!result.Success) throw new InvalidOperationException(result.Message);
}
Apply(new CreateSequence(sequenceId, "Source", new(video.Width!.Value, video.Height!.Value, video.FrameRate ?? new(60, 1)), 48000));
Apply(new AddAsset(imported.Asset));
var transaction = engine.Execute(new EditTransaction(Guid.NewGuid(), 1, engine.Session.Snapshot.Revision,
    [.. InsertionCommands.Build(engine.Session.Snapshot, sequenceId, imported, default).Select(o => new EditCommand(Guid.NewGuid(), 1, o))]), token);
if (!transaction.Success) throw new InvalidOperationException(transaction.Message);
var snapshot = engine.Session.Snapshot;
var sequence = engine.Query().Sequence(sequenceId);
var firstVideo = sequence.Tracks.First(t => t.Kind == TrackKind.Video).Clips[0];
if (Array.IndexOf(args, "--slice-project") is int sliceOption && sliceOption >= 0)
{
    var start = new SequenceTime(7201 * SequenceTime.TicksPerSecond);
    var sixSeconds = new SequenceTime(6 * SequenceTime.TicksPerSecond);
    foreach (var clip in sequence.Tracks.SelectMany(track => track.Clips))
    {
        var from = clip.MapToSource(start);
        Apply(new TrimClip(sequenceId, clip.Id, default, from, from.Shift(sixSeconds)));
    }
    Apply(new PatchClipColor(sequenceId, firstVideo.Id, new(Exposure: .14, Contrast: 1.07)));
    var second = MediaAssetProbe.Import(Guid.NewGuid(), Path.GetFullPath(args[sliceOption + 1]));
    var secondVideo = second.Asset.Streams.First(item => item.Kind == TrackKind.Video);
    var secondStart = new SourcePosition(secondVideo.Start ?? second.ContainerStart);
    Apply(new AddAsset(second.Asset));
    var main = sequence.Tracks.First(track => track.Kind == TrackKind.Video);
    Apply(new InsertClip(sequenceId, main.Id, new(Guid.NewGuid(), second.Asset.Id, secondVideo.Index,
        new(13 * SequenceTime.TicksPerSecond / 2), secondStart, secondStart.Shift(new(2 * SequenceTime.TicksPerSecond)), new("Second source"))));
    Guid overlay = Guid.NewGuid();
    Apply(new AddTrack(sequenceId, overlay, TrackKind.Video, main.Order + 1, "Qualification overlay"));
    Apply(new InsertClip(sequenceId, overlay, new(Guid.NewGuid(), second.Asset.Id, secondVideo.Index,
        new(SequenceTime.TicksPerSecond / 2), secondStart, secondStart.Shift(new(3 * SequenceTime.TicksPerSecond)),
        new("Fractional effect", Opacity: .4m, Video: new(Scale: .8, Saturation: .8, Region: new("pixelate", .1, .1, .4, .35))))));
    var mic = engine.Query().Sequence(sequenceId).Tracks.First(track => track.Kind == TrackKind.Audio).Clips[0];
    var caption = new CaptionWord(Guid.NewGuid(), mic.AssetId, mic.StreamIndex,
        mic.SourceIn.Shift(new(SequenceTime.TicksPerSecond)), mic.SourceIn.Shift(new(3 * SequenceTime.TicksPerSecond)),
        "Qualification");
    Apply(new SetCreatorSettings(sequenceId, new(1, [caption], new(), [], [])));
    await engine.SaveAsync(Path.Combine(output, "source.valencia"), token);
    return;
}
if (args.Contains("--project-only"))
{
    await engine.SaveAsync(Path.Combine(output, "source.valencia"), token);
    return;
}
if (args.Contains("--audio-index"))
{
    var measurements = new List<object>();
    foreach (var stream in imported.Asset.Streams.Where(s => s.Kind == TrackKind.Audio))
    {
        var timer = Stopwatch.StartNew();
        var index = MediaIndex.Build(imported.Asset, stream.Index, token);
        measurements.Add(new { index, elapsedMs = timer.Elapsed.TotalMilliseconds });
    }
    await File.WriteAllTextAsync(Path.Combine(output, "audio-index.json"), EngineJson.Write(measurements), token);
    return;
}
if (args.Contains("--export-comparison"))
{
    var measurements = new List<object>();
    var end = new SequenceTime(Math.Min(sequence.Duration.Ticks, 8 * SequenceTime.TicksPerSecond));
    Environment.SetEnvironmentVariable("VALENCIA_VIDEO_DECODER", "software");
    foreach (int pass in new[] { 0, 1 })
    foreach (string backend in pass == 0 ? new[] { "h264", "h264-nvenc" } : new[] { "h264-nvenc", "h264" })
    {
        var encoder = ExportCatalog.Encoder(backend);
        encoder = encoder with { Options = ["-preset", backend == "h264" ? "veryfast" : "p4", "-b:v", "12M", "-g", "120"] };
        var result = await SequenceExport.RunAsync(snapshot, sequenceId, Path.Combine(output, $"{backend}-{pass}.mp4"),
            cancellation: token, range: (default, end), encoder: encoder);
        measurements.Add(new { pass, result });
    }
    await File.WriteAllTextAsync(Path.Combine(output, "export-comparison.json"), EngineJson.Write(new
    {
        source, imported.Asset, measurements,
        scope = "Same 8-second source range, software decode, output size, 12 Mb/s target, GOP, AAC and render path. Encoder speed/rate control algorithms differ."
    }), token);
    return;
}
if (args.Contains("--proxy-comparison"))
{
    string cache = Path.Combine(output, "proxies");
    Environment.SetEnvironmentVariable("VALENCIA_PROXY_DIR", cache);
    Environment.SetEnvironmentVariable("VALENCIA_PREVIEW_PROXY", "always");
    var timer = Stopwatch.StartNew();
    var proxy = await MediaProxy.GenerateAsync(imported.Asset, video.Index, token);
    double buildMs = timer.Elapsed.TotalMilliseconds;
    timer.Restart();
    var reopened = await MediaProxy.GenerateAsync(imported.Asset, video.Index, token);
    double reuseMs = timer.Elapsed.TotalMilliseconds;
    if (reopened != proxy) throw new InvalidDataException("Proxy generation did not reuse the published file.");
    var measurements = new List<object>();
    var randomSeek = new Random(20261127);
    using var original = new SequenceEvaluator(snapshot, sequenceId, retainVideoHistory: true);
    using var preview = new SequenceEvaluator(snapshot, sequenceId, useProxies: true, retainVideoHistory: true);
    for (int index = 0; index < 40; index++)
    {
        var at = new SequenceTime(firstVideo.Start.Ticks + (long)(randomSeek.NextDouble() *
            Math.Max(0, firstVideo.Duration.Ticks - SequenceTime.TicksPerSecond / 4)));
        timer.Restart();
        var sourceFrame = original.Video(at, token);
        double originalMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var proxyFrame = preview.Video(at, token);
        double proxyMs = timer.Elapsed.TotalMilliseconds;
        if (!proxyFrame.PrimaryUsesProxy || !sourceFrame.SourceTimestamps!.Value.SequenceEqual(proxyFrame.SourceTimestamps!.Value))
            throw new InvalidDataException($"Proxy changed the selected picture timestamp at {at.TotalSeconds:F6}s.");
        measurements.Add(new { index, at, originalMs, proxyMs, proxyFrame.SourceTimestamps });
    }
    await File.WriteAllTextAsync(Path.Combine(output, "proxy-comparison.json"), EngineJson.Write(new
    {
        source, cache, proxy, buildMs, reuseMs, measurements,
        scope = "Source-sized software composition with matched original and proxy cursor requests. UI latency is measured in the native smoke."
    }), token);
    return;
}
if (args.Contains("--decode-comparison"))
{
    var capability = VideoDecodeProvider.Probe();
    var measurements = new List<object>();
    var seekRandom = new Random(20261127);
    // Time selection is deliberately independent of logical frame rate and exercises both directions.
    var positions = new[] { 0L }.Concat(Enumerable.Range(0, 40).Select(_ =>
        (long)(seekRandom.NextDouble() * Math.Max(0, firstVideo.Duration.Ticks - SequenceTime.TicksPerSecond / 4))));
    using var software = new SequenceEvaluator(snapshot, sequenceId, useHardwareDecode: false);
    using var hardware = new SequenceEvaluator(snapshot, sequenceId, useHardwareDecode: true);
    byte[]? referencePixels = null, selectedPixels = null;
    int query = 0;
    foreach (long ticks in positions)
    {
        var time = new SequenceTime(firstVideo.Start.Ticks + ticks);
        var timer = Stopwatch.StartNew();
        var reference = software.Video(time, token);
        double softwareMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var selected = hardware.Video(time, token);
        double hardwareMs = timer.Elapsed.TotalMilliseconds;
        if (reference.VideoClips.Length != 1 || reference.SourceTimestamps is not { Length: 1 }
            || selected.SourceTimestamps is not { Length: 1 }
            || !reference.SourceTimestamps.Value.SequenceEqual(selected.SourceTimestamps.Value))
            throw new InvalidDataException($"Timestamp mismatch after seek at {time.TotalSeconds:F6}s.");
        var (maximum, mean, high) = ComparePixels(reference.Rgba, selected.Rgba);
        measurements.Add(new { request = query++, ticks, softwareMs, hardwareMs, selected.SourceTimestamps, selected.PrimaryDecode,
            referenceFormat = reference.PrimaryDecode, maximumRgbDelta = maximum, meanRgbDelta = mean, channelsAbove8 = high });
        referencePixels ??= reference.Rgba;
        selectedPixels ??= selected.Rgba;
    }
    await File.WriteAllBytesAsync(Path.Combine(output, "first-software.rgba"), referencePixels!, token);
    await File.WriteAllBytesAsync(Path.Combine(output, "first-d3d11-request.rgba"), selectedPixels!, token);
    await File.WriteAllTextAsync(Path.Combine(output, "decode-comparison.json"), EngineJson.Write(new
    {
        source, imported.Asset, capability, importMs, measurements, runtime = Environment.Version.ToString(),
        peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
        scope = "Paired source-size seek requests on two independent authoritative evaluators. Times exclude pixel comparison and UI."
    }), token);
    return;
}
if (args.Contains("--scrub-seeks"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Audio-device seek comparison requires Windows.");
    var measurements = new List<object>();
    using var preview = new SequencePreview();
    foreach (string mode in new[] { "audio-video-open", "video-only-preview" })
    foreach (int pass in new[] { 0, 1 })
    foreach (double fraction in new[] { .8, .1, .6, 0, .4, .2 })
    {
        // Bounded span also allows the same comparison on multi-hour sources without an obligatory full audio index scan.
        long ticks = checked((long)(Math.Min(sequence.Duration.Ticks - 1, 20 * SequenceTime.TicksPerSecond) * fraction));
        var at = new SequenceTime(Math.Max(0, ticks));
        var timer = Stopwatch.StartNew();
        var player = mode == "audio-video-open"
            ? await SequencePlayback.OpenAsync(snapshot, sequenceId, at, token)
            : SequencePlayback.OpenPreview(preview, snapshot, sequenceId, at, token);
        double elapsedMs = timer.Elapsed.TotalMilliseconds;
        if (player.Current?.Time != at) throw new InvalidOperationException("Seek returned the wrong timeline position.");
        timer.Restart(); await player.DisposeAsync();
        var result = new { mode, pass, ticks, elapsedMs, disposeMs = timer.Elapsed.TotalMilliseconds };
        measurements.Add(result); Console.Error.WriteLine(EngineJson.Write(result));
    }
    await File.WriteAllTextAsync(Path.Combine(output, "scrub-seeks.json"), EngineJson.Write(new
    {
        source, imported.Asset, measurements, runtime = Environment.Version.ToString(),
        cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
        peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
        scope = "Same source/snapshot/timestamps. Full player audio prebuffer/device versus reusable paused video evaluator. Decode-ready CPU latency; excludes UI/presentation. OS caches not flushed."
    }), token);
    return;
}
if (args.Contains("--audio-seeks"))
{
    var measurements = new List<object>();
    using var evaluator = new SequenceEvaluator(snapshot, sequenceId);
    // Repeat exactly the same positions to separate progressive index construction
    // from warm random seeking. All three independent OBS audio streams are mixed.
    foreach (int pass in new[] { 0, 1 })
    {
        foreach (int second in new[] { 690, 5952, 2308, 5041, 1313, 5913 })
        {
            var at = new SequenceTime(checked(second * SequenceTime.TicksPerSecond));
            var timer = Stopwatch.StartNew();
            var pcm = evaluator.Audio(at, 0, 2048, token);
            var measurement = new { pass, second, elapsedMs = timer.Elapsed.TotalMilliseconds,
                sampleCount = pcm.Length, finite = pcm.All(float.IsFinite),
                pcmSha256 = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(pcm.AsSpan()))) };
            measurements.Add(measurement);
            Console.Error.WriteLine(EngineJson.Write(measurement));
            await File.WriteAllTextAsync(Path.Combine(output, "audio-seeks.json"), EngineJson.Write(new
            {
                source, imported.Asset, measurements,
                peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                scope = "Fresh process; progressive sample-count index; repeated positions; OS caches not flushed; CPU PCM, no audio device"
            }), token);
        }
    }
    return;
}
if (args.Contains("--resources"))
{
    // A fresh artifact identity and proxy directory distinguish artifact misses from
    // reuse of an existing encoded proxy. Existing model weights are never downloaded.
    Environment.SetEnvironmentVariable("VALENCIA_PROXY_DIR", Path.Combine(output, "proxies"));
    var measurements = new List<object>();
    var audio = imported.Asset.Streams.First(s => s.Kind == TrackKind.Audio);
    foreach (var request in new AnalysisRequest[] { new(imported.Asset.Id, audio.Index, "audio"),
        new(imported.Asset.Id, audio.Index, "transcript", Transcription: new(Alignment: "dtw")),
        new(imported.Asset.Id, video.Index, "proxy"), new(imported.Asset.Id, audio.Index, "audio") })
    {
        var timer = Stopwatch.StartNew();
        Guid id = engine.Analysis.Start(request);
        var status = await engine.Analysis.WaitAsync(id, token);
        if (status.State != "completed") throw new InvalidOperationException(status.Error ?? status.State);
        var artifact = await engine.Analysis.ResultAsync(status.Key, token);
        measurements.Add(new { request.Kind, elapsedMs = timer.Elapsed.TotalMilliseconds, status,
            proxyBytes = artifact.Proxy?.Bytes, artifactBytes = Directory.GetFiles(engine.Analysis.Cache.DirectoryPath, status.Key + "*")
                .Sum(path => new FileInfo(path).Length), transcriptWords = artifact.Transcript?.Words.Length });
    }
    if (engine.Session.Snapshot != snapshot) throw new InvalidOperationException("Resource analysis mutated the editing snapshot.");
    await File.WriteAllTextAsync(Path.Combine(output, "resources.json"), EngineJson.Write(new
    {
        source, imported.Duration, measurements, peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
        scope = "Fresh analysis identities/proxy directory; existing local model, OS file caches not flushed; source/project not mutated"
    }), token);
    return;
}
var first = Stopwatch.StartNew();
using (var initial = new SequenceEvaluator(snapshot, sequenceId)) _ = initial.Video(firstVideo.Start, token);
double firstDecodeMs = first.Elapsed.TotalMilliseconds;
Console.Error.WriteLine(EngineJson.Write(new { stage = "opened", importMs, firstDecodeMs, imported.Duration }));
var random = new Random(20261115);
var seeking = new List<object>();
var sequential = new List<double>();
using (var evaluator = new SequenceEvaluator(snapshot, sequenceId))
{
    for (int index = 0; index < 40; index++)
    {
        long ticks = firstVideo.Start.Ticks + (long)((decimal)(firstVideo.Duration.Ticks - SequenceTime.TicksPerSecond / 10) * random.Next(0, 100000) / 100000);
        var at = new SequenceTime(ticks);
        Console.Error.WriteLine(EngineJson.Write(new { stage = "seek", index, requestedTicks = EngineJson.Exact(ticks) }));
        var timer = Stopwatch.StartNew();
        var frame = evaluator.Video(at, token);
        double videoMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        _ = evaluator.Audio(at, 0, 2048, token);
        var measured = new { requestedTicks = EngineJson.Exact(ticks), videoMs, audioMs = timer.Elapsed.TotalMilliseconds,
            evaluator.ActiveVideoDecodedFrames, frame.SourceTimestamps };
        seeking.Add(measured);
        Console.Error.WriteLine(EngineJson.Write(new { stage = "seek-completed", index, measured }));
    }
    for (int index = 0; index < 120; index++)
    {
        var at = new SequenceTime(firstVideo.Start.Ticks + TimelineTime.FromFrames(index, sequence.Video.FrameRate).Ticks);
        if (at.Ticks >= sequence.Duration.Ticks) break;
        var timer = Stopwatch.StartNew();
        _ = evaluator.Video(at, token);
        sequential.Add(timer.Elapsed.TotalMilliseconds);
    }
}
var profile = new OutputProfile(Guid.NewGuid(), "Identity", sequence.Video.Width, sequence.Video.Height, sequence.Video.FrameRate);
var profileTimes = new List<double>();
using (var evaluator = new SequenceEvaluator(snapshot, sequenceId, profile))
{
    for (int index = 0; index < 30; index++)
    {
        var timer = Stopwatch.StartNew();
        _ = evaluator.Video(new(firstVideo.Start.Ticks + TimelineTime.FromFrames(index, sequence.Video.FrameRate).Ticks), token);
        profileTimes.Add(timer.Elapsed.TotalMilliseconds);
    }
}
var resultData = new
{
    source, sourceCharacteristics = imported.Asset, durationSeconds = imported.Duration.TotalSeconds, importMs, firstDecodeMs,
    randomSeeks = seeking, sequentialVideoMilliseconds = sequential, identityProfileVideoMilliseconds = profileTimes,
    peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
    scope = "Current source native FFmpeg and shared sequence evaluator, source-sized output; headless throughput is not a sustained GPU/device playback measurement. 40 seek samples: p99 unqualified."
};
await File.WriteAllTextAsync(Path.Combine(output, "headless.json"), EngineJson.Write(resultData), token);
await engine.SaveAsync(Path.Combine(output, "source.valencia"), token);
Console.WriteLine(EngineJson.Write(resultData));

static (int Maximum, double Mean, long ChannelsAbove8) ComparePixels(byte[] reference, byte[] selected)
{
    if (reference.Length != selected.Length) throw new InvalidDataException("Decoded output dimensions do not match.");
    int maximum = 0;
    long total = 0, high = 0;
    for (int offset = 0; offset < reference.Length; offset++)
    {
        if ((offset & 3) == 3) continue;
        int delta = Math.Abs(reference[offset] - selected[offset]);
        maximum = Math.Max(delta, maximum);
        total += delta;
        if (delta > 8) high++;
    }
    return (maximum, (double)total / (reference.Length * .75), high);
}
