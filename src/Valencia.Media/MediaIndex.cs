using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Media;

public sealed record SourceIndexData(int StreamIndex, string Policy, SourcePosition Through, int Anchors);
public static class MediaIndex
{
    public static SourceIndexData Build(MediaAsset asset, int streamIndex, CancellationToken cancellation)
    {
        var imported = MediaAssetProbe.Import(asset.Id, asset.SourcePath);
        var stream = asset.Streams.First(s => s.Index == streamIndex);
        var end = stream.End is { } bound ? new SourcePosition(bound) : new SourcePosition(imported.ContainerStart).Shift(imported.Duration);
        if (stream.Kind == TrackKind.Video) return new(streamIndex, "FFmpeg demuxer keyframe/cue index + preceding-keyframe decode-forward, actual presentation PTS", end, 0);
        return AudioSeekIndex.Build(asset, stream, end, cancellation);
    }
}
