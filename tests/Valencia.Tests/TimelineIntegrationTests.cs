using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Media;
using Valencia.Persistence;
using Xunit;

namespace Valencia.Tests;

public sealed class TimelineFixture : IAsyncLifetime
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "Valencia.M1b", Guid.NewGuid().ToString("N"));
    public string A => Path.Combine(Root, "a.mp4");
    public string B => Path.Combine(Root, "b.mkv");
    public static Guid Id(int i) => new(i, 0, 0, new byte[8]);
    public static readonly FrameRate Rate = new(30000, 1001);
    public static SequenceTime F(long n) => TimelineTime.FromFrames(n, Rate);
    public ProjectSnapshot Snapshot { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        string font = "C\\:/Windows/Fonts/consola.ttf";
        foreach (var (path, hue, hz) in new[] { (A, 0, 440), (B, 100, 880) })
            await Run("-f", "lavfi", "-i", $"testsrc2=size=160x90:rate=30000/1001:duration=4,hue=h={hue},drawtext=fontfile='{font}':text='%{{n}}':x=4:y=4:fontsize=16:fontcolor=white:box=1:boxcolor=black",
                "-f", "lavfi", "-i", $"aevalsrc=0.08*sin(2*PI*{hz}*t)+if(lt(mod(t\\,0.5)\\,0.01)\\,0.4*sin(2*PI*220*t)\\,0):s=48000:d=4",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "30", "-bf", "2", "-c:a", "aac", path);
        var assets = new[] { MediaAssetProbe.Read(Id(2), A), MediaAssetProbe.Read(Id(3), B) };
        TimelineClip Clip(int id, int asset, TrackKind kind, long start, long source, long frames)
        {
            var stream = assets[asset].Streams.First(s => s.Kind == kind);
            var origin = new SourcePosition(new(0, stream.TimeBaseNumerator, stream.TimeBaseDenominator));
            return new(Id(id), assets[asset].Id, stream.Index, F(start), origin.Shift(F(source)), origin.Shift(F(source + frames)), new());
        }
        var sequence = new SequenceDocument(Id(4), "Edited", new(160, 90, Rate), 48000,
            [new(Id(5), TrackKind.Video, 0, "V1", new(), [Clip(10, 0, TrackKind.Video, 0, 30, 30), Clip(11, 1, TrackKind.Video, 45, 0, 30)]),
             new(Id(6), TrackKind.Video, 1, "V2", new(), [Clip(12, 0, TrackKind.Video, 60, 60, 15)]),
             new(Id(7), TrackKind.Audio, 2, "A1", new(), [Clip(13, 0, TrackKind.Audio, 0, 30, 30), Clip(14, 1, TrackKind.Audio, 45, 0, 30)]),
             new(Id(8), TrackKind.Audio, 3, "A2", new(), [Clip(15, 0, TrackKind.Audio, 60, 60, 15)])]);
        Snapshot = new(new(Id(1), 1, "Fixture", [.. assets], [sequence]), 0); DocumentValidator.Validate(Snapshot.Document);
    }
    public static async Task<byte[]> Run(params string[] args)
    {
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (string a in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args)) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!; Task<string> error = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream(); var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try { await process.WaitForExitAsync(timeout.Token); await copy; } catch { process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await error); return output.ToArray();
    }
    public Task DisposeAsync() { Directory.Delete(Root, true); return Task.CompletedTask; }
}

