using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Valencia.Analysis;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Media;
using Valencia.Persistence;
using Valencia.Scripting;
using Xunit;

namespace Valencia.Programming.Tests;

public sealed class AudioAnalysisTests
{
    private static AudioAnalyzer Analyze(int samples, Func<int, float> signal, int channels = 1, AudioSettings? settings = null, long origin = 0)
    {
        var analyzer = new AudioAnalyzer(settings ?? new(MinimumSilenceMs: 100));
        const int rate = 8000;
        for (int start = 0; start < samples; start += 400)
        {
            int count = Math.Min(400, samples - start);
            var values = new float[count * channels];
            for (int i = 0; i < count; i++)
                for (int c = 0; c < channels; c++) values[i * channels + c] = c == 0 ? signal(start + i) : -signal(start + i);
            analyzer.Push(new(new(origin + start, 1, rate), rate, channels, values));
        }
        return analyzer;
    }
    [Theory]
    [InlineData(8000, true, 1)]
    [InlineData(8000, false, 0)]
    [InlineData(80, true, 0)]
    public void SilentActiveAndShortSources(int samples, bool silent, int intervals)
    {
        var result = Analyze(samples, _ => silent ? 0 : .5f).Finish();
        Assert.Equal(intervals, result.Silence.Length);
        Assert.Equal(samples, result.Waveform[0].Bins.Sum(b => b.Samples));
        Assert.All(result.Waveform.SelectMany(l => l.Bins), b => Assert.True(b.End.CompareTo(b.Start) > 0));
    }
    [Fact]
    public void StereoPhaseAndBoundariesPreserveSamplePositionsAndPyramidEnergy()
    {
        var result = Analyze(24000, i => i < 8000 || i >= 16000 ? 0 : .5f, 2, origin: 9007199254740993).Finish();
        Assert.Equal(2, result.Silence.Length);
        Assert.Equal(8000, TimelineTime.SampleIndex(result.Silence[0].End.Since(result.Silence[0].Start), 8000, TimeRounding.Exact));
        Assert.True(result.Waveform.Length > 1);
        var top = result.Waveform[^1].Bins.Single();
        Assert.Equal(.5f, top.Channels[0].Maximum);
        Assert.Equal(-.5f, top.Channels[1].Minimum);
        Assert.InRange(top.Channels[0].Rms, .2886, .2888);
        Assert.Equal("active", result.Activity[1].Category);
        Assert.Equal(9007199254740993, result.Silence[0].Start.Timestamp.Value);
    }
    [Fact]
    public void MergingShortActivityIsExplicitAndDeterministic()
    {
        AudioSettings settings = new(MinimumSilenceMs: 100, MinimumActivityMs: 60);
        var first = Analyze(8000, i => i is >= 3840 and < 4160 ? .5f : 0, settings: settings).Finish();
        var second = Analyze(8000, i => i is >= 3840 and < 4160 ? .5f : 0, settings: settings).Finish();
        Assert.Single(first.Silence);
        Assert.Equal(AnalysisJson.Write(first.Silence), AnalysisJson.Write(second.Silence));
    }
    [Fact]
    public void DiscontinuitiesAreKeptSeparate()
    {
        var analyzer = new AudioAnalyzer(new(MinimumSilenceMs: 0));
        analyzer.Push(new(new(0, 1, 8000), 8000, 1, new float[800]));
        analyzer.Push(new(new(8000, 1, 8000), 8000, 1, new float[800]));
        var result = analyzer.Finish();
        Assert.Equal(2, result.Silence.Length);
        Assert.Equal(1, result.Silence[1].Epoch);
        Assert.All(result.Waveform, l => Assert.Equal(2, l.Bins.Length));
    }
    [Fact]
    public void TenMinuteStreamingInputDoesNotRetainPcm()
    {
        var analyzer = new AudioAnalyzer(new(WindowMs: 1));
        float[] block = new float[8000];
        for (int second = 0; second < 600; second++) analyzer.Push(new(new(second, 1, 1), 8000, 1, block));
        var result = analyzer.Finish();
        Assert.Equal(4_800_000, analyzer.ProcessedSamples);
        Assert.Single(result.Silence);
        // 1 ms windows initially make 5 ms bins. Crossing 65,536 bins doubles
        // the base spacing to 10 ms without losing aggregate sample coverage.
        Assert.Equal(60000, result.Waveform[0].Bins.Length);
        Assert.True(result.Waveform.Sum(l => l.Bins.Length) < 120010);
    }
    [Fact]
    public void CoordinateConversionsUseExactOffsetsAndCheckedOverflow()
    {
        var start = AnalysisTime.SampleOffset(new(90000, 1, 90000), 48000, 48000);
        Assert.Equal(SequenceTime.TicksPerSecond, AnalysisTime.Normalize(start, new(1, 1, 1)).Ticks);
        var clip = new TimelineClip(Guid.NewGuid(), Guid.NewGuid(), 0, new(10 * SequenceTime.TicksPerSecond),
            new(new(1, 1, 1)), new(new(4, 1, 1)), new());
        var time = AnalysisTime.ToTimeline(clip, start);
        Assert.Equal(11 * SequenceTime.TicksPerSecond, time.Ticks);
        Assert.True(start.EquivalentTo(AnalysisTime.ToSource(clip, time)));
        Assert.Throws<OverflowException>(() => AnalysisTime.SampleOffset(new(0, 1, 1), long.MaxValue, 8000));
    }
    [Fact]
    public void FractionalSampleWindowsPaddingAndVisibleWaveformQueries()
    {
        var analyzer = new AudioAnalyzer(new(WindowMs: 1, MinimumSilenceMs: 0, LeadingPaddingMs: 1, TrailingPaddingMs: 1));
        analyzer.Push(new(new(0, 1, 44100), 44100, 1, new float[4410]));
        var result = analyzer.Finish();
        Assert.Equal(4410, result.Waveform[0].Bins.Sum(b => b.Samples));
        var silence = Assert.Single(result.Silence);
        Assert.Equal(AnalysisTime.Milliseconds(1), silence.Start.Offset);
        var artifact = new AnalysisArtifact(1, "key", Guid.NewGuid(), "source", 0, "audio", MediaAnalyzer.AudioAlgorithm, "{}", "test",
            new(0, 1, 1), "complete", 44100, 1, 4410, result.Waveform, result.Silence, result.Activity, [], []);
        var index = new WaveformIndex(artifact);
        var start = new SourcePosition(new(1000, 1, 44100));
        var end = new SourcePosition(new(2000, 1, 44100));
        var visible = index.Visible(start, end, 3);
        Assert.InRange(visible.Count, 1, 3);
        Assert.All(visible, bin => Assert.True(bin.End.CompareTo(start) > 0 && bin.Start.CompareTo(end) < 0));
    }
    [Fact]
    public void TypedObjectSettingsAcceptNegativeDbfsThroughTheExistingCompiler()
    {
        Guid asset = Guid.NewGuid();
        string source = $$"""const $value: AnalysisRequest = { assetId: "{{asset}}", streamIndex: 1, kind: "audio", audio: { thresholdDb: -35.5, minimumSilenceMs: 800 } };""";
        var request = AnalysisContracts.Parse<AnalysisRequest>(source, "AnalysisRequest", "settings.obj.ts");
        Assert.Equal(-35.5, request.Audio!.ThresholdDb);
        Assert.Equal(20, request.Audio.WindowMs);
        Assert.Equal(asset, request.AssetId);
    }
}

