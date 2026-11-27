using FFmpeg.AutoGen;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Media;

/// <summary>One demux/decoder context, confined to its worker. Audio and video use independent contexts.</summary>
internal sealed unsafe class FfmpegDecoder : IDisposable
{
    private AVFormatContext* format;
    private AVCodecContext* codec;
    private AVPacket* packet;
    private AVFrame* frame;
    private AVFrame* retainedVideo;
    private AVFrame* downloadedVideo;
    private AVFrame* planarVideo;
    private SwsContext* scaler;
    private SwsContext* planeScaler;
    private SwrContext* resampler;
    private readonly int streamIndex;
    private int outputSampleRate = AudioStreamBuffer.SampleRate;
    private int outputChannels = 2;
    private bool draining;
    private bool finished;
    private bool disposed;
    private double lastReadMilliseconds;
    private readonly string? hardwareFallback;
    private bool HasHardwareDevice => codec->hw_device_ctx != null;
    private const long VideoHistoryBudget = 64 * 1024 * 1024;
    private const int MaximumHistoryFrames = 64;
    private readonly Queue<HistoryPicture> videoHistory = new();
    private long videoHistoryBytes;
    private VideoFrame? convertedHistory;
    private readonly record struct HistoryPicture(nint Frame, MediaTimestamp Timestamp, long Bytes);