public sealed class TimelineIntegrationTests(TimelineFixture fixture) : IClassFixture<TimelineFixture>
{
    [Fact]
    [Trait("Category", "AudioHardware")]
    public async Task PausedPreviewShutdownDoesNotRequireUiContextPumping()
    {
        var playback = await SequencePlayback.OpenAsync(fixture.Snapshot, TimelineFixture.Id(4), default);
        var original = SynchronizationContext.Current;
        var context = new CountingContext();
        Task disposal;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            disposal = playback.DisposeAsync().AsTask();
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        await disposal.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, context.Posts);
    }

    private sealed class CountingContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref Posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    [Fact]
    public async Task UnsupportedSampleRateFailsInsteadOfWaitingForAnImpossiblePrebuffer()
    {
        var snapshot = fixture.Snapshot with { Document = fixture.Snapshot.Document with
        { Sequences = [fixture.Snapshot.Document.Sequences[0] with { AudioSampleRate = 384000 }] } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<NotSupportedException>(async () => { await using var ignored = await SequencePlayback.OpenAsync(snapshot, TimelineFixture.Id(4), default, timeout.Token); });
    }
    [Fact]
    public void SixHourSequenceOffsetResolvesRealMediaWithoutDecodingTheGap()
    {
        var shift = new SequenceTime(6L * 3600 * SequenceTime.TicksPerSecond);
        var sequence = fixture.Snapshot.Document.Sequences[0];
        var shifted = sequence with { Tracks = [.. sequence.Tracks.Select(t => t with
        { Clips = [.. t.Clips.Select(c => c with { Start = new(checked(c.Start.Ticks + shift.Ticks)) })] })] };
        var snapshot = fixture.Snapshot with { Document = fixture.Snapshot.Document with { Sequences = [shifted] } };
        using var far = new SequenceEvaluator(snapshot, sequence.Id);
        using var near = new SequenceEvaluator(fixture.Snapshot, sequence.Id);
        Assert.Equal(near.Video(TimelineFixture.F(45), default).Rgba, far.Video(new(shift.Ticks + TimelineFixture.F(45).Ticks), default).Rgba);
        Assert.All(far.Audio(shift, 48048, 24024, default), s => Assert.Equal(0f, s));
        Assert.Equal(near.Audio(default, 72072, 2048, default), far.Audio(shift, 72072, 2048, default));
    }
    [Fact]
    public async Task EvaluatorUsesActualSourcePtsCutsGapsAndLayerOrder()
    {
        using var evaluator = new SequenceEvaluator(fixture.Snapshot, TimelineFixture.Id(4));
        foreach (long frame in new long[] { 0, 29, 30, 44, 45, 59, 60, 74 })
        {
            var time = TimelineFixture.F(frame);
            var actual = evaluator.Video(time, default);
            if (frame is >= 30 and < 45) Assert.True(actual.Rgba.Where((_, i) => i % 4 < 3).All(v => v == 0));
            else
            {
                bool a = frame < 30 || frame >= 60;
                long source = frame < 30 ? frame + 30 : frame >= 60 ? frame : frame - 45;
                var expected = await TimelineFixture.Run("-i", a ? fixture.A : fixture.B, "-vf", $"select=eq(n\\,{source})", "-frames:v", "1", "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1");
                Assert.InRange(Error(actual.Rgba, expected), 0, 3.5);
            }
        }
    }
    [Fact]
    public void AudioGapIsExactAndMixMutePoliciesAreShared()
    {
        using var evaluator = new SequenceEvaluator(fixture.Snapshot, TimelineFixture.Id(4));
        var all = evaluator.Audio(default, 0, 120120, default);
        Assert.True(all.AsSpan(48048 * 2, 24024 * 2).ToArray().All(s => s == 0));
        Assert.True(Rms(all.AsSpan(1000, 10000)) > .03);
        var muted = fixture.Snapshot with { Document = fixture.Snapshot.Document with { Sequences = [fixture.Snapshot.Document.Sequences[0] with
        { Tracks = [.. fixture.Snapshot.Document.Sequences[0].Tracks.Select(t => t with { State = t.State with { Muted = true, Visible = false } })] }] } };
        using var silent = new SequenceEvaluator(muted, TimelineFixture.Id(4));
        Assert.All(silent.Audio(default, 0, 2048, default), s => Assert.Equal(0f, s));
        Assert.True(silent.Video(default, default).Rgba.Where((_, i) => i % 4 < 3).All(b => b == 0));
    }
    [Fact]
    public async Task RealExportIsIndependentlyDecodedAndMatchesReopenedDocument()
    {
        string project = Path.Combine(fixture.Root, "edit.valencia"); var files = new ProjectFile();
        await files.SaveAsync(project, fixture.Snapshot); var reopened = await files.LoadAsync(project);
        string output = Path.Combine(fixture.Root, "edited.mp4");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var result = await SequenceExport.RunAsync(reopened.Snapshot, TimelineFixture.Id(4), output, cancellation: timeout.Token);
        Assert.Equal(75, result.Frames); Assert.Equal(120120, result.AudioSamples);
        byte[] video = await TimelineFixture.Run("-i", output, "-an", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1");
        Assert.Equal(75 * 160 * 90 * 4, video.Length);
        foreach (int frame in new[] { 0, 29, 30, 44, 45, 59, 60, 74 })
        {
            var actual = video.AsSpan(frame * 57600, 57600).ToArray();
            if (frame is >= 30 and < 45) Assert.InRange(actual.Where((_, i) => i % 4 < 3).Average(b => (double)b), 0, 1.5);
            else
            {
                bool a = frame < 30 || frame >= 60; int source = frame < 30 ? frame + 30 : frame >= 60 ? frame : frame - 45;
                byte[] expected = await TimelineFixture.Run("-i", a ? fixture.A : fixture.B, "-vf", $"select=eq(n\\,{source})", "-frames:v", "1", "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1");
                Assert.InRange(Error(actual, expected), 0, 8);
            }
        }
        byte[] audioBytes = await TimelineFixture.Run("-i", output, "-vn", "-f", "f32le", "-ac", "2", "-ar", "48000", "pipe:1");
        float[] audio = MemoryMarshal.Cast<byte, float>(audioBytes).ToArray();
        Assert.InRange(audio.Length / 2, 120120, 121344);
        Assert.InRange(Rms(audio.AsSpan(55000 * 2, 10000 * 2)), 0, .001);
        Assert.True(Rms(audio.AsSpan(20000, 12000)) > .03);
        // Check the decoded AAC against an independently decoded/selected original audio window, allowing codec loss.
        byte[] sourceAudio = await TimelineFixture.Run("-i", fixture.A, "-vn", "-f", "f32le", "-ac", "2", "-ar", "48000", "pipe:1");
        float[] sourceSamples = MemoryMarshal.Cast<byte, float>(sourceAudio).ToArray();
        double difference = Math.Sqrt(Enumerable.Range(2000, 30000).Average(i => Math.Pow(audio[i] - sourceSamples[48048 * 2 + i], 2)));
        Assert.InRange(difference, 0, .015);
        Assert.True(DocumentSemantics.Equal(fixture.Snapshot.Document, reopened.Snapshot.Document));
    }
    [Fact]
    public async Task CancelledExportLeavesPreviousDestinationAndNoPartialFile()
    {
        string output = Path.Combine(fixture.Root, "cancel.mp4"); await File.WriteAllTextAsync(output, "previous");
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelProgress(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SequenceExport.RunAsync(fixture.Snapshot, TimelineFixture.Id(4), output, progress, cancellation.Token));
        Assert.Equal("previous", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.partial"));
    }
    [Fact]
    public async Task SeekGenerationsCancelAndBackwardPlaybackResolvesFreshSources()
    {
        using var cancellation = new CancellationTokenSource();
        var stale = SequencePlayback.OpenAsync(fixture.Snapshot, TimelineFixture.Id(4), TimelineFixture.F(60), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await using var ignored = await stale; });
        await using var fresh = await SequencePlayback.OpenAsync(fixture.Snapshot, TimelineFixture.Id(4), default);
        Assert.Equal(0, fresh.Position.Ticks); Assert.Contains(TimelineFixture.Id(10), fresh.Current!.VideoClips);
        fresh.Play(); await Task.Delay(220); fresh.Update(); fresh.Pause();
        Assert.InRange(fresh.Position.TotalSeconds, .1, .4);
    }
    private static double Error(byte[] actual, byte[] expected) { Assert.Equal(actual.Length, expected.Length); return actual.Where((_, i) => i % 4 < 3).Zip(expected.Where((_, i) => i % 4 < 3), (a, b) => Math.Abs(a - b)).Average(); }
    private static double Rms(ReadOnlySpan<float> values) { double sum = 0; foreach (float value in values) sum += value * value; return Math.Sqrt(sum / values.Length); }
    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<ExportProgress> { public void Report(ExportProgress value) { if (value.Frames >= 5) cancellation.Cancel(); } }
}