internal static class AnalysisFixture
{
    public static async Task<byte[]> Run(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); await copy; }
        catch { process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await errors);
        return output.ToArray();
    }
    public static async Task Create(string path)
    {
        await Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30:duration=6",
            "-f", "lavfi", "-i", "aevalsrc=exprs='if(gte(t,2)*lt(t,4),0,0.5*sin(2*PI*440*t))|-if(gte(t,2)*lt(t,4),0,0.5*sin(2*PI*440*t))':s=48000:d=6",
            "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=6", "-map", "0:v", "-map", "1:a", "-map", "2:a",
            "-vf", "drawtext=fontfile='C\\:/Windows/Fonts/consola.ttf':text='%{n}':x=4:y=4:fontsize=16:fontcolor=white:box=1:boxcolor=black," +
            "geq=lum='if(gte(Y,H-12),if(mod(floor(N/pow(2,floor(X/20))),2),235,16),lum(X,Y))':cb='if(gte(Y,H-6),128,cb(X,Y))':cr='if(gte(Y,H-6),128,cr(X,Y))'",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "pcm_s16le", path);
    }
    public static async Task<string> Project(string root, string source)
    {
        string path = Path.Combine(root, "input.valencia");
        if (File.Exists(path)) path = Path.Combine(root, Path.GetFileNameWithoutExtension(source) + ".valencia");
        await Run("dotnet", typeof(Valencia.Cli.Program).Assembly.Location, "project", "create", path, "--media", source);
        return path;
    }
}