    public FfmpegDecoder(MediaInfo info, int streamIndex, double seekSeconds, bool requestHardware = false)
    {
        this.streamIndex = streamIndex;
        try
        {
            AVFormatContext* opened = null;
            int openResult = ffmpeg.avformat_open_input(&opened, info.Path, null, null);
            format = opened;
            FfmpegRuntime.Check(openResult, "Open decoder source");
            FfmpegRuntime.Check(ffmpeg.avformat_find_stream_info(format, null), "Read decoder streams");
            if (streamIndex < 0 || streamIndex >= format->nb_streams) throw new InvalidDataException("Source stream no longer exists.");
            AVStream* stream = format->streams[streamIndex];
            var codecId = stream->codecpar->codec_id;
            AVCodec* software = ffmpeg.avcodec_find_decoder(codecId);
            AVCodec* decoder = requestHardware ? VideoDecodeProvider.SelectDecoder(software, codecId) : software;
            if (decoder == null)
            {
                throw new NotSupportedException($"Decoder unavailable for {ffmpeg.avcodec_get_name(stream->codecpar->codec_id)}.");
            }
            codec = ffmpeg.avcodec_alloc_context3(decoder);
            if (codec == null)
            {
                throw new OutOfMemoryException("FFmpeg codec context allocation failed.");
            }
            FfmpegRuntime.Check(ffmpeg.avcodec_parameters_to_context(codec, stream->codecpar), "Configure decoder");
            codec->thread_count = Math.Min(Environment.ProcessorCount, 4);
            if (requestHardware)
            {
                hardwareFallback = VideoDecodeProvider.Configure(codec, decoder);
                if (hardwareFallback is not null && decoder != software)
                    throw new HardwareVideoDecodeException(hardwareFallback);
            }
            CheckDecode(ffmpeg.avcodec_open2(codec, decoder, null), "Start decoder");
            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            if (packet == null || frame == null)
            {
                throw new OutOfMemoryException("FFmpeg frame/packet allocation failed.");
            }
            if (seekSeconds > 0)
            {
                long timestamp = checked((long)((seekSeconds + info.StartSeconds) * ffmpeg.AV_TIME_BASE));
                FfmpegRuntime.Check(ffmpeg.av_seek_frame(format, -1, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD), "Seek media");
                ffmpeg.avcodec_flush_buffers(codec);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public FfmpegDecoder(MediaAsset asset, AssetStream stream, SourcePosition seek, int sampleRate, bool requestHardware = false)
        : this(new MediaInfo(asset.SourcePath, asset.Container, 0, 0, 0, 0, 0, stream.Index, null, []), stream.Index, 0, requestHardware)
    {
        outputSampleRate = sampleRate;
        try
        {
            if (stream.Start is { } first && seek.CompareTo(new(first)) <= 0) return;
            // Matroska commonly indexes only video keyframes. Seeking against an
            // audio stream bypasses those cues and can scan hours of interleaved
            // packets. Use the container's video seek reference, then decode the
            // selected audio stream forward to its exact sample-count anchor.
            // The timebase conversion is rational; the reference does not change
            // the audio stream's clock or its sample phase.
            int referenceIndex = stream.Index;
            if (stream.Kind == TrackKind.Audio)
            {
                for (int index = 0; index < format->nb_streams; index++)
                {
                    if (format->streams[index]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        referenceIndex = index;
                        break;
                    }
                }
            }
            AVRational seekTimeBase = format->streams[referenceIndex]->time_base;
            long timestamp = TimelineTime.Rebase(seek, seekTimeBase.num, seekTimeBase.den, TimeRounding.Floor).Value;
            long referenceStart = format->streams[referenceIndex]->start_time;
            if (referenceStart != ffmpeg.AV_NOPTS_VALUE && timestamp <= referenceStart) return;
            FfmpegRuntime.Check(ffmpeg.av_seek_frame(format, referenceIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD), "Seek source timestamp");
            ffmpeg.avcodec_flush_buffers(codec);
        }
        catch { Dispose(); throw; }
    }

    public bool Read(CancellationToken cancellation)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!finished)
        {
            cancellation.ThrowIfCancellationRequested();
            int result = ffmpeg.avcodec_receive_frame(codec, frame);
            if (result == 0)
            {
                lastReadMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return true;
            }
            if (result == ffmpeg.AVERROR_EOF)
            {
                finished = true;
                return false;
            }
            if (result != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                CheckDecode(result, "Decode frame");
            }
            if (draining)
            {
                throw new InvalidDataException("Decoder requested input after its drain packet.");
            }

            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                result = ffmpeg.av_read_frame(format, packet);
                if (result == ffmpeg.AVERROR_EOF)
                {
                    CheckDecode(ffmpeg.avcodec_send_packet(codec, null), "Drain decoder");
                    draining = true;
                    break;
                }
                FfmpegRuntime.Check(result, "Read media packet");
                try
                {
                    if (packet->stream_index == streamIndex)
                    {
                        CheckDecode(ffmpeg.avcodec_send_packet(codec, packet), "Submit compressed packet");
                        break;
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(packet);
                }
            }
        }
        return false;
    }

    private void CheckDecode(int result, string operation)
    {
        try { FfmpegRuntime.Check(result, operation); }
        catch (InvalidDataException error) when (HasHardwareDevice)
        { throw new HardwareVideoDecodeException(error.Message, error); }
    }

    // Reuse the demux/codec/scaler allocation after a discontinuous video query.
    // The caller can reopen codecs/containers that cannot seek back to the start.
    public void SeekVideo(SourcePosition target)
    {
        AVStream* stream = format->streams[streamIndex];
        var timeBase = stream->time_base;
        long pts = TimelineTime.Rebase(target, timeBase.num, timeBase.den, TimeRounding.Floor).Value;
        if (stream->start_time != ffmpeg.AV_NOPTS_VALUE) pts = Math.Max(pts, stream->start_time);
        FfmpegRuntime.Check(ffmpeg.av_seek_frame(format, streamIndex, pts, ffmpeg.AVSEEK_FLAG_BACKWARD), "Seek video history");
        ffmpeg.avcodec_flush_buffers(codec);
        ffmpeg.av_frame_unref(frame);
        if (retainedVideo != null) ffmpeg.av_frame_unref(retainedVideo);
        draining = finished = false;
        ClearHistory();
    }

    // Retain source pictures by reference. Only the selected picture travels
    // through swscale. Evict by actual referenced buffer bytes and frame count.
    public void RememberVideo(MediaTimestamp timestamp)
    {
        // A D3D11 frame's buffer size does not account for the texture subresource it pins.
        if (frame->hw_frames_ctx != null) return;
        long bytes = 0;
        for (uint index = 0; index < 8; index++)
            if (frame->buf[index] is var buffer && buffer != null) bytes += checked((long)buffer->size);
        for (int index = 0; index < frame->nb_extended_buf; index++)
            bytes += checked((long)frame->extended_buf[index]->size);
        if (bytes <= 0 || bytes > VideoHistoryBudget)
        {
            ClearHistory();
            return;
        }
        while (videoHistory.Count >= MaximumHistoryFrames || videoHistoryBytes > VideoHistoryBudget - bytes)
            FreeOldestPicture();
        AVFrame* owned = ffmpeg.av_frame_clone(frame);
        if (owned == null) throw new OutOfMemoryException("FFmpeg video history allocation failed.");
        if (convertedHistory?.SourceTimestamp == timestamp) convertedHistory = null;
        videoHistory.Enqueue(new((nint)owned, timestamp, bytes));
        videoHistoryBytes += bytes;
    }

    public VideoFrame? HistoryAt(SourcePosition target, out bool converted)
    {
        converted = false;
        HistoryPicture? selected = null;
        foreach (var picture in videoHistory)
        {
            if (new SourcePosition(picture.Timestamp).CompareTo(target) > 0) break;
            selected = picture;
        }
        if (selected is not { } value) return null;
        if (convertedHistory?.SourceTimestamp == value.Timestamp) return convertedHistory;
        converted = true;
        return convertedHistory = CopyVideo((AVFrame*)value.Frame, 0);
    }

    private void FreeOldestPicture()
    {
        var picture = videoHistory.Dequeue();
        videoHistoryBytes -= picture.Bytes;
        AVFrame* owned = (AVFrame*)picture.Frame;
        ffmpeg.av_frame_free(&owned);
    }

    private void ClearHistory()
    {
        while (videoHistory.Count > 0) FreeOldestPicture();
        convertedHistory = null;
    }

    public MediaTimestamp Timestamp => TimestampOf(frame);
    private MediaTimestamp TimestampOf(AVFrame* frame)
    {
        long value = frame->best_effort_timestamp;
        if (value == ffmpeg.AV_NOPTS_VALUE)
        {
            throw new NotSupportedException("Decoded frame has no usable presentation timestamp.");
        }
        AVRational timeBase = format->streams[streamIndex]->time_base;
        return new MediaTimestamp(value, timeBase.num, timeBase.den);
    }

    // Keep one ref-counted decoded picture while reading the next PTS. Seeking need not swscale/copy
    // every preroll frame merely to discover which presentation interval contains the requested time.
    public void RetainVideo()
    {
        if (retainedVideo == null) retainedVideo = ffmpeg.av_frame_alloc();
        if (retainedVideo == null) throw new OutOfMemoryException("FFmpeg retained frame allocation failed.");
        ffmpeg.av_frame_unref(retainedVideo);
        FfmpegRuntime.Check(ffmpeg.av_frame_ref(retainedVideo, frame), "Retain decoded video frame");
    }
    public VideoFrame CopyRetainedVideo() => retainedVideo == null
        ? throw new InvalidOperationException("No retained video frame.") : CopyVideo(retainedVideo, 0);

    public VideoFrame CopyVideo(double startSeconds) => CopyVideo(frame, startSeconds);
    private VideoFrame CopyVideo(AVFrame* frame, double startSeconds)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (frame->color_trc is AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 or AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67)
            throw new NotSupportedException("HDR PQ/HLG requires tone mapping; M1b supports SDR video only.");
        int width = frame->width;
        int height = frame->height;
        if (width <= 0 || height <= 0 || (long)width * height > 33_177_600)
        {
            throw new NotSupportedException("M0 supports decoded frames up to 8K, with valid dimensions.");
        }
        AVFrame* pixels = frame;
        var surface = (AVPixelFormat)frame->format;
        double downloadMilliseconds = 0;
        double normalizeMilliseconds = 0;
        long visiblePlaneBytes = 0;
        bool hardware = surface == AVPixelFormat.AV_PIX_FMT_D3D11 && frame->hw_frames_ctx != null && frame->data[0] != null;
        var plane = surface;
        if (hardware)
        {
            long downloadStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            pixels = Download(frame);
            downloadMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(downloadStarted).TotalMilliseconds;
            plane = (AVPixelFormat)pixels->format;
            visiblePlaneBytes = ffmpeg.av_image_get_buffer_size(plane, width, height, 1);
            long normalizeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            pixels = NormalizePlanes(pixels, width, height);
            normalizeMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(normalizeStarted).TotalMilliseconds;
        }
        scaler = ffmpeg.sws_getCachedContext(scaler, width, height, (AVPixelFormat)pixels->format,
            width, height, AVPixelFormat.AV_PIX_FMT_RGBA, ffmpeg.SWS_BILINEAR, null, null, null);
        if (scaler == null)
        {
            throw new NotSupportedException("FFmpeg cannot convert this video pixel format to RGBA.");
        }
        int colorspace = frame->colorspace == AVColorSpace.AVCOL_SPC_BT709 ? ffmpeg.SWS_CS_ITU709 : ffmpeg.SWS_CS_DEFAULT;
        int* sourceCoefficients = ffmpeg.sws_getCoefficients(colorspace);
        int_array4 coefficients = default;
        for (int index = 0; index < 4; index++)
        {
            coefficients[(uint)index] = sourceCoefficients[index];
        }
        FfmpegRuntime.Check(ffmpeg.sws_setColorspaceDetails(scaler, coefficients,
            frame->color_range == AVColorRange.AVCOL_RANGE_JPEG ? 1 : 0, coefficients, 1, 0, 1 << 16, 1 << 16), "Configure SDR color conversion");
        byte[] rgba = new byte[checked(width * height * 4)];
        fixed (byte* destination = rgba)
        {
            byte_ptrArray4 data = default;
            int_array4 strides = default;
            data[0] = destination;
            strides[0] = width * 4;
            FfmpegRuntime.Check(ffmpeg.sws_scale(scaler, pixels->data, pixels->linesize, 0, height, data, strides), "Convert video frame");
        }
        MediaTimestamp timestamp = TimestampOf(frame);
        MediaTimestamp? duration = frame->duration > 0 ? new(frame->duration, timestamp.Numerator, timestamp.Denominator) : null;
        MediaTimestamp? dts = frame->pkt_dts == ffmpeg.AV_NOPTS_VALUE ? null : new(frame->pkt_dts, timestamp.Numerator, timestamp.Denominator);
        return new VideoFrame(timestamp, timestamp.TotalSeconds - startSeconds, width, height, rgba, duration, dts)
        {
            Timing = new(System.Diagnostics.Stopwatch.GetTimestamp(), lastReadMilliseconds,
                System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds),
            Decode = new(hardware ? "d3d11va" : "software", ffmpeg.avcodec_get_name(codec->codec_id), codec->profile,
                ffmpeg.av_get_pix_fmt_name(surface), ffmpeg.av_get_pix_fmt_name(plane), width, height,
                frame->color_range.ToString(), frame->colorspace.ToString(),
                hardware ? null : hardwareFallback ?? (HasHardwareDevice ? "The driver/decoder negotiated a software pixel format." : null),
                downloadMilliseconds, visiblePlaneBytes, normalizeMilliseconds)
        };
    }

