using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Media;

public sealed record ProxyData(int Version, string Key, string SourceIdentity, int SourceStream, int Width, int Height,
    string Codec, string ClockPolicy, string VideoIdentity, long Bytes);
public sealed record ProxySource(MediaAsset Asset, AssetStream Stream);

/// <summary>Regenerable all-intra H.264 video proxies. Exact presentation clocks are retained; audio stays on originals.</summary>
public static class MediaProxy
{
    private static readonly SemaphoreSlim Writer = new(1, 1);
    private const long Budget = 4L * 1024 * 1024 * 1024;
    public static string DirectoryPath => Environment.GetEnvironmentVariable("VALENCIA_PROXY_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valencia", "Proxies-v1");
    private static string PathFor(string key, string suffix)
    {
        if (key.Length != 64 || !key.All(char.IsAsciiHexDigit)) throw new ArgumentException("Invalid proxy identity.");
        return Path.Combine(DirectoryPath, key + suffix);
    }
    private static string Identity(string path)
    {
        var info = new FileInfo(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{info.Length}|{info.LastWriteTimeUtc.Ticks}|"));
        using var file = File.OpenRead(path);
        byte[] buffer = new byte[65536];
        foreach (long offset in new[] { 0L, Math.Max(0, info.Length / 2 - 32768), Math.Max(0, info.Length - buffer.Length) })
        {
            file.Position = offset;
            int count = file.Read(buffer);
            hash.AppendData(buffer.AsSpan(0, count));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public static ProxySource? Resolve(MediaAsset asset, AssetStream stream)
    {
        if (asset.ProxyKey is not { } key) return null;
        var data = Read(key);
        if (data is null || data.SourceStream != stream.Index || Identity(asset.SourcePath) != data.SourceIdentity) return null;
        string video = PathFor(key, ".mp4");
        try
        {
            if (!File.Exists(video) || Identity(video) != data.VideoIdentity) return null;
            var imported = MediaAssetProbe.Read(asset.Id, video);
            File.SetLastWriteTimeUtc(PathFor(key, ".json"), DateTime.UtcNow);
            return new(imported, imported.Streams.First(s => s.Kind == TrackKind.Video));
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { return null; }
    }
    public static ProxyData? Read(string key)
    {
        try
        {
            string path = PathFor(key, ".json");
            if (!File.Exists(path) || new FileInfo(path).Length > 16384) return null;
            var data = JsonSerializer.Deserialize<ProxyData>(File.ReadAllText(path));
            return data?.Version == 1 && data.Key == key ? data : null;
        }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }
    public static async Task<ProxyData> GenerateAsync(MediaAsset asset, int streamIndex, CancellationToken cancellation)
    {
        var stream = asset.Streams.First(s => s.Index == streamIndex && s.Kind == TrackKind.Video);
        if (stream.TimeBaseNumerator != 1) throw new NotSupportedException("Proxy profile currently requires a reciprocal-integer source timebase; use originals otherwise.");
        string sourceIdentity = Identity(asset.SourcePath);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"proxy-v1|{sourceIdentity}|{streamIndex}|h264-intra-crf20-max640|{stream.TimeBaseDenominator}")));
        await Writer.WaitAsync(cancellation).ConfigureAwait(false);
        string? temporary = null, manifest = null;
        try
        {
            var attached = asset with { ProxyKey = key };
            if (Resolve(attached, stream) is not null) return Read(key)!;
            Directory.CreateDirectory(DirectoryPath);
            temporary = Path.Combine(DirectoryPath, ".proxy-" + Guid.NewGuid().ToString("N") + ".mp4");
            await MediaProcess.RunAsync(["-v", "error", "-nostats", "-y", "-copyts", "-i", asset.SourcePath, "-map", $"0:{streamIndex}", "-an",
                "-vf", "scale=w='min(640,iw)':h=-2", "-fps_mode", "passthrough", "-enc_time_base:v", "demux", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-g", "1",
                "-bf", "0", "-pix_fmt", "yuv420p", "-video_track_timescale", stream.TimeBaseDenominator.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-avoid_negative_ts", "disabled", "-use_editlist", "0", "-fs", (Budget + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", "mp4", temporary], cancellation).ConfigureAwait(false);
            var proxy = MediaAssetProbe.Read(asset.Id, temporary);
            var output = proxy.Streams.First(s => s.Kind == TrackKind.Video);
            if (stream.Start is { } start && (output.Start is not { } actual || !new SourcePosition(start).EquivalentTo(new(actual))))
                throw new NotSupportedException("Proxy muxer changed source start timestamps; the proxy cannot be published.");
            long bytes = new FileInfo(temporary).Length;
            if (bytes > Budget) throw new IOException("Proxy exceeds the four-GiB cache budget; use originals or a narrower recording.");
            var original = MediaAssetProbe.Import(asset.Id, asset.SourcePath);
            var generated = MediaAssetProbe.Import(asset.Id, temporary);
            if (Math.Abs(original.Duration.TotalSeconds - generated.Duration.TotalSeconds) > .25) throw new NotSupportedException("Proxy duration differs from the original; use original media.");
            if (Identity(asset.SourcePath) != sourceIdentity) throw new IOException("Source changed during proxy generation.");
            cancellation.ThrowIfCancellationRequested();
            // Published data identity includes its post-rename mtime; a metadata commit makes it discoverable.
            File.Move(temporary, PathFor(key, ".mp4"), true); temporary = null;
            var result = new ProxyData(1, key, sourceIdentity, streamIndex, output.Width!.Value, output.Height!.Value,
                "H.264 all-intra CRF20", "Absolute original PTS; demux timebase; VFR passthrough; originals for audio/export", Identity(PathFor(key, ".mp4")), bytes);
            manifest = Path.Combine(DirectoryPath, ".proxy-" + Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(result), CancellationToken.None).ConfigureAwait(false);
            File.Move(manifest, PathFor(key, ".json"), true); manifest = null;
            long total = new DirectoryInfo(DirectoryPath).EnumerateFiles("*.mp4").Sum(f => f.Length);
            foreach (var file in new DirectoryInfo(DirectoryPath).EnumerateFiles("*.json").OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= Budget) break;
                if (file.Name.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
                string video = Path.ChangeExtension(file.FullName, ".mp4");
                if (File.Exists(video)) { long length = new FileInfo(video).Length; File.Delete(video); total -= length; }
                file.Delete();
            }
            return result;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            if (manifest is not null && File.Exists(manifest)) File.Delete(manifest);
            Writer.Release();
        }
    }
}
