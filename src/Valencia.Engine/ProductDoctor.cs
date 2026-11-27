using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Aurelian.Graphics.Plants;
using Aurelian.Graphics.Vulkan.Device;
using NAudio.Wave;
using Valencia.Analysis;
using Valencia.Core;
using Valencia.Media;

namespace Valencia.Engine;

public sealed record CapabilityCheck(string Id, bool Essential, bool Available, string Detail, string? Suggestion = null);
public sealed record ProductCapabilities(string Version, string Runtime, string RuntimePath, string Architecture, bool Packaged,
    IReadOnlyList<CapabilityCheck> Checks, IReadOnlyList<EncoderAvailability> Encoders,
    IReadOnlyList<ExportPreset> Presets, IReadOnlyList<ModelAvailability> Models);

public static class ProductDoctor
{
    public static string Version => typeof(ProductDoctor).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion.Split('+')[0] ?? "0.3.0";

    public static async Task<ProductCapabilities> CheckAsync(string? selectedGraphicsDevice = null,
        CancellationToken cancellation = default)
    {
        var checks = new List<CapabilityCheck>();
        bool windows = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && RuntimeInformation.ProcessArchitecture == Architecture.X64;
        checks.Add(new("windows", true, windows, RuntimeInformation.OSDescription,
            windows ? null : "Use a supported Windows x64 system."));
        if (selectedGraphicsDevice is null && windows)
        {
            var vulkan = VulkanPlantInitializer.CreatePlant(PlantId.Zero, new(EnableValidation: false, ApplicationName: "Valencia diagnostics"));
            using var plant = vulkan.Plant;
            selectedGraphicsDevice = plant?.Facts.PhysicalDeviceName;
            if (selectedGraphicsDevice is null)
                checks.Add(new("vulkan", true, false, string.Join(" ", vulkan.Diagnostics.Select(d => d.Message)),
                    "Install a current Vulkan-capable graphics driver."));
        }
        if (selectedGraphicsDevice is not null) checks.Add(new("vulkan", true, true, selectedGraphicsDevice));
        if (OperatingSystem.IsWindows())
        {
            try
            {
                int devices = WaveOut.DeviceCount;
                checks.Add(new("audio-output", false, devices > 0, $"{devices} WinMM output device(s).",
                    devices > 0 ? null : "Enable a Windows sound output device for preview."));
            }
            catch (Exception error) when (error is InvalidOperationException or DllNotFoundException)
            { checks.Add(new("audio-output", false, false, error.Message, "Check Windows sound devices.")); }
        }
        try
        {
            string decoder = FfmpegRuntime.DecoderIdentity;
            string codecs = string.Join(", ", new[] { "h264", "hevc", "av1", "vp9", "aac", "opus" }.Where(FfmpegRuntime.HasDecoder));
            checks.Add(new("ffmpeg-decode", true, true, decoder + ". Decoders " + codecs + "."));
        }
        catch (Exception error) when (error is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or IOException or NotSupportedException)
        { checks.Add(new("ffmpeg-decode", true, false, error.Message, "Repair or re-extract the Valencia distribution.")); }
        foreach (string name in new[] { "Analysis-v1", "Recovery", "RenderQueue" })
            checks.Add(CheckDirectory(name));
        bool obs;
        using (var process = Process.GetProcessesByName("obs64").FirstOrDefault()) obs = process is not null;
        checks.Add(new("obs", false, obs, obs ? "An OBS process is running." : "OBS is not running.",
            obs ? null : "Open OBS and enable WebSocket control to connect."));
        var encoders = new List<EncoderAvailability>();
        foreach (string id in new[] { "vp9", "av1" })
            encoders.Add(await ExportCatalog.ProbeAsync(ExportCatalog.Encoder(id), cancellation).ConfigureAwait(false));
        checks.Add(new("standard-export", false, encoders.Any(e => e.Available), "Executed short AV1 and VP9 encoder probes.",
            encoders.Any(e => e.Available) ? null : "Repair the bundled encoder tools."));
        return new(Version, RuntimeInformation.FrameworkDescription, typeof(object).Assembly.Location, RuntimeInformation.ProcessArchitecture.ToString(),
            FfmpegRuntime.IsDistribution, checks, encoders, ExportCatalog.Presets, WhisperModels.List());
    }

    private static CapabilityCheck CheckDirectory(string name)
    {
        string path = Path.Combine(ValenciaData.Root, name);
        try
        {
            Directory.CreateDirectory(path);
            using (new FileStream(Path.Combine(path, $"doctor-{Guid.NewGuid():N}.tmp"), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose)) { }
            return new(name, false, true, path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(name, false, false, error.Message, "Use a writable Valencia application-data directory.");
        }
    }
}
