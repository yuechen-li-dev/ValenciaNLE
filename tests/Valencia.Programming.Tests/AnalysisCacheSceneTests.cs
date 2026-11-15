using Valencia.Analysis;
using Valencia.Core;
using Valencia.Engine;
using Valencia.Media;
using Xunit;

namespace Valencia.Programming.Tests;

public sealed class AnalysisCacheTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Valencia-analysis-cache", Guid.NewGuid().ToString("N"));
    public AnalysisCacheTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private static AnalysisArtifact Artifact(string key) => new(1, key, Guid.NewGuid(), "source", 0, "audio", MediaAnalyzer.AudioAlgorithm,
        "{}", MediaAnalyzer.DecoderPolicy, new(0, 1, 1), "complete", 8000, 1, 1, [], [], [], [], []);
    [Fact]
    public async Task CacheRoundtripCorruptionCancellationAndVersionInvalidation()
    {
        var cache = new AnalysisCache(root);
        string key = AnalysisCache.Key(Guid.Empty, "source", 0, "algorithm-v1", "{}");
        Assert.Null(await cache.ReadAsync(key));
        var artifact = Artifact(key);
        await cache.PublishAsync(artifact, default);
        Assert.Equal(AnalysisJson.Write(artifact), AnalysisJson.Write(await cache.ReadAsync(key)));
        byte[] previous = File.ReadAllBytes(Path.Combine(root, key + ".vca"));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.PublishAsync(artifact, cancel.Token));
        Assert.Equal(previous, File.ReadAllBytes(Path.Combine(root, key + ".vca")));
        Assert.Empty(Directory.GetFiles(root, "*.partial"));
        Assert.NotEqual(key, AnalysisCache.Key(Guid.Empty, "source", 0, "algorithm-v2", "{}"));
        Assert.NotEqual(key, AnalysisCache.Key(Guid.Empty, "source", 0, "algorithm-v1", "{\"thresholdDb\":-20}"));
        previous[^1] ^= 1;
        File.WriteAllBytes(Path.Combine(root, key + ".vca"), previous);
        Assert.Null(await cache.ReadAsync(key));
        Assert.False(File.Exists(Path.Combine(root, key + ".vca")));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.PublishAsync(artifact with { Status = "partial" }, default));
    }
    [Fact]
    public async Task EvictionAndConcurrentEquivalentPublicationStayWithinDiskBudget()
    {
        var initial = new AnalysisCache(root);
        string a = new('A', 64), b = new('B', 64);
        var artifact = Artifact(a);
        await initial.PublishAsync(artifact, default);
        long length = new FileInfo(Path.Combine(root, a + ".vca")).Length;
        var bounded = new AnalysisCache(root, length + 100);
        await bounded.PublishAsync(artifact with { Key = b }, default);
        Assert.Null(await bounded.ReadAsync(a));
        Assert.NotNull(await bounded.ReadAsync(b));
        Assert.True(new DirectoryInfo(root).GetFiles("*.vca").Sum(f => f.Length) <= length + 100);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => new AnalysisCache(root, length + 100).PublishAsync(artifact with { Key = b }, default)));
        Assert.NotNull(await bounded.ReadAsync(b));
    }
    [Fact]
    public void QuickFingerprintDetectsSampleAndMetadataChanges()
    {
        string file = Path.Combine(root, "source.bin");
        File.WriteAllBytes(file, new byte[200000]);
        var stamp = File.GetLastWriteTimeUtc(file);
        string before = AnalysisCache.Fingerprint(file);
        using (var stream = File.OpenWrite(file)) { stream.Position = 100000; stream.WriteByte(1); }
        File.SetLastWriteTimeUtc(file, stamp);
        Assert.NotEqual(before, AnalysisCache.Fingerprint(file));
    }
}

public sealed class SceneAnalysisTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Valencia-scenes", Guid.NewGuid().ToString("N"));
    public SceneAnalysisTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    [Theory]
    [InlineData("static", 0)]
    [InlineData("fade", 0)]
    [InlineData("cut", 1)]
    [InlineData("vfr", 1)]
    public async Task ActualLumaDecodeDetectsCutsAndKeepsOriginalVfrTimestamps(string kind, int count)
    {
        string path = Path.Combine(root, kind + ".mkv");
        string filter = kind switch
        {
            "static" => "color=black:size=64x36:rate=30:duration=2",
            "fade" => "color=white:size=64x36:rate=30:duration=2,fade=t=in:st=0:d=2",
            "cut" => "color=black:size=64x36:rate=30:duration=1[b];color=white:size=64x36:rate=30:duration=1[w];[b][w]concat=n=2:v=1:a=0",
            _ => "color=black:size=64x36:rate=30:duration=1[b];color=white:size=64x36:rate=30:duration=1[w];[b][w]concat=n=2:v=1:a=0,setpts='if(lt(N,30),N/30/TB,(1+(N-30)/15)/TB)'"
        };
        await AnalysisFixture.Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", filter, "-fps_mode", "vfr", "-c:v", "libx264", "-pix_fmt", "yuv420p", path);
        var asset = MediaAssetProbe.Read(Guid.NewGuid(), path);
        string fingerprint = AnalysisCache.Fingerprint(path);
        var settings = new SceneSettings();
        var result = MediaAnalyzer.Scenes(asset, 0, settings, "test", fingerprint, null, default);
        Assert.Equal(count, result.Scenes.Length);
        if (count > 0)
        {
            Assert.Equal(1, result.Scenes[0].Timestamp.TotalSeconds);
            Assert.True(result.Scenes[0].Score >= .35);
            Assert.Equal(asset.Streams[0].TimeBaseDenominator, result.Scenes[0].Timestamp.Denominator);
        }
        Assert.Equal(AnalysisJson.Write(result), AnalysisJson.Write(MediaAnalyzer.Scenes(asset, 0, settings, "test", fingerprint, null, default)));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MediaAnalyzer.Scenes(asset, 0, settings, "test", fingerprint, null, stop.Token));
    }
}
