using System.Diagnostics;
using Valencia.Analysis;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Media;
using Xunit;

namespace Valencia.Programming.Tests;

public sealed partial class TranscriptionTests
{
    [Theory]
    [InlineData(44100, 2)]
    [InlineData(48000, 1)]
    [InlineData(16000, 2)]
    public async Task PreprocessingPreservesExactSampleClockAndDownmixes(int rate, int channels)
    {
        string file = Path.Combine(root, "pcm.wav");
        string signal = channels == 2 ? "0.25*sin(2*PI*440*t)|-0.25*sin(2*PI*440*t)" : "0.25*sin(2*PI*440*t)";
        await AnalysisFixture.Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", $"aevalsrc=exprs='{signal}':s={rate}:d=1", "-c:a", "pcm_s16le", file);
        var asset = MediaAssetProbe.Read(Guid.NewGuid(), file);
        long samples = 0;
        float maximum = 0;
        AnalysisStream.Transcription(asset, 0, block =>
        {
            Assert.Equal(0, block.Epoch);
            Assert.Equal(samples, block.FirstSample);
            samples += block.Samples.Length;
            maximum = Math.Max(maximum, block.Samples.Select(Math.Abs).DefaultIfEmpty().Max());
        }, default);
        Assert.Equal(16000, samples);
        Assert.InRange(maximum, channels == 2 ? 0 : .24f, channels == 2 ? .0001f : .26f);
    }

    [Fact]
    public async Task SilentTranscriptCompletesEmptyAndInvalidTextPlansCannotMutate()
    {
        string file = Path.Combine(root, "silent.wav");
        await AnalysisFixture.Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono:d=6", file);
        var asset = MediaAssetProbe.Read(Guid.NewGuid(), file);
        var result = WhisperTranscriber.Run(asset, 0, new(), "silent", AnalysisCache.Fingerprint(file), new(Path.Combine(root, "silence-cache")), null, default);
        Assert.Empty(result.Transcript!.Words);
        Assert.Equal("complete", result.Status);
        await using var engine = await Engine();
        var transcript = await Transcribe(engine);
        var snapshot = engine.Session.Snapshot;
        Assert.Throws<ArgumentException>(() => TranscriptEditing.Create(snapshot, transcript, new(engine.Query().Sequences[0].Id, transcript.Key, "retain", [])));
        Assert.Throws<ArgumentException>(() => TranscriptEditing.Create(snapshot, transcript with { Status = "partial" }, new(engine.Query().Sequences[0].Id, transcript.Key, "remove", [transcript.Transcript!.Words[0].Id])));
        Assert.Same(snapshot, engine.Session.Snapshot);
        var clip = engine.Query().Sequences[0].Tracks.First().Clips.First();
        Assert.NotEmpty(TranscriptQueries.ForClip(transcript, clip));
        Assert.Empty(TranscriptQueries.Range(transcript, new(new(100, 1, 1)), new(new(101, 1, 1))));
        Assert.Single(TranscriptQueries.Search(transcript, "ANNOUNCEMENT!", exactWord: true));
    }

    [Fact]
    public async Task FullWhisperWindowRecoversTheSpokenTail()
    {
        string file = Path.Combine(root, "spoken-tail.wav");
        await AnalysisFixture.Run("ffmpeg", "-v", "error", "-y", "-stream_loop", "-1",
            "-i", Path.Combine(root, "speech.wav"), "-t", "30", "-ar", "16000", file);
        var asset = MediaAssetProbe.Read(Guid.NewGuid(), file);
        var result = WhisperTranscriber.Run(asset, 0, new(), "spoken-tail", AnalysisCache.Fingerprint(file),
            new(Path.Combine(root, "spoken-tail-cache")), null, default);
        var tail = result.Transcript!.Words.TakeLast(3).ToArray();
        Assert.Equal(new[] { "welcome", "to", "valencia" }, tail.Select(word => WhisperTranscriber.Normalize(word.Text)));
        Assert.All(tail, word =>
        {
            Assert.True(word.TimingUsable);
            Assert.InRange(AnalysisTime.Normalize(word.Range.Start, result.ContainerStart).TotalSeconds, 28, 30);
        });
    }

    [Fact]
    public async Task RepeatedContinuousSpeechUsesOverlapOwnershipAndNativeCancellation()
    {
        string file = Path.Combine(root, "continuous.wav");
        await AnalysisFixture.Run("ffmpeg", "-v", "error", "-y", "-stream_loop", "-1", "-i", Path.Combine(root, "speech.wav"), "-t", "120", "-ar", "16000", file);
        var asset = MediaAssetProbe.Read(Guid.NewGuid(), file);
        var timer = Stopwatch.StartNew();
        var result = WhisperTranscriber.Run(asset, 0, new(), "continuous", AnalysisCache.Fingerprint(file), new(Path.Combine(root, "continuous-cache")), null, default);
        int announcements = result.Transcript!.Words.Count(w => WhisperTranscriber.Normalize(w.Text) == "announcement");
        Assert.InRange(announcements, 11, 14);
        Assert.True(result.Transcript.CompletedChunks >= 4);
        Assert.All(result.Transcript.Words, w => Assert.InRange(AnalysisTime.Normalize(w.Range.Start, result.ContainerStart).TotalSeconds, 0, 120));
        log.WriteLine("M2C_CONTINUOUS=" + EngineJson.Write(new
        {
            audioSeconds = 120,
            wallSeconds = timer.Elapsed.TotalSeconds,
            words = result.Transcript.Words.Length,
            announcements,
            result.Transcript.CompletedChunks
        }));
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(150);
        timer.Restart();
        Assert.ThrowsAny<OperationCanceledException>(() => WhisperTranscriber.Run(asset, 0, new(ChunkSeconds: 29), "cancel-continuous",
            AnalysisCache.Fingerprint(file), new(Path.Combine(root, "cancel-cache")), null, cancellation.Token));
        Assert.InRange(timer.Elapsed.TotalSeconds, .1, 10);
        log.WriteLine("M2C_CANCEL=" + EngineJson.Write(new { totalMs = timer.Elapsed.TotalMilliseconds, requestedAfterMs = 150, completedArtifactPublished = false }));
    }
}
