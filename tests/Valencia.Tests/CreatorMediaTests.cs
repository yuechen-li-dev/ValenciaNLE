using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Media;
using Xunit;

namespace Valencia.Tests;

public sealed class CreatorMediaTests : IAsyncLifetime
{
    [Fact]
    public async Task WidelySeparatedVideoQueriesSeekInsteadOfDecodingTheInterveningMinutes()
    {
        string path = Path.Combine(root, "forward-jumps.mkv");
        await TimelineFixture.Run("-f", "lavfi", "-i", "testsrc2=size=160x90:rate=2:duration=600",
            "-c:v", "libx264", "-g", "4", "-bf", "2", "-pix_fmt", "yuv420p", path);
        var imported = MediaAssetProbe.Import(Guid.NewGuid(), path);
        var stream = imported.Asset.Streams.Single();
        var project = Project(imported.Asset, TrackKind.Video, new(stream.Start!.Value), imported.Duration);
        using var evaluator = new SequenceEvaluator(project, project.Document.Sequences[0].Id);
        foreach (long second in new[] { 0L, 500L, 100L, 599L })
        {
            var frame = evaluator.Video(Seconds(second), default);
            Assert.Equal((double)second, Assert.Single(frame.SourceTimestamps!.Value).TotalSeconds, 6);
        }
        // A ten-minute source has 1200 frames; only the actual preceding GOPs should be read.
        Assert.InRange(evaluator.ActiveVideoDecodedFrames, 4, 40);
        var actual = evaluator.Video(Seconds(500), default);
        var independentlyDecoded = await TimelineFixture.Run("-i", path, "-vf", "select='eq(n,1000)'", "-fps_mode", "passthrough",
            "-frames:v", "1", "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1");
        Assert.InRange(actual.Rgba.Zip(independentlyDecoded, (a, b) => Math.Abs(a - b)).Average(), 0, 1);
    }
    private readonly string root = Path.Combine(Path.GetTempPath(), "Valencia.M2d", Guid.NewGuid().ToString("N"));
    private readonly Xunit.Abstractions.ITestOutputHelper log;
    public CreatorMediaTests(Xunit.Abstractions.ITestOutputHelper log) => this.log = log;
    public Task InitializeAsync() { Directory.CreateDirectory(root); return Task.CompletedTask; }
    public Task DisposeAsync() { Directory.Delete(root, true); return Task.CompletedTask; }
    private static SequenceTime Seconds(long seconds) => new(seconds * SequenceTime.TicksPerSecond);
    private static ProjectSnapshot Project(MediaAsset asset, TrackKind kind, SourcePosition from, SequenceTime duration, ClipProperties? properties = null)
    {
        var stream = asset.Streams.First(s => s.Kind == kind);
        var clip = new TimelineClip(Guid.NewGuid(), asset.Id, stream.Index, default, from, from.Shift(duration), properties ?? new());
        var track = new TimelineTrack(Guid.NewGuid(), kind, 0, kind.ToString(), new(), [clip]);
        return new(new(Guid.NewGuid(), 1, "Media qualification", [asset], [new(Guid.NewGuid(), "Main", new(160, 90, new(30, 1)), 48000, [track])]), 0);
    }
    [Theory]
    [InlineData(100007)]
    [InlineData(300001)]
    [InlineData(47201)]
    public async Task CoarseMatroskaAudioSeekPreservesExactSamplePhase(int sample)
    {
        string source = Path.Combine(root, "noise.mkv");
        await TimelineFixture.Run("-f", "lavfi", "-i", "anoisesrc=r=48000:d=8:a=0.1:s=63", "-c:a", "pcm_s16le", source);
        var asset = MediaAssetProbe.Read(Guid.NewGuid(), source);
        var stream = asset.Streams.Single();
        Assert.Equal(1000, stream.TimeBaseDenominator);
        var from = new SourcePosition(stream.Start!.Value).Shift(TimelineTime.FromSamples(sample, 48000));
        var project = Project(asset, TrackKind.Audio, from, Seconds(1));
        var timer = Stopwatch.StartNew();
        using var evaluator = new SequenceEvaluator(project, project.Document.Sequences[0].Id);
        float[] actual = evaluator.Audio(default, 0, 4096, default);
        var bytes = await TimelineFixture.Run("-i", source, "-af", $"atrim=start_sample={sample}:end_sample={sample + 4096}", "-ac", "2", "-ar", "48000", "-f", "f32le", "pipe:1");
        var expected = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        double error = actual.Zip(expected, (a, b) => Math.Abs(a - b)).Average();
        log.WriteLine($"M2D_EXACT_AUDIO sample={sample} mae={error} wallMs={timer.Elapsed.TotalMilliseconds}");
        Assert.InRange(error, 0, 1e-7);
    }
    [Fact]
    public async Task VideoCueAudioSeeksPreserveEachStreamSamplePhaseAndBoundDecodeWork()
    {
        string source = Path.Combine(root, "indexed-audio.mkv");
        await TimelineFixture.Run("-f", "lavfi", "-i", "testsrc2=size=160x90:rate=2:duration=120",
            "-f", "lavfi", "-i", "anoisesrc=r=48000:d=120:a=0.1:s=63",
            "-f", "lavfi", "-i", "anoisesrc=r=48000:d=120:a=0.2:s=97",
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "libx264", "-g", "12",
            "-keyint_min", "12", "-sc_threshold", "0", "-bf", "2", "-c:a", "pcm_s16le",
            "-output_ts_offset", "2", source);
        var asset = MediaAssetProbe.Read(Guid.NewGuid(), source);
        Assert.Equal(3, asset.Streams.Length);
        foreach (var stream in asset.Streams.Where(s => s.Kind == TrackKind.Audio))
        {
            Assert.Equal(1000, stream.TimeBaseDenominator);
            Assert.True(stream.Start!.Value.Value > 0);
            var selected = asset with { Streams = [asset.Streams[0], stream] };
            var from = new SourcePosition(stream.Start.Value).Shift(TimelineTime.FromSamples(13, 48000));
            var project = Project(selected, TrackKind.Audio, from, Seconds(119));
            using var evaluator = new SequenceEvaluator(project, project.Document.Sequences[0].Id);
            foreach (long sample in new[] { 100 * 48000 + 7L, 6 * 48000 + 23L, 110 * 48000 + 1L })
            {
                var timer = Stopwatch.StartNew();
                float[] actual = evaluator.Audio(TimelineTime.FromSamples(sample, 48000), 0, 2048, default);
                log.WriteLine($"CUE_AUDIO stream={stream.Index} sample={sample} ms={timer.Elapsed.TotalMilliseconds} frames={evaluator.ActiveAudioDecodedFrames}");
                // A seek may start at the preceding six-second video GOP plus
                // one-second audio preroll. It must not decode the intervening minutes.
                Assert.InRange(evaluator.ActiveAudioDecodedFrames, 1, 500);
                var bytes = await TimelineFixture.Run("-i", source, "-map", $"0:{stream.Index}", "-af",
                    $"atrim=start_sample={sample + 13}:end_sample={sample + 13 + 2048}",
                    "-ac", "2", "-ar", "48000", "-f", "f32le", "pipe:1");
                var expected = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
                Assert.Equal(expected.Length, actual.Length);
                Assert.InRange(actual.Zip(expected, (a, b) => Math.Abs(a - b)).Average(), 0, 1e-7);
            }
        }
    }
    [Fact]
    public async Task VfrBFramesLongGopOffsetAndBackwardRequestsMatchIndependentFrames()
    {
        string source = Path.Combine(root, "vfr.mkv");
        await TimelineFixture.Run("-copyts", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30:duration=8", "-vf",
            "settb=1/60000,setpts='if(lt(N,60),N/(30*TB),if(lt(N,120),2/TB+(N-60)/(15*TB),6/TB+(N-120)/(60*TB)))+2/TB'",
            "-fps_mode", "passthrough", "-enc_time_base", "1:60000", "-c:v", "libx264", "-g", "120", "-bf", "3", "-pix_fmt", "yuv420p", source);
        var imported = MediaAssetProbe.Import(Guid.NewGuid(), source);
        var stream = imported.Asset.Streams.Single();
        var start = new SourcePosition(stream.Start!.Value);
        var project = Project(imported.Asset, TrackKind.Video, start, imported.Duration);
        var psi = new ProcessStartInfo("ffprobe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "error", "-select_streams", "v:0", "-show_frames", "-show_entries", "frame=best_effort_timestamp,pkt_dts,duration", "-of", "json", source }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await error);
        using var json = JsonDocument.Parse(await output);
        var pts = json.RootElement.GetProperty("frames").EnumerateArray().Select(f => f.GetProperty("best_effort_timestamp").GetInt64()).ToArray();
        Assert.True(pts.Zip(pts.Skip(1), (a, b) => b - a).Distinct().Count() > 1);
        using var evaluator = new SequenceEvaluator(project, project.Document.Sequences[0].Id);
        var latencies = new List<double>();
        foreach (int index in new[] { 119, 10, 60, 59, 120, 200, 0, 238 })
        {
            foreach (long delta in new[] { -1L, 0L, 1L })
            {
                long wanted = Math.Max(pts[0], pts[index] + delta);
                int expectedIndex = Array.FindLastIndex(pts, p => p <= wanted);
                var position = new SourcePosition(new(wanted, stream.TimeBaseNumerator, stream.TimeBaseDenominator));
                var time = position.Since(start);
                var timer = Stopwatch.StartNew();
                var frame = evaluator.Video(time, default);
                log.WriteLine($"SEEK wanted={wanted}, expectedIndex={expectedIndex}, expectedPts={pts[expectedIndex]}, actual={JsonSerializer.Serialize(frame.SourceTimestamps)}");
                latencies.Add(timer.Elapsed.TotalMilliseconds);
                var expected = await TimelineFixture.Run("-i", source, "-vf", $"select=eq(n\\,{expectedIndex})", "-frames:v", "1", "-fps_mode", "passthrough", "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1");
                Assert.Equal(expected.Length, frame.Rgba.Length);
                Assert.InRange(frame.Rgba.Zip(expected, (a, b) => Math.Abs(a - b)).Average(), 0, 3.5);
            }
        }
        log.WriteLine("M2D_RANDOM_SEEK_MS=" + JsonSerializer.Serialize(latencies));
    }
    [Theory]
    [InlineData("libx265", "hevc")]
    [InlineData("libaom-av1", "av1")]
    public async Task AvailableHevcAndAv1AreActuallyDecoded(string encoder, string codec)
    {
        string path = Path.Combine(root, codec + ".mkv");
        var args = new List<string> { "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=5:duration=1", "-c:v", encoder, "-threads", "2" };
        if (encoder == "libx265") args.AddRange(["-preset", "ultrafast", "-x265-params", "pools=1:frame-threads=1:log-level=error"]);
        else args.AddRange(["-cpu-used", "8", "-crf", "45"]);
        args.Add(path);
        await TimelineFixture.Run([.. args]);
        var imported = MediaAssetProbe.Import(Guid.NewGuid(), path);
        Assert.Equal(codec, imported.Asset.Streams[0].Codec);
        var project = Project(imported.Asset, TrackKind.Video, new(imported.Asset.Streams[0].Start!.Value), imported.Duration);
        using var evaluator = new SequenceEvaluator(project, project.Document.Sequences[0].Id);
        Assert.Contains(evaluator.Video(default, default).Rgba, pixel => pixel > 50 && pixel < 250);
    }
    [Fact]
    public async Task ProxyPreservesVfrClockAndPreviewCanFallBackToOriginal()
    {
        string path = Path.Combine(root, "source.mp4");
        await TimelineFixture.Run("-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30000/1001:duration=3", "-c:v", "libx264", "-g", "60", "-bf", "2", path);
        var imported = MediaAssetProbe.Import(Guid.NewGuid(), path);
        var proxy = await MediaProxy.GenerateAsync(imported.Asset, 0, default);
        Assert.Equal(proxy, await MediaProxy.GenerateAsync(imported.Asset, 0, default));
        var asset = imported.Asset with { ProxyKey = proxy.Key };
        Assert.NotNull(MediaProxy.Resolve(asset, asset.Streams[0]));
        var project = Project(asset, TrackKind.Video, new(asset.Streams[0].Start!.Value), imported.Duration);
        using var preview = new SequenceEvaluator(project, project.Document.Sequences[0].Id, useProxies: true);
        using var original = new SequenceEvaluator(project, project.Document.Sequences[0].Id);
        foreach (int frame in new[] { 0, 29, 60, 1 })
        {
            var time = TimelineTime.FromFrames(frame, new(30000, 1001));
            Assert.InRange(preview.Video(time, default).Rgba.Zip(original.Video(time, default).Rgba, (a, b) => Math.Abs(a - b)).Average(), 0, 6);
        }
        var missing = asset with { ProxyKey = new string('0', 64) };
        Assert.Null(MediaProxy.Resolve(missing, missing.Streams[0]));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaProxy.GenerateAsync(asset, 0, cancellation.Token));
    }
    [Fact]
    public async Task FftNoiseSuppressionActuallyProcessesBoundedAudio()
    {
        string path = Path.Combine(root, "quiet-noise.wav");
        await TimelineFixture.Run("-f", "lavfi", "-i", "anoisesrc=r=48000:d=3:a=0.002:s=63", "-c:a", "pcm_s16le", path);
        var asset = MediaAssetProbe.Read(Guid.NewGuid(), path);
        var raw = Project(asset, TrackKind.Audio, new(asset.Streams[0].Start ?? new(0, asset.Streams[0].TimeBaseNumerator, asset.Streams[0].TimeBaseDenominator)), Seconds(3));
        var sequence = raw.Document.Sequences[0];
        var track = sequence.Tracks[0];
        var clip = track.Clips[0];
        var clean = raw with { Document = raw.Document with { Sequences = [sequence with { Tracks = [track with { Clips = [clip with { Properties = clip.Properties with { Audio = new(NoiseReductionDb: 18) } }] }] }] } };
        using var baseline = new SequenceEvaluator(raw, sequence.Id);
        using var filtered = new SequenceEvaluator(clean, sequence.Id);
        var a = baseline.Audio(default, 24000, 48000, default);
        var b = filtered.Audio(default, 24000, 48000, default);
        double rmsA = Math.Sqrt(a.Select(x => (double)x * x).Average()), rmsB = Math.Sqrt(b.Select(x => (double)x * x).Average());
        log.WriteLine($"M2D_NOISE rawRms={rmsA} cleanRms={rmsB}");
        Assert.True(rmsB < rmsA * .8);
    }