public sealed partial class MediaAnalysisTests : IAsyncLifetime
{
    private readonly Xunit.Abstractions.ITestOutputHelper log;
    public MediaAnalysisTests(Xunit.Abstractions.ITestOutputHelper log) => this.log = log;
    private readonly string root = Path.Combine(Path.GetTempPath(), "Valencia-M2b", Guid.NewGuid().ToString("N"));
    private string source = "", project = "";
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        source = Path.Combine(root, "source.mkv");
        await AnalysisFixture.Create(source);
        project = await AnalysisFixture.Project(root, source);
    }
    public Task DisposeAsync() { Directory.Delete(root, true); return Task.CompletedTask; }
    private async Task<EditingEngine> Engine()
    {
        var loaded = await new ProjectFile().LoadAsync(project);
        return new(new(loaded.Snapshot), projectPath: project, analysisDirectory: Path.Combine(root, "cache"));
    }
    private static async Task<AnalysisArtifact> Audio(EditingEngine engine, int stream = 1)
    {
        var id = engine.Analysis.Start(new(engine.Query().Assets[0].Id, stream, "audio"));
        var state = await engine.Analysis.WaitAsync(id);
        Assert.True(state.State == "completed", state.Error);
        return await engine.Analysis.ResultAsync(state.Key);
    }
    [Fact]
    public async Task RealDecodeSharesFeaturesCachesAndKeepsObsStreamsIndependent()
    {
        await using var engine = await Engine();
        var snapshot = engine.Session.Snapshot;
        var artifact = await Audio(engine);
        Assert.Equal(2, artifact.Channels);
        Assert.Equal(48000, artifact.SampleRate);
        var silence = Assert.Single(artifact.Silence);
        Assert.InRange(silence.Start.Timestamp.TotalSeconds + silence.Start.Offset.TotalSeconds, 1.98, 2.02);
        Assert.InRange(silence.End.Timestamp.TotalSeconds + silence.End.Offset.TotalSeconds, 3.98, 4.02);
        Assert.Equal(288000, artifact.ProcessedUnits);
        Assert.True(artifact.Waveform.Length > 1);
        var hit = engine.Analysis.Start(new(artifact.AssetId, 1, "audio"));
        Assert.True((await engine.Analysis.WaitAsync(hit)).CacheHit);
        Assert.Empty((await Audio(engine, 2)).Silence);
        Assert.Same(snapshot, engine.Session.Snapshot);
        using var json = JsonDocument.Parse(EngineJson.Write(artifact));
        Assert.Empty(Copeland.TS.Tson.TypeProfileCompiler.Validate(AnalysisContracts.Catalog, "AnalysisArtifact", json.RootElement));
    }
    [Fact]
    public async Task PlanIsDeterministicAtomicUndoableAndExportSelectsCorrectSourceFrames()
    {
        await using var engine = await Engine();
        var artifact = await Audio(engine);
        var before = engine.Session.Snapshot;
        var sequence = engine.Query().Sequences[0];
        var policy = new RoughCutPolicy(MinimumSegmentMs: 100, MinimumRemovalMs: 500, PrePaddingMs: 0, PostPaddingMs: 0);
        var proposal = await engine.RoughCutAsync(sequence.Id, artifact.Key, policy);
        Assert.True(proposal.Valid, EngineJson.Write(proposal));
        Assert.Same(before, engine.Session.Snapshot);
        Assert.Equal(EngineJson.Write(proposal.Plan), EngineJson.Write((await engine.RoughCutAsync(sequence.Id, artifact.Key, policy)).Plan));
        Assert.Equal(4 * SequenceTime.TicksPerSecond, proposal.EstimatedDuration.Ticks);
        var applied = engine.Plan(proposal.Plan!, true);
        Assert.True(applied.Valid, EngineJson.Write(applied));
        Assert.Equal(EngineJson.Write(proposal.Preview!.Changes), EngineJson.Write(applied.Changes));
        var after = engine.Session.Snapshot;
        Assert.True(engine.Undo(after.Revision).Success);
        Assert.True(DocumentSemantics.Equal(before.Document, engine.Session.Snapshot.Document));
        Assert.True(engine.Redo(engine.Session.Snapshot.Revision).Success);
        Assert.True(DocumentSemantics.Equal(after.Document, engine.Session.Snapshot.Document));
        Assert.False(engine.Plan(proposal.Plan!, true).Valid);
        string saved = Path.Combine(root, "edited.valencia");
        await engine.SaveAsync(saved);
        Assert.True(DocumentSemantics.Equal(after.Document, (await new ProjectFile().LoadAsync(saved)).Snapshot.Document));
        string movie = Path.Combine(root, "edited.mp4");
        var export = await engine.Exports.WaitAsync(engine.Exports.Start(sequence.Id, movie));
        Assert.True(export.State == "completed", export.Error);
        async Task<byte[]> Frame(string file, string time) => await AnalysisFixture.Run("ffmpeg", "-v", "error", "-ss", time, "-i", file,
            "-frames:v", "1", "-vf", "scale=32:18:flags=area", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1");
        byte[] expected = await Frame(source, "4.5"), actual = await Frame(movie, "2.5");
        Assert.InRange(expected.Zip(actual, (a, b) => Math.Abs(a - b)).Average(), 0, 8);
    }
    [Fact]
    public async Task CopelandScriptAnalyzesAndCreatesEquivalentNativePlan()
    {
        await using var engine = await Engine();
        var artifact = await Audio(engine);
        var sequence = engine.Query().Sequences[0];
        var native = await engine.RoughCutAsync(sequence.Id, artifact.Key, new());
        using var host = new CopelandScriptHost();
        string script = $$"""
            using Valencia.Scripting;
            function run(api: ScriptApi): string {
                const job = api.AnalyzeAudio("{{artifact.AssetId}}", 1, -35, 800);
                const key = api.WaitAnalysis(job);
                return api.RoughCutPlan(api.FirstSequence(), key, "{}");
            }
            """;
        var result = await host.RunAsync(script, Path.Combine(root, "analyze.ts"), engine);
        Assert.True(result.Success, EngineJson.Write(result.Diagnostics));
        Assert.Equal(EngineJson.Write(native.Plan), result.ReturnValue);
    }
}