    private AVFrame* Download(AVFrame* source)
    {
        var context = (AVHWFramesContext*)source->hw_frames_ctx->data;
        if (downloadedVideo == null) downloadedVideo = ffmpeg.av_frame_alloc();
        if (downloadedVideo == null) throw new OutOfMemoryException("FFmpeg transfer frame allocation failed.");
        if (downloadedVideo->format != (int)context->sw_format
            || downloadedVideo->width != context->width || downloadedVideo->height != context->height
            || downloadedVideo->buf[0] == null)
        {
            ffmpeg.av_frame_unref(downloadedVideo);
            downloadedVideo->format = (int)context->sw_format;
            downloadedVideo->width = context->width;
            downloadedVideo->height = context->height;
            CheckDecode(ffmpeg.av_frame_get_buffer(downloadedVideo, 0), "Allocate D3D11 download planes");
        }
        CheckDecode(ffmpeg.av_hwframe_transfer_data(downloadedVideo, source, 0), "Download D3D11 video surface");
        return downloadedVideo;
    }

    private AVFrame* NormalizePlanes(AVFrame* source, int width, int height)
    {
        var input = (AVPixelFormat)source->format;
        var output = input switch
        {
            AVPixelFormat.AV_PIX_FMT_NV12 => AVPixelFormat.AV_PIX_FMT_YUV420P,
            AVPixelFormat.AV_PIX_FMT_P010LE => AVPixelFormat.AV_PIX_FMT_YUV420P10LE,
            _ => throw new HardwareVideoDecodeException($"No planar SDR conversion for D3D11 pixel format {input}.")
        };
        // swscale's direct NV12 and planar RGB paths sample chroma differently at sharp edges.
        // Use the same color conversion as software decoding after an exact plane unpack.
        planeScaler = ffmpeg.sws_getCachedContext(planeScaler, width, height, input, width, height, output,
            ffmpeg.SWS_POINT, null, null, null);
        if (planeScaler == null) throw new HardwareVideoDecodeException("Could not prepare D3D11 plane normalization.");
        if (planarVideo == null) planarVideo = ffmpeg.av_frame_alloc();
        if (planarVideo == null) throw new OutOfMemoryException("FFmpeg planar frame allocation failed.");
        if (planarVideo->format != (int)output || planarVideo->width != width || planarVideo->height != height
            || planarVideo->buf[0] == null)
        {
            ffmpeg.av_frame_unref(planarVideo);
            planarVideo->format = (int)output;
            planarVideo->width = width;
            planarVideo->height = height;
            CheckDecode(ffmpeg.av_frame_get_buffer(planarVideo, 0), "Allocate D3D11 planar normalization buffer");
        }
        CheckDecode(ffmpeg.sws_scale(planeScaler, source->data, source->linesize, 0, height, planarVideo->data, planarVideo->linesize),
            "Normalize D3D11 video planes");
        return planarVideo;
    }