    [Theory]
    [InlineData(16000)]
    [InlineData(44100)]
    [InlineData(48000)]
    public async Task NoiseCleanupPreservesImpulseSampleAndBackwardAudioReads(int rate)
    {
        string path = Path.Combine(root, "impulse.wav");
        await TimelineFixture.Run("-f", "lavfi", "-i", $"aevalsrc=if(eq(n\\,{rate})\\,0.8\\,0):s={rate}:d=3", "-c:a", "pcm_f32le", path);
        var asset = MediaAssetProbe.Read(Guid.NewGuid(), path);
        var project = Project(asset, TrackKind.Audio, new(asset.Streams[0].Start ?? new(0, 1, rate)), Seconds(3), new(Audio: new(NoiseReductionDb: 12)));
        project = project with { Document = project.Document with { Sequences = [project.Document.Sequences[0] with { AudioSampleRate = rate }] } };
        using var evaluator = new SequenceEvaluator(project, project.Document.Sequences[0].Id);
        var audio = evaluator.Audio(default, rate - 1000, 4096, default);
        int peak = Enumerable.Range(0, audio.Length / 2).MaxBy(i => Math.Abs(audio[i * 2]));
        log.WriteLine($"M2D_DENOISE_IMPULSE rate={rate} expectedSample={rate} actualSample={rate - 1000 + peak}");
        Assert.Equal(1000, peak);
        Assert.Equal(audio, evaluator.Audio(default, rate - 1000, 4096, default));
    }

