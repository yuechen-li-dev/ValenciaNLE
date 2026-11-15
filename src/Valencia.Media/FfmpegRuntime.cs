using FFmpeg.AutoGen;

namespace Valencia.Media;

public static unsafe class FfmpegRuntime
{
    private static readonly object Gate = new();
    private static bool initialized;

    public static string NativeDirectory { get; private set; } = string.Empty;
    public static string DecoderIdentity
    {
        get
        {
            Initialize();
            return $"avcodec={ffmpeg.avcodec_version()};avutil={ffmpeg.avutil_version()};swr={ffmpeg.swresample_version()};sws={ffmpeg.swscale_version()}";
        }
    }

    public static void Initialize()
    {
        lock (Gate)
        {
            if (initialized)
            {
                return;
            }

            string? directory = Environment.GetEnvironmentVariable("VALENCIA_FFMPEG_DIR");
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(directory))
            {
                candidates.Add(directory);
            }
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "native"));
            candidates.Add(AppContext.BaseDirectory);
            candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
            directory = candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "avformat-61.dll")));
            if (directory is null)
            {
                throw new InvalidOperationException(
                    "FFmpeg 7.1 shared libraries are required. Set VALENCIA_FFMPEG_DIR to the folder containing avformat-61.dll, avcodec-61.dll, avutil-59.dll, swscale-8.dll and swresample-5.dll.");
            }

            ffmpeg.RootPath = Path.GetFullPath(directory);
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            if ((ffmpeg.avformat_version() >> 16) != 61)
            {
                throw new InvalidOperationException("The installed FFmpeg ABI does not match the pinned FFmpeg 7.1 bindings.");
            }
            NativeDirectory = ffmpeg.RootPath;
            initialized = true;
        }
    }

    internal static void Check(int result, string operation)
    {
        if (result >= 0)
        {
            return;
        }
        byte* message = stackalloc byte[1024];
        ffmpeg.av_strerror(result, message, 1024);
        string detail = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)message) ?? result.ToString();
        throw new InvalidDataException($"{operation}: {detail} ({result}).");
    }
}
