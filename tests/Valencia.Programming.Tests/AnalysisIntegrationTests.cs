using System.Diagnostics;
using System.Text.Json;
using Valencia.Analysis;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Media;
using Valencia.Persistence;
using Xunit;

namespace Valencia.Programming.Tests;

public sealed partial class MediaAnalysisTests
{
    [Theory]
    [InlineData("partial")]
    [InlineData("version")]
    [InlineData("locked")]
    [InlineData("misaligned")]
    [InlineData("invalidRange")]
    public async Task PlannerRejectsUnsafeInputsWithoutMutation(string kind)
    {
        await using var engine = await Engine();
        var artifact = await Audio(engine);
        var sequence = engine.Query().Sequences[0];
        if (kind == "locked") Assert.True(engine.Execute(new EditCommand(Guid.NewGuid(), 1,
            new SetTrackState(sequence.Id, sequence.Tracks[0].Id, new(Locked: true)), engine.Session.Snapshot.Revision)).Success);
        if (kind == "misaligned") Assert.True(engine.Execute(new EditCommand(Guid.NewGuid(), 1,
            new MoveClip(sequence.Id, sequence.Tracks[0].Clips[0].Id, sequence.Tracks[0].Id, new(SequenceTime.TicksPerSecond)), engine.Session.Snapshot.Revision)).Success);
        if (kind == "partial") artifact = artifact with { Status = "partial" };
        if (kind == "version") artifact = artifact with { SchemaVersion = 2 };
        if (kind == "invalidRange") artifact = artifact with { Silence = [artifact.Silence[0] with { End = artifact.Silence[0].Start }] };
        var before = engine.Session.Snapshot;
        Assert.ThrowsAny<Exception>(() => RoughCutPlanner.Create(before, sequence.Id, artifact, new()));
        Assert.Same(before, engine.Session.Snapshot);
    }
    [Fact]
    public async Task PlannerHandlesPaddingMinimumSegmentsEmptyFactsAndFullRemoval()
    {
        await using var engine = await Engine();
        var artifact = await Audio(engine);
        var sequence = engine.Query().Sequences[0];
        var snapshot = engine.Session.Snapshot;
        var padded = RoughCutPlanner.Create(snapshot, sequence.Id, artifact, new(PrePaddingMs: 200, PostPaddingMs: 200, TargetDurationMs: 1000));
        Assert.InRange(padded.EstimatedDuration.TotalSeconds, 4.39, 4.41);
        Assert.False(padded.TargetMet);
        Assert.Equal(2, padded.Retained.Length);
        var none = RoughCutPlanner.Create(snapshot, sequence.Id, artifact with { Silence = [] }, new());
        Assert.True(none.Valid);
        Assert.Null(none.Plan);
        Assert.Empty(none.Removed);
        var all = artifact with { Silence = [new(new(new(0, 1, 48000)), new(new(288000, 1, 48000)), 0)] };
        var empty = RoughCutPlanner.Create(snapshot, sequence.Id, all, new(PrePaddingMs: 0, PostPaddingMs: 0));
        Assert.Equal(0, empty.EstimatedDuration.Ticks);
        Assert.Empty(empty.Retained);
        Assert.True(engine.Plan(empty.Plan!, true).Valid);
        Assert.Equal(0, engine.Query().Duration(sequence.Id).Ticks);
    }
    [Fact]
    public async Task SplitMovedNonzeroInClipsPreserveExistingGapsAndAlignment()
    {
        await using var engine = await Engine();
        var artifact = await Audio(engine);
        var sequence = engine.Query().Sequences[0];
        foreach (var track in sequence.Tracks)
        {
            Guid right = Guid.NewGuid();
            Assert.True(engine.Execute(new EditCommand(Guid.NewGuid(), 1,
                new SplitClip(sequence.Id, track.Clips[0].Id, new(2 * SequenceTime.TicksPerSecond), right), engine.Session.Snapshot.Revision)).Success);
            Assert.True(engine.Execute(new EditCommand(Guid.NewGuid(), 1,
                new MoveClip(sequence.Id, right, track.Id, new(4 * SequenceTime.TicksPerSecond)), engine.Session.Snapshot.Revision)).Success);
        }
        var proposal = await engine.RoughCutAsync(sequence.Id, artifact.Key, new(PrePaddingMs: 0, PostPaddingMs: 0));
        Assert.True(proposal.Valid);
        Assert.True(engine.Plan(proposal.Plan!, true).Valid);
        Assert.Empty(engine.Query().Evaluate(sequence.Id, new(3 * SequenceTime.TicksPerSecond)));
        var active = engine.Query().Evaluate(sequence.Id, new(4 * SequenceTime.TicksPerSecond));
        Assert.Equal(3, active.Length);
        Assert.All(active, clip => Assert.Equal(4, clip.SourcePosition.Timestamp.TotalSeconds + clip.SourcePosition.Offset.TotalSeconds));
        Assert.Equal(6, engine.Query().Duration(sequence.Id).TotalSeconds);
    }
    [Fact]
    public async Task EquivalentJobsDeduplicateAndCancellationPublishesNoPartialArtifact()
    {
        await using var engine = await Engine();
        var request = new AnalysisRequest(engine.Query().Assets[0].Id, 1, "audio", new(WindowMs: 1));
        Guid first = engine.Analysis.Start(request);
        Guid second = engine.Analysis.Start(request);
        Assert.Equal(first, second);
        var timer = Stopwatch.StartNew();
        engine.Analysis.Cancel(first);
        var result = await engine.Analysis.WaitAsync(first);
        log.WriteLine("M2B_CANCELLATION_MS=" + timer.Elapsed.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("cancelled", result.State);
        Assert.Null(await engine.Analysis.Cache.ReadAsync(result.Key));
        Assert.Empty(Directory.Exists(engine.Analysis.Cache.DirectoryPath) ? Directory.GetFiles(engine.Analysis.Cache.DirectoryPath, "*.partial") : []);
        var redo = await engine.Analysis.WaitAsync(engine.Analysis.Start(request));
        Assert.Equal("completed", redo.State);
        Assert.False(redo.CacheHit);
        var changed = request with { Audio = new(WindowMs: 1, ThresholdDb: -30) };
        var invalidated = await engine.Analysis.WaitAsync(engine.Analysis.Start(changed));
        Assert.NotEqual(redo.Key, invalidated.Key);
    }
    [Fact]
    public async Task ActualCliCopelandAnalysisPlanSaveReopenAndExportWorkflow()
    {
        string repo = FindRepo();
        string cli = typeof(Valencia.Cli.Program).Assembly.Location;
        byte[] original = await File.ReadAllBytesAsync(project);
        var result = await AnalysisFixture.Run("dotnet", cli, "script", "run", Path.Combine(repo, "examples", "auto-roughcut.ts"), "--project", project);
        Assert.True(JsonDocument.Parse(result).RootElement.GetProperty("success").GetBoolean());
        string saved = project + ".roughcut.valencia";
        var loaded = await new ProjectFile().LoadAsync(saved);
        Assert.Equal(4, loaded.Snapshot.Document.Sequences[0].Duration.TotalSeconds);
        Assert.Equal(original, await File.ReadAllBytesAsync(project));
        await VerifyExport(saved + ".mp4", 4, "4.5");
        var info = await new ProjectFile().LoadAsync(project);
        Guid asset = info.Snapshot.Document.Assets[0].Id;
        var analyzed = JsonDocument.Parse(await AnalysisFixture.Run("dotnet", cli, "analyze", "silence", project, "--asset", asset.ToString(), "--stream", "1"));
        string key = analyzed.RootElement.GetProperty("job").GetProperty("key").GetString()!;
        string plan = Path.Combine(root, "roughcut.json");
        var proposed = JsonDocument.Parse(await AnalysisFixture.Run("dotnet", cli, "roughcut", "plan", project, "--analysis", key, "--output", plan));
        Assert.True(proposed.RootElement.GetProperty("valid").GetBoolean());
        string destination = Path.Combine(root, "cli-cut.valencia");
        await AnalysisFixture.Run("dotnet", cli, "roughcut", "apply", project, "--plan", plan, "--save-as", destination);
        Assert.InRange((await new ProjectFile().LoadAsync(destination)).Snapshot.Document.Sequences[0].Duration.TotalSeconds, 4.19, 4.21);
    }
    private async Task VerifyExport(string movie, double duration, string sourceTime)
    {
        async Task<byte[]> Frame(string file, string time) => await AnalysisFixture.Run("ffmpeg", "-v", "error", "-ss", time, "-i", file,
            "-frames:v", "1", "-vf", "scale=32:18:flags=area", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1");
        var actual = await Frame(movie, "2.5");
        var expected = await Frame(source, sourceTime);
        Assert.InRange(actual.Zip(expected, (a, b) => Math.Abs(a - b)).Average(), 0, 8);
        async Task<int> FrameCode(string file, string time)
        {
            byte[] pixels = await AnalysisFixture.Run("ffmpeg", "-v", "error", "-ss", time, "-i", file, "-frames:v", "1",
                "-vf", "scale=160:90:flags=area", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1");
            Assert.Equal(160 * 90 * 3, pixels.Length);
            int code = 0;
            for (int bit = 0; bit < 8; bit++)
                if (pixels[(84 * 160 + bit * 20 + 10) * 3] >= 128) code |= 1 << bit;
            return code;
        }
        Assert.Equal(await FrameCode(source, sourceTime), await FrameCode(movie, "2.5"));
        Assert.Equal((int)Math.Round(double.Parse(sourceTime, System.Globalization.CultureInfo.InvariantCulture) * 30), await FrameCode(movie, "2.5"));
        using var probe = JsonDocument.Parse(await AnalysisFixture.Run("ffprobe", "-v", "error", "-show_entries", "format=duration:stream=codec_name", "-of", "json", movie));
        Assert.InRange(double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture), duration - .01, duration + .04);
        var codecs = probe.RootElement.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_name").GetString());
        Assert.Contains("h264", codecs);
        Assert.Contains("aac", codecs);
        byte[] audio = await AnalysisFixture.Run("ffmpeg", "-v", "error", "-ss", "2.5", "-i", movie, "-t", "0.2", "-vn", "-af", "pan=mono|c0=c0", "-ar", "48000", "-f", "f32le", "pipe:1");
        byte[] sourceAudio = await AnalysisFixture.Run("ffmpeg", "-v", "error", "-ss", sourceTime, "-i", source, "-t", "0.2", "-filter_complex",
            "[0:a:0][0:a:1]amix=inputs=2:normalize=0,pan=mono|c0=c0[a]", "-map", "[a]", "-ar", "48000", "-f", "f32le", "pipe:1");
        float[] a = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(audio).ToArray();
        float[] b = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(sourceAudio).ToArray();
        Assert.Equal(a.Length, b.Length);
        // Matroska's 1 ms stream clock quantizes a seek's first audio PTS. Measure the
        // offset within that original clock quantum, rather than claiming sample-exact seek.
        double bestError = double.MaxValue;
        for (int lag = -48; lag <= 48; lag++)
        {
            double error = 0;
            for (int sample = 48; sample < a.Length - 48; sample++) error += Math.Abs(a[sample] - b[sample + lag]);
            bestError = Math.Min(bestError, error / (a.Length - 96));
        }
        Assert.InRange(bestError, 0, .04);
    }
    private static string FindRepo()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "Valencia.slnx"))) return folder.FullName;
        throw new DirectoryNotFoundException();
    }
    [Fact]
    public async Task StaleSourceInvalidatesArtifactsAndAlreadyGeneratedPlans()
    {
        await using var engine = await Engine();
        var artifact = await Audio(engine);
        var sequence = engine.Query().Sequences[0];
        var proposal = await engine.RoughCutAsync(sequence.Id, artifact.Key, new());
        var before = engine.Session.Snapshot;
        File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddSeconds(1));
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.Analysis.ResultAsync(artifact.Key));
        Assert.False(engine.Plan(proposal.Plan!, true).Valid);
        Assert.Same(before, engine.Session.Snapshot);
    }
    [Fact]
    public async Task RetentionMergeAndGapPoliciesAreExplicit()
    {
        await using var engine = await Engine();
        var artifact = await Audio(engine);
        var sequence = engine.Query().Sequences[0];
        var gap = RoughCutPlanner.Create(engine.Session.Snapshot, sequence.Id, artifact, new(MaximumRetainedGapMs: 1000));
        Assert.Equal(5, gap.EstimatedDuration.TotalSeconds);
        var original = artifact.Silence[0];
        var two = artifact with { Silence = [original with { End = original.Start.Shift(AnalysisTime.Milliseconds(500)) },
            original with { Start = original.Start.Shift(AnalysisTime.Milliseconds(600)) }] };
        var merged = RoughCutPlanner.Create(engine.Session.Snapshot, sequence.Id, two,
            new(MinimumSegmentMs: 250, MinimumRemovalMs: 100, PrePaddingMs: 0, PostPaddingMs: 0));
        Assert.Single(merged.Removed);
        Assert.Equal(4, merged.EstimatedDuration.TotalSeconds);
    }
    [Fact]
    public async Task Mp4AacAnalysisKeepsOriginalStreamClockAndCodecBoundaryTolerance()
    {
        string mp4 = Path.Combine(root, "aac.mp4");
        await AnalysisFixture.Run("ffmpeg", "-v", "error", "-y", "-i", source, "-map", "0", "-c:v", "copy", "-c:a", "aac", mp4);
        var asset = MediaAssetProbe.Read(Guid.NewGuid(), mp4);
        var artifact = MediaAnalyzer.Audio(asset, 1, new(), "test", AnalysisCache.Fingerprint(mp4), null, default);
        var range = Assert.Single(artifact.Silence);
        Assert.Equal(asset.Streams[1].TimeBaseDenominator, range.Start.Timestamp.Denominator);
        Assert.InRange(AnalysisTime.Normalize(range.Start, artifact.ContainerStart).TotalSeconds, 1.96, 2.04);
        Assert.InRange(AnalysisTime.Normalize(range.End, artifact.ContainerStart).TotalSeconds, 3.96, 4.04);
    }
}