    [Fact]
    public async Task ForwardAudioTimestampGapProducesSilenceAndKeepsFollowingSamples()
    {
        string path = Path.Combine(root, "audio-gap.mkv");
        await TimelineFixture.Run("-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2", "-af", "asetpts='PTS+gte(T,1)/TB'", "-c:a", "pcm_s16le", path);
        var asset = MediaAssetProbe.Read(Guid.NewGuid(), path);
        var project = Project(asset, TrackKind.Audio, new(asset.Streams[0].Start!.Value), Seconds(3));
        using var evaluator = new SequenceEvaluator(project, project.Document.Sequences[0].Id);
        var silent = evaluator.Audio(default, 65000, 4096, default);
        Assert.All(silent, value => Assert.Equal(0, value));
        var after = evaluator.Audio(default, 110000, 4096, default);
        Assert.Contains(after, value => Math.Abs(value) > .01);
        Assert.Equal(after, evaluator.Audio(default, 110000, 4096, default));
    }

    [Fact]
    public async Task CaptionsHighlightTheCorrectWordAndSurviveIndependentMp4Decode()
    {
        string path = Path.Combine(root, "blue.mp4");
        await TimelineFixture.Run("-f", "lavfi", "-i", "color=blue:size=160x90:rate=30:duration=2", "-c:v", "libx264", path);
        var imported = MediaAssetProbe.Import(Guid.NewGuid(), path);
        // Caption timing belongs to a selected source audio stream, independently of the visible video stream.
        var asset = imported.Asset with { Streams = imported.Asset.Streams.Add(new(1, TrackKind.Audio, "fixture", 1, 48000)) };
        var project = Project(asset, TrackKind.Video, new(asset.Streams[0].Start!.Value), Seconds(2));
        var sequence = project.Document.Sequences[0];
        var profile = new OutputProfile(Guid.NewGuid(), "caption", 320, 180, new(30, 1));
        var settings = new CreatorSettings(1, [new(Guid.NewGuid(), asset.Id, 1, new(new(0, 1, 48000)), new(new(48000, 1, 48000)), "Hello"),
            new(Guid.NewGuid(), asset.Id, 1, new(new(48000, 1, 48000)), new(new(96000, 1, 48000)), "world")], new(Size: 120), [profile], []);
        project = project with { Document = project.Document with { Sequences = [sequence with { Creator = settings }] } };
        using var evaluator = new SequenceEvaluator(project, sequence.Id, profile);
        var first = evaluator.Video(new(SequenceTime.TicksPerSecond / 2), default);
        Assert.Equal(first.Rgba, evaluator.Video(first.Time, default).Rgba);
        var second = evaluator.Video(Seconds(1), default);
        double HighlightX(byte[] pixels)
        {
            var x = Enumerable.Range(0, pixels.Length / 4).Where(p => pixels[p * 4] > 180 && pixels[p * 4 + 1] > 180 && pixels[p * 4 + 2] < 100).Select(p => p % 320).ToArray();
            Assert.NotEmpty(x); return x.Average();
        }
        Assert.True(HighlightX(second.Rgba) > HighlightX(first.Rgba) + 30);
        string output = Path.Combine(root, "caption.mp4");
        await SequenceExport.RunAsync(project, sequence.Id, output, cancellation: default, profile: profile);
        var decoded = await TimelineFixture.Run("-ss", "0.5", "-i", output, "-frames:v", "1", "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1");
        Assert.InRange(decoded.Zip(first.Rgba, (a, b) => Math.Abs(a - b)).Average(), 0, 5);
        Assert.InRange(HighlightX(decoded), HighlightX(first.Rgba) - 3, HighlightX(first.Rgba) + 3);
    }

