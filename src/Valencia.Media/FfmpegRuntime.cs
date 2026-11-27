using FFmpeg.AutoGen;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Valencia.Media;

public static unsafe class FfmpegRuntime
{
    private static readonly object Gate = new();
    private static bool initialized;

    public static string NativeDirectory { get; private set; } = string.Empty;
    public static bool IsDistribution => File.Exists(Path.Combine(AppContext.BaseDirectory, "valencia-release.json"));
    public static string Executable => Tool("ffmpeg", "VALENCIA_FFMPEG_EXE");
    public static string ProbeExecutable => Tool("ffprobe", "VALENCIA_FFPROBE_EXE");

    public static string CompatibilityExecutable
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("VALENCIA_COMPAT_FFMPEG_EXE");
            if (!string.IsNullOrWhiteSpace(configured)) return ExplicitExecutable(configured, "VALENCIA_COMPAT_FFMPEG_EXE");
            if (!IsDistribution) return Executable;
            throw new InvalidOperationException("Set VALENCIA_COMPAT_FFMPEG_EXE to an absolute path to FFmpeg with the requested H.264 or HEVC encoder.");
        }
    }

    private static string Tool(string name, string variable)
    {
        string packaged = Path.Combine(AppContext.BaseDirectory, "native", name + ".exe");
        if (File.Exists(packaged)) return packaged;
        if (IsDistribution) throw new FileNotFoundException($"Bundled {name}.exe is missing. Repair or re-extract Valencia.", packaged);
        string? configured = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(configured) ? name : ExplicitExecutable(configured, variable);
    }

    private static string ExplicitExecutable(string path, string variable)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new FileNotFoundException($"{variable} must point to an existing executable using its absolute path.", path);
        return path;
    }
    public static bool HasDecoder(string codec)
    {
        Initialize();
        return ffmpeg.avcodec_find_decoder_by_name(codec) is not null;
    }
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
            if (!IsDistribution && !string.IsNullOrWhiteSpace(directory) && Path.IsPathFullyQualified(directory))
            {
                candidates.Add(directory);
            }
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "native"));
            if (!IsDistribution)
            {
                candidates.Add(AppContext.BaseDirectory);
                candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                    .Where(Path.IsPathFullyQualified));
            }
            directory = candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "avformat-61.dll")));
            if (directory is null)
            {
                throw new InvalidOperationException(
                    IsDistribution ? "Bundled FFmpeg 7.1 libraries are missing. Repair or re-extract Valencia."
                    : "FFmpeg 7.1 shared libraries are required. Set VALENCIA_FFMPEG_DIR to the absolute folder containing avformat-61.dll and the matching libraries.");
            }

            ffmpeg.RootPath = Path.GetFullPath(directory);
            if (IsDistribution && OperatingSystem.IsWindows())
                PreloadDistribution(ffmpeg.RootPath);
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            if ((ffmpeg.avformat_version() >> 16) != 61)
            {
                throw new InvalidOperationException("The installed FFmpeg ABI does not match the pinned FFmpeg 7.1 bindings.");
            }
            NativeDirectory = ffmpeg.RootPath;
            initialized = true;
        }
    }

    private static void PreloadDistribution(string directory)
    {
        const uint SameDllDirectory = 0x00000100;
        const uint System32 = 0x00000800;
        var loaded = new List<nint>();
        try
        {
            foreach (string name in new[] { "avutil-59.dll", "swresample-5.dll", "swscale-8.dll",
                "avcodec-61.dll", "avformat-61.dll", "avfilter-10.dll", "avdevice-61.dll" })
            {
                nint handle = LoadLibraryExW(Path.Combine(directory, name), 0, SameDllDirectory | System32);
                if (handle == 0)
                    throw new InvalidOperationException($"Could not load bundled {name}. Repair or re-extract Valencia.",
                        new Win32Exception(Marshal.GetLastWin32Error()));
                loaded.Add(handle);
            }
            // AutoGen resolves these already loaded modules by their absolute paths for the process lifetime.
        }
        catch
        {
            foreach (nint handle in Enumerable.Reverse(loaded)) _ = FreeLibrary(handle);
            throw;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern nint LoadLibraryExW(string file, nint reserved, uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(nint module);

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
