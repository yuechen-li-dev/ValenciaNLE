using FFmpeg.AutoGen;
using Valencia.Core;

namespace Valencia.Media;

public static unsafe class MediaProbe
{
    public static MediaInfo Read(string path)
    {
        FfmpegRuntime.Initialize();
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Media file not found.", path);
        }
        AVFormatContext* format = null;
        try
        {
            FfmpegRuntime.Check(ffmpeg.avformat_open_input(&format, path, null, null), "Open media");
            FfmpegRuntime.Check(ffmpeg.avformat_find_stream_info(format, null), "Read media streams");
            int video = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (video < 0)
            {
                throw new NotSupportedException("This file has no supported video stream/decoder.");
            }
            int audio = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            AVStream* stream = format->streams[video];
            if (stream->codecpar->width <= 0 || stream->codecpar->height <= 0 || (long)stream->codecpar->width * stream->codecpar->height > 33_177_600)
            {
                throw new NotSupportedException("M0 requires valid video dimensions, up to 8K.");
            }
            if (stream->codecpar->color_trc is AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 or AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67)
            {
                throw new NotSupportedException("HDR PQ/HLG playback needs tone mapping and is not supported in M0. Use an SDR source.");
            }
            var streams = new List<MediaStreamInfo>();
            for (int index = 0; index < format->nb_streams; index++)
            {
                AVCodecParameters* parameters = format->streams[index]->codecpar;
                string kind = parameters->codec_type switch
                {
                    AVMediaType.AVMEDIA_TYPE_VIDEO => "Video",
                    AVMediaType.AVMEDIA_TYPE_AUDIO => "Audio",
                    _ => "Other"
                };
                string details = kind == "Video"
                    ? $"{parameters->width} x {parameters->height}"
                    : $"{parameters->sample_rate} Hz / {parameters->ch_layout.nb_channels} ch";
                streams.Add(new MediaStreamInfo(index, kind, ffmpeg.avcodec_get_name(parameters->codec_id), details));
            }

            if (audio < 0 && streams.Any(item => item.Kind == "Audio"))
            {
                throw new NotSupportedException("This file contains audio, but no compatible audio decoder could be opened.");
            }
            double start = format->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : (double)format->start_time / ffmpeg.AV_TIME_BASE;
            double duration = format->duration == ffmpeg.AV_NOPTS_VALUE ? 0 : (double)format->duration / ffmpeg.AV_TIME_BASE;
            // Timestamp-offset containers can advertise the end timestamp as duration.
            // Prefer a stream's duration when available; Matroska commonly provides only the format end.
            if (stream->duration != ffmpeg.AV_NOPTS_VALUE && stream->duration > 0)
            {
                duration = (double)stream->duration * stream->time_base.num / stream->time_base.den
                    + Math.Max(0, (double)stream->start_time * stream->time_base.num / stream->time_base.den - start);
            }
            else if (start > 0 && duration > start)
            {
                duration -= start;
            }
            if (!double.IsFinite(duration) || duration <= 0)
            {
                throw new NotSupportedException("M0 requires a local, finite video with a known duration.");
            }
            AVRational rate = ffmpeg.av_guess_frame_rate(format, stream, null);
            return new MediaInfo(path, System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)format->iformat->name) ?? "Unknown", duration, start,
                stream->codecpar->width, stream->codecpar->height,
                rate.den > 0 ? (double)rate.num / rate.den : 0, video, audio >= 0 ? audio : null, streams);
        }
        finally
        {
            ffmpeg.avformat_close_input(&format);
        }
    }
}