    [Fact]
    public async Task NativePcmGainFadeAndAutomationMatchIndependentSampleFormula()
    {
        string path = Path.Combine(root, "constant.wav");
        await TimelineFixture.Run("-f", "lavfi", "-i", "aevalsrc=0.1:s=48000:d=2", "-c:a", "pcm_f32le", path);
        var asset = MediaAssetProbe.Read(Guid.NewGuid(), path);
        long half = SequenceTime.TicksPerSecond / 2;
        var project = Project(asset, TrackKind.Audio, new(new(0, 1, 48000)), Seconds(2), new(Gain: .5m, Audio: new(half, half)));
        var sequence = project.Document.Sequences[0];
        var settings = new CreatorSettings(1, [], new(), [], [new(sequence.Tracks[0].Id, -6, [new(default, 0), new(Seconds(1), -6)])], MasterGainDb: -3);
        project = project with { Document = project.Document with { Sequences = [sequence with { Creator = settings }] } };
        using var evaluator = new SequenceEvaluator(project, sequence.Id);
        foreach (int sample in new[] { 0, 12000, 24000, 48000, 84000 })
        {
            double seconds = sample / 48000.0;
            double fade = Math.Min(1, Math.Min(seconds / .5, (2 - seconds) / .5));
            double expected = .1 / Math.Sqrt(2) * .5 * Math.Pow(10, (-6 - 3 - 6 * Math.Min(1, seconds)) / 20) * fade;
            var pcm = evaluator.Audio(default, sample, 1, default);
            Assert.All(pcm, value => Assert.InRange(Math.Abs(value - expected), 0, 1e-7));
        }
    }
}

