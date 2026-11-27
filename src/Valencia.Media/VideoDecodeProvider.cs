using FFmpeg.AutoGen;

namespace Valencia.Media;

public sealed record VideoDecodeCapability(string Backend, bool DeviceAvailable, string Detail,
    IReadOnlyList<string> CodecConfigurations, bool RequiresSourceProbe);

public sealed record VideoDecodeEvidence(string Backend, string Codec, int Profile, string SurfaceFormat,
    string PlaneFormat, int Width, int Height, string Range, string Matrix, string? Fallback,
    double DownloadMilliseconds, long VisiblePlaneBytes, double NormalizeMilliseconds = 0);

internal sealed class HardwareVideoDecodeException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>Negotiates device-backed decode. A device or codec configuration is not evidence of a decoded surface.</summary>
public static unsafe class VideoDecodeProvider
{
    // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX from libavcodec/codec.h.
    private const int DeviceContextMethod = 1;
    private static readonly AVCodecContext_get_format SelectFormat = ChooseFormat;
    public static bool Requested => string.Equals(Environment.GetEnvironmentVariable("VALENCIA_VIDEO_DECODER"),
        "d3d11va", StringComparison.OrdinalIgnoreCase);

    public static VideoDecodeCapability Probe()
    {
        FfmpegRuntime.Initialize();
        if (!OperatingSystem.IsWindows())
            return new("d3d11va", false, "D3D11VA requires Windows.", [], true);
        var codecs = new[] { "h264", "hevc", "av1", "vp9", "mpeg2video", "vc1" }
            .Where(name => Supports(ffmpeg.avcodec_find_decoder_by_name(name))).ToArray();
        if (codecs.Length == 0)
            return new("d3d11va", false, "The loaded FFmpeg build exposes no D3D11VA codec configurations.", [], true);
        AVBufferRef* device = CreateDevice(out var error);
        bool available = device != null;
        ffmpeg.av_buffer_unref(&device);
        return new("d3d11va", available,
            error ?? "Device initialized. The driver must negotiate the actual source and return a D3D11 surface.",
            codecs, true);
    }

    internal static string? Configure(AVCodecContext* context, AVCodec* decoder)
    {
        if (!OperatingSystem.IsWindows()) return "D3D11VA requires Windows.";
        if (!Supports(decoder)) return "FFmpeg has no D3D11VA device configuration for this codec.";
        context->hw_device_ctx = CreateDevice(out var error);
        if (context->hw_device_ctx != null)
        {
            context->get_format = SelectFormat;
            // Decoder-owned references are separate from our retained display picture and lookahead.
            context->extra_hw_frames = 2;
        }
        return error;
    }

    internal static AVCodec* SelectDecoder(AVCodec* fallback, AVCodecID codecId)
    {
        if (Supports(fallback)) return fallback;
        // Some builds prioritize an external software implementation (for example libaom AV1).
        // The native codec may still expose a device-backed implementation for the same bitstream.
        AVCodec* native = ffmpeg.avcodec_find_decoder_by_name(ffmpeg.avcodec_get_name(codecId));
        return Supports(native) ? native : fallback;
    }

    private static bool Supports(AVCodec* decoder)
    {
        if (decoder == null) return false;
        for (int index = 0; ffmpeg.avcodec_get_hw_config(decoder, index) is var config && config != null; index++)
            if (config->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA
                && config->pix_fmt == AVPixelFormat.AV_PIX_FMT_D3D11 && (config->methods & DeviceContextMethod) != 0)
                return true;
        return false;
    }

    private static AVBufferRef* CreateDevice(out string? error)
    {
        AVBufferRef* device = null;
        AVDictionary* options = null;
        string? vendor = Environment.GetEnvironmentVariable("VALENCIA_D3D11_VENDOR_ID");
        try
        {
            if (!string.IsNullOrWhiteSpace(vendor))
                FfmpegRuntime.Check(ffmpeg.av_dict_set(&options, "vendor_id", vendor, 0), "Set D3D11 adapter vendor");
            FfmpegRuntime.Check(ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
                Environment.GetEnvironmentVariable("VALENCIA_D3D11_ADAPTER"), options, 0), "Create D3D11VA decoder device");
            error = null;
            return device;
        }
        catch (InvalidDataException failure)
        {
            ffmpeg.av_buffer_unref(&device);
            error = failure.Message;
            return null;
        }
        finally { ffmpeg.av_dict_free(&options); }
    }

    private static AVPixelFormat ChooseFormat(AVCodecContext* context, AVPixelFormat* formats)
    {
        // Full device, stream profile, bit depth, chroma and resolution negotiation occurs inside FFmpeg.
        for (var format = formats; *format != AVPixelFormat.AV_PIX_FMT_NONE; format++)
            if (*format == AVPixelFormat.AV_PIX_FMT_D3D11) return *format;
        return ffmpeg.avcodec_default_get_format(context, formats);
    }
}
