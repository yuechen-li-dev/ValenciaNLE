using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Media;

public sealed record ProxyData(int Version, string Key, string SourceIdentity, int SourceStream, int Width, int Height,
    string Codec, string ClockPolicy, string VideoIdentity, long Bytes);
public sealed record ProxySource(MediaAsset Asset, AssetStream Stream);
public enum PreviewProxyPolicy { Automatic, Always, Never }

/// <summary>Regenerable seek-friendly proxies. Exact presentation clocks are retained; audio stays on originals.</summary>
public static class MediaProxy
{
    private static readonly SemaphoreSlim Writer = new(1, 1);
    private const long Budget = 4L * 1024 * 1024 * 1024;
    public static PreviewProxyPolicy Policy => Environment.GetEnvironmentVariable("VALENCIA_PREVIEW_PROXY")?.ToLowerInvariant() switch
    {
        "always" => PreviewProxyPolicy.Always,
        "never" => PreviewProxyPolicy.Never,
        _ => PreviewProxyPolicy.Automatic
    };
    public static string DirectoryPath => Environment.GetEnvironmentVariable("VALENCIA_PROXY_DIR")
        ?? Path.Combine(ValenciaData.Root, "Proxies-v1");

    public static bool ShouldPrepare(AssetStream stream, SequenceTime? duration = null) => Policy switch
    {
        PreviewProxyPolicy.Never => false,
        PreviewProxyPolicy.Always => stream.Kind == TrackKind.Video,
        _ => stream.Kind == TrackKind.Video && ((long)(stream.Width ?? 0) * (stream.Height ?? 0) > 1920 * 1080
            || (duration?.TotalSeconds ?? (stream.End?.TotalSeconds - stream.Start?.TotalSeconds)) >= 1200)
    };

