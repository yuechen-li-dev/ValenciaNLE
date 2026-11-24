using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Media;

/// <summary>Import metadata adapter. Reads original stream clocks directly; M0's display doubles are never edit positions.</summary>
public static unsafe class MediaAssetProbe
{
    public static MediaAsset Read(Guid assetId, string path) => Import(assetId, path).Asset;
    public static ImportedAsset Import(Guid assetId, string path)
    {
        FfmpegRuntime.Initialize();
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Media file not found.", path);
        AVFormatContext* format = null;
        try
        {
            FfmpegRuntime.Check(ffmpeg.avformat_open_input(&format, path, null, null), "Open asset");
            FfmpegRuntime.Check(ffmpeg.avformat_find_stream_info(format, null), "Read asset streams");
            string container = Marshal.PtrToStringUTF8((nint)format->iformat->name) ?? "Unknown";
            var streams = ImmutableArray.CreateBuilder<AssetStream>();
            for (int index = 0; index < format->nb_streams; index++)
            {
                AVStream* stream = format->streams[index];
                AVCodecParameters* parameters = stream->codecpar;
                if (parameters->codec_type is not (AVMediaType.AVMEDIA_TYPE_VIDEO or AVMediaType.AVMEDIA_TYPE_AUDIO)) continue;
                bool video = parameters->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO;
                MediaTimestamp? start = stream->start_time == ffmpeg.AV_NOPTS_VALUE ? null
                    : new(stream->start_time, stream->time_base.num, stream->time_base.den);
                // The WAV demuxer reports duration in stream ticks but often omits its implicit zero origin.
                if (!video && container == "wav" && start is null && stream->duration > 0)
                    start = new(0, stream->time_base.num, stream->time_base.den);
                MediaTimestamp? end = start is { } streamOrigin && stream->duration > 0
                    ? new(checked(streamOrigin.Value + stream->duration), streamOrigin.Numerator, streamOrigin.Denominator) : null;
                AVRational rate = video ? ffmpeg.av_guess_frame_rate(format, stream, null) : default;
                streams.Add(new(index, video ? TrackKind.Video : TrackKind.Audio, ffmpeg.avcodec_get_name(parameters->codec_id),
                    stream->time_base.num, stream->time_base.den, start, end,
                    video && parameters->width > 0 ? parameters->width : null,
                    video && parameters->height > 0 ? parameters->height : null,
                    !video && parameters->sample_rate > 0 ? parameters->sample_rate : null,
                    !video && parameters->ch_layout.nb_channels > 0 ? parameters->ch_layout.nb_channels : null,
                    rate.num > 0 && rate.den > 0 ? new FrameRate(rate.num, rate.den) : null));
            }
            if (streams.Count == 0) throw new NotSupportedException("Source has no video/audio streams to register.");
            var asset = new MediaAsset(assetId, Path.GetFileName(path), path, container, streams.ToImmutable());
            var origin = new MediaTimestamp(format->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : format->start_time, 1, ffmpeg.AV_TIME_BASE);
            var primary = asset.Streams.FirstOrDefault(s => s.Kind == TrackKind.Video) ?? asset.Streams[0];
            SequenceTime duration;
            if (primary.Start is { } s && primary.End is { } e) duration = new SourcePosition(e).Since(new(s));
            else
            {
                if (format->duration == ffmpeg.AV_NOPTS_VALUE || format->duration <= 0) throw new NotSupportedException("Import requires a finite known container duration.");
                long value = format->duration;
                if (origin.Value > 0 && value > origin.Value) value -= origin.Value;
                duration = TimelineTime.Convert(new(value, 1, ffmpeg.AV_TIME_BASE), TimeRounding.Floor);
            }
            return new(asset, duration, origin);
        }
        finally { ffmpeg.avformat_close_input(&format); }
    }
}

public sealed record ImportedAsset(MediaAsset Asset, SequenceTime Duration, MediaTimestamp ContainerStart);