    public float[] CopyAudio()
    {
        if (resampler == null)
        {
            AVChannelLayout outputLayout = default;
            ffmpeg.av_channel_layout_default(&outputLayout, outputChannels);
            try
            {
                SwrContext* configured = null;
                int result = ffmpeg.swr_alloc_set_opts2(&configured, &outputLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT,
                    outputSampleRate, &frame->ch_layout, (AVSampleFormat)frame->format, frame->sample_rate, 0, null);
                resampler = configured;
                FfmpegRuntime.Check(result, "Configure audio resampling");
                FfmpegRuntime.Check(ffmpeg.swr_init(resampler), "Start audio resampler");
            }
            finally
            {
                ffmpeg.av_channel_layout_uninit(&outputLayout);
            }
        }
        return ConvertAudio(frame->extended_data, frame->nb_samples);
    }

    public float[] DrainAudio() => resampler == null ? [] : ConvertAudio(null, 0);
    public int SourceSampleRate => frame->sample_rate;
    public int SourceSampleCount => frame->nb_samples;
    public float[] CopyTranscriptionAudio()
    {
        outputSampleRate = 16000;
        outputChannels = 1;
        return CopyAudio();
    }
    public void ResetAudioConversion()
    {
        SwrContext* owned = resampler;
        ffmpeg.swr_free(&owned);
        resampler = null;
    }