    private static string Key(string sourceIdentity, AssetStream stream) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"proxy-v2|{sourceIdentity}|{stream.Index}|vp9-12-max640|{stream.TimeBaseDenominator}")));
    private static string PathFor(string key, string suffix)
    {
        if (key.Length != 64 || !key.All(char.IsAsciiHexDigit)) throw new ArgumentException("Invalid proxy identity.");
        return Path.Combine(DirectoryPath, key + suffix);
    }
    internal static string Identity(string path)
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
        try
        {
            var data = Read(key);
            if (data is null || data.SourceStream != stream.Index || Identity(asset.SourcePath) != data.SourceIdentity) return null;
            string video = PathFor(key, ".mp4");
            if (!File.Exists(video) || Identity(video) != data.VideoIdentity) return null;
            var imported = MediaAssetProbe.Read(asset.Id, video);
            File.SetLastWriteTimeUtc(PathFor(key, ".json"), DateTime.UtcNow);
            return new(imported, imported.Streams.First(s => s.Kind == TrackKind.Video));
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { return null; }
    }

    internal static ProxySource? ResolvePreview(MediaAsset asset, AssetStream stream)
    {
        if (Policy == PreviewProxyPolicy.Never) return null;
        if (Resolve(asset, stream) is { } attached) return attached;
        if (stream.TimeBaseNumerator != 1 || !Directory.Exists(DirectoryPath)) return null;
        try { return Resolve(asset with { ProxyKey = Key(Identity(asset.SourcePath), stream) }, stream); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
    public static ProxyData? Read(string key)
    {
        try
        {
            string path = PathFor(key, ".json");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length > 16384) return null;
            var data = JsonSerializer.Deserialize<ProxyData>(input);
            return data?.Version is 1 or 2 && data.Key == key ? data : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    public static async Task<ProxyData> GenerateAsync(MediaAsset asset, int streamIndex, CancellationToken cancellation)
    {
        var stream = asset.Streams.First(s => s.Index == streamIndex && s.Kind == TrackKind.Video);
        cancellation.ThrowIfCancellationRequested();
        if (stream.TimeBaseNumerator != 1) throw new NotSupportedException("Proxy profile currently requires a reciprocal-integer source timebase; use originals otherwise.");
        string sourceIdentity = Identity(asset.SourcePath);
        string key = Key(sourceIdentity, stream);
        await Writer.WaitAsync(cancellation).ConfigureAwait(false);
        string? temporary = null, manifest = null;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            using var commit = await AcquireWriterAsync(cancellation).ConfigureAwait(false);
            var attached = asset with { ProxyKey = key };
            if (Resolve(attached, stream) is not null) return Read(key)!;
            temporary = Path.Combine(DirectoryPath, ".proxy-" + Guid.NewGuid().ToString("N") + ".mp4");
            string timescale = stream.TimeBaseDenominator.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await MediaProcess.RunBackgroundAsync(["-v", "error", "-nostats", "-y", "-copyts", "-noautorotate", "-threads", "2", "-i",
                asset.SourcePath, "-map", $"0:{streamIndex}", "-an", "-vf", "scale=w='min(640,iw)':h=-2", "-filter_threads", "2",
                "-fps_mode", "passthrough", "-enc_time_base:v", "demux", "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "7",
                "-threads", "2", "-row-mt", "1", "-crf", "30", "-b:v", "1M", "-maxrate", "2M", "-bufsize", "2M", "-g", "12",
                "-lag-in-frames", "0", "-auto-alt-ref", "0", "-pix_fmt", "yuv420p", "-video_track_timescale", timescale, "-movie_timescale", timescale,
                "-avoid_negative_ts", "disabled", "-use_editlist", "1", "-fs", (Budget + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", "mp4", temporary], cancellation).ConfigureAwait(false);
            var proxy = MediaAssetProbe.Read(asset.Id, temporary);
            var output = proxy.Streams.First(s => s.Kind == TrackKind.Video);
            if (stream.Start is { } start && (output.Start is not { } actual || !new SourcePosition(start).EquivalentTo(new(actual))))
                throw new NotSupportedException("Proxy muxer changed source start timestamps; the proxy cannot be published.");
            long bytes = new FileInfo(temporary).Length;
            if (bytes > Budget) throw new IOException("Proxy exceeds the four-GiB cache budget; use originals or a narrower recording.");
            if (stream.TimeBaseDenominator != output.TimeBaseDenominator || output.TimeBaseNumerator != 1
                || VideoPacketClock.Read(asset.SourcePath, streamIndex, cancellation) != VideoPacketClock.Read(temporary, output.Index, cancellation))
                throw new NotSupportedException("Proxy changed the original video packet presentation clock; use original media.");
            if (Identity(asset.SourcePath) != sourceIdentity) throw new IOException("Source changed during proxy generation.");
            cancellation.ThrowIfCancellationRequested();
            // Published data identity includes its post-rename mtime; a metadata commit makes it discoverable.
            File.Move(temporary, PathFor(key, ".mp4"), true); temporary = null;
            var result = new ProxyData(2, key, sourceIdentity, streamIndex, output.Width!.Value, output.Height!.Value,
                "VP9 short GOP", "Exact original packet PTS count and order in presentation time; originals for audio/export", Identity(PathFor(key, ".mp4")), bytes);
            manifest = Path.Combine(DirectoryPath, ".proxy-" + Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(result), CancellationToken.None).ConfigureAwait(false);
            File.Move(manifest, PathFor(key, ".json"), true); manifest = null;
            long total = new DirectoryInfo(DirectoryPath).EnumerateFiles("*.mp4").Sum(f => f.Length);
            foreach (var file in new DirectoryInfo(DirectoryPath).EnumerateFiles("*.json").OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= Budget) break;
                if (file.Name.Length != 69 || !file.Name[..64].All(char.IsAsciiHexDigit)) continue;
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

    private static async Task<FileStream> AcquireWriterAsync(CancellationToken cancellation)
    {
        string path = Path.Combine(DirectoryPath, ".write.lock");
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            { await Task.Delay(100, cancellation).ConfigureAwait(false); }
        }
    }
}