    // Analysis changes sample format only: no rate conversion, channel mixing or fabricated timestamps.
    public (float[] Samples, int Rate, int Channels) CopySourceAudio()
    {
        int channels = frame->ch_layout.nb_channels;
        if (channels is < 1 or > 2 || frame->sample_rate <= 0)
            throw new NotSupportedException("Source analysis currently supports mono/stereo streams; select another independent OBS stream.");
        if (resampler != null && (outputSampleRate != frame->sample_rate || outputChannels != channels))
            throw new NotSupportedException("Audio format changes within one stream are unsupported.");
        outputSampleRate = frame->sample_rate;
        outputChannels = channels;
        return (CopyAudio(), outputSampleRate, outputChannels);
    }

    public byte[] CopyAnalysisLuma()
    {
        const int width = 64, height = 36;
        scaler = ffmpeg.sws_getCachedContext(scaler, frame->width, frame->height, (AVPixelFormat)frame->format,
            width, height, AVPixelFormat.AV_PIX_FMT_GRAY8, ffmpeg.SWS_AREA, null, null, null);
        if (scaler == null) throw new NotSupportedException("Cannot downsample source luma.");
        byte[] pixels = new byte[width * height];
        fixed (byte* target = pixels)
        {
            byte_ptrArray4 data = default;
            int_array4 strides = default;
            data[0] = target;
            strides[0] = width;
            FfmpegRuntime.Check(ffmpeg.sws_scale(scaler, frame->data, frame->linesize, 0, frame->height, data, strides), "Downsample analysis frame");
        }
        return pixels;
    }

    private float[] ConvertAudio(byte** source, int sampleCount)
    {
        int capacity = ffmpeg.swr_get_out_samples(resampler, sampleCount);
        FfmpegRuntime.Check(capacity, "Size audio block");
        float[] samples = new float[checked(capacity * outputChannels)];
        fixed (float* destination = samples)
        {
            byte* output = (byte*)destination;
            int count = ffmpeg.swr_convert(resampler, &output, capacity, source, sampleCount);
            FfmpegRuntime.Check(count, "Resample audio");
            Array.Resize(ref samples, count * outputChannels);
        }
        return samples;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        ClearHistory();
        ffmpeg.sws_freeContext(scaler);
        ffmpeg.sws_freeContext(planeScaler);
        SwrContext* ownedResampler = resampler;
        AVFrame* ownedFrame = frame;
        AVFrame* ownedRetainedVideo = retainedVideo;
        AVFrame* ownedDownloadedVideo = downloadedVideo;
        AVFrame* ownedPlanarVideo = planarVideo;
        AVPacket* ownedPacket = packet;
        AVCodecContext* ownedCodec = codec;
        AVFormatContext* ownedFormat = format;
        ffmpeg.swr_free(&ownedResampler);
        ffmpeg.av_frame_free(&ownedFrame);
        ffmpeg.av_frame_free(&ownedRetainedVideo);
        ffmpeg.av_frame_free(&ownedDownloadedVideo);
        ffmpeg.av_frame_free(&ownedPlanarVideo);
        ffmpeg.av_packet_free(&ownedPacket);
        ffmpeg.avcodec_free_context(&ownedCodec);
        ffmpeg.avformat_close_input(&ownedFormat);
    }
}
