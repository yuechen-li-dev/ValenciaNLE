using System.Runtime.InteropServices;
using System.Diagnostics;
using Aurelian.Graphics.Vulkan.Diagnostics;
using Aurelian.Graphics.Plants;
using Aurelian.Graphics.Vulkan.Device;
using Aurelian.Graphics.Vulkan.Native2D;
using Aurelian.Graphics.Vulkan.Presentation;
using Aurelian.Graphics.Vulkan.Resources.Textures;
using Aurelian.Machina;
using Aurelian.Rendering.Contracts.Shaders;
using Machina.Presentation;
using Machina.Layout.Geometry;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Valencia.Rendering;

/// <summary>Owns native graphics on the window thread. Media workers never touch Vulkan.</summary>
public sealed class NativeVideoPresenter : IDisposable
{
    public const int LogicalWidth = 1280;
    public const int LogicalHeight = 720;
    private readonly AurelianVulkanPlant plant;
    private readonly CompiledGraphicsProgram shader;
    private readonly CompiledGraphicsProgram textShader;
    private readonly CompiledGraphicsProgram shapeShader;
    private readonly NativeUiPresentation ui = new();
    private AurelianVulkanSurface? surface;
    private AurelianVulkanSwapchain? swapchain;
    private VulkanNativeFrameTarget? target;
    private VulkanOrderedQuadRenderer? renderer;
    private VulkanOrderedQuadRenderer? textRenderer;
    private VulkanOrderedQuadRenderer? shapeRenderer;
    private AurelianMsdfAtlasCache? atlas;
    private VulkanNativeSwapchainPresenter? presenter;
    private Native2DTextureHandle uiTexture;
    private Native2DTextureHandle videoTexture;
    private Native2DTextureHandle whiteTexture;
    private Native2DTextureHandle glyphTexture;
    private int glyphVersion = -1;
    public bool EditorLayout { get; set; }
    public Rect? ProgramMonitor { get; set; }
    public IReadOnlyList<CanvasRectangle> Canvas { get; set; } = [];
    public IReadOnlyList<CanvasRectangle> CanvasOverlay { get; set; } = [];
    private int videoWidth;
    private int videoHeight;
    private byte[]? lastVideo;
    private MachinaPresentationFrame? uiFrame;
    private double logicalWidth = LogicalWidth, logicalHeight = LogicalHeight;
    private bool resize;
    private ulong frameId;
    private bool disposed;
    private readonly NativeRenderTrace? trace;
    private readonly VulkanValidationCapture? validation;
    public int ValidationErrorCount => validation?.ErrorCount ?? 0;
    public void RecordCpuPhase(string name, double milliseconds) => trace?.Phase(name, milliseconds);
    public void RecordMediaFrame(object metadata)
    {
        if (trace is not null) trace.MediaFrame = metadata;
    }
    public void ObservePresentationClock(double frameSeconds, double clockSeconds) =>
        trace?.ObservePresentation(frameId - 1, frameSeconds, clockSeconds);

    public NativeVideoPresenter(bool visible = true, string? tracePath = null)
    {
        tracePath ??= Environment.GetEnvironmentVariable("VALENCIA_RENDER_TRACE");
        if (!string.IsNullOrWhiteSpace(tracePath)) trace = new(tracePath);
        shader = VideoShader.Load();
        textShader = VideoShader.Load("MsdfText");
        shapeShader = VideoShader.Load("AnalyticShape2D");
        WindowOptions options = WindowOptions.DefaultVulkan;
        options.Title = "Valencia — M0 native video player";
        options.Size = new Vector2D<int>(LogicalWidth, LogicalHeight);
        options.IsVisible = visible;
        Window = Silk.NET.Windowing.Window.Create(options);
        Window.Initialize();
        var init = VulkanPlantInitializer.CreatePlant(PlantId.Zero,
            new VulkanPlantOptions(EnableValidation: true, EnablePresentation: true, ApplicationName: "Valencia",
                RequiredPresentationInstanceExtensions: RequiredExtensions(Window)));
        if (!init.Success || init.Plant is null)
        {
            Window.Dispose();
            throw new InvalidOperationException("Vulkan initialization failed: " + string.Join("; ", init.Diagnostics.Select(item => item.Message)));
        }
        plant = init.Plant;
        GpuName = plant.Facts.PhysicalDeviceName;
        ValidationAvailable = plant.Facts.EnabledValidationLayers.Contains("VK_LAYER_KHRONOS_validation");
        if (trace is not null && ValidationAvailable) validation = new(plant);
        try
        {
            RecreateSurface();
            Window.FramebufferResize += _ => resize = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static unsafe string[] RequiredExtensions(IWindow window)
    {
        var source = window.VkSurface ?? throw new NotSupportedException("The native window has no Vulkan surface.");
        byte** extensions = source.GetRequiredExtensions(out uint count);
        var names = new string[count];
        for (int index = 0; index < count; index++)
        {
            names[index] = Marshal.PtrToStringUTF8((nint)extensions[index])
                ?? throw new InvalidOperationException("Vulkan surface extension name is empty.");
        }
        return names;
    }

    public static void PrepareShaderArtifact(string outputPath)
    {
        VideoShader.WriteArtifact(outputPath);
        string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        VideoShader.WriteArtifact(Path.Combine(directory, "MsdfText.json"), "MsdfText");
        VideoShader.WriteArtifact(Path.Combine(directory, "AnalyticShape2D.json"), "AnalyticShape2D");
    }

    public IWindow Window { get; }
    public string GpuName { get; }
    public bool ValidationAvailable { get; }
    public long PresentedFrames { get; private set; }
    /// <summary>Frames skipped because no swapchain image was free (composition throttled); nothing waited.</summary>
    public long SkippedFrames { get; private set; }
    /// <summary>True when the last <see cref="Draw"/> reached the display; media skew is only meaningful then.</summary>
    public bool LastDrawPresented { get; private set; }
    public long UploadedVideoFrames { get; private set; }
    public int MsdfGlyphCount => ui.Glyphs.Count;
    /// <summary>Text glyph quads submitted this frame, raster and MSDF together.</summary>
    public int TextGlyphCount => ui.Glyphs.Count + ui.RasterGlyphs.Count;
    public int PixelFallbackTextRuns => ui.FallbackTextRuns;
    public int AtlasUploadCount => atlas?.UploadCount ?? 0;

    public void UpdateUi(MachinaPresentationFrame frame)
    {
        uiFrame = frame;
        logicalWidth = frame.Viewport.Width; logicalHeight = frame.Viewport.Height;
        if (renderer is not null)
        {
            renderer.SetLogicalSize((float)logicalWidth, (float)logicalHeight);
            textRenderer!.SetLogicalSize((float)logicalWidth, (float)logicalHeight);
            shapeRenderer!.SetLogicalSize((float)logicalWidth, (float)logicalHeight);
            PrepareUi();
        }
    }

    private void PrepareUi()
    {
        if (uiFrame is null || renderer is null || target is null) return;
        ui.Prepare(uiFrame, atlas!, (int)target.Width, (int)target.Height);
        if (uiTexture.Value != 0) { renderer.DisposeTexture(uiTexture); uiTexture = default; }
        if (ui.FallbackPixels is { } pixels) uiTexture = renderer.CreateTexture(target.Width, target.Height, pixels);
        var raster = RasterText.Instance;
        if (raster.Version != glyphVersion)
        {
            if (glyphTexture.Value == 0) glyphTexture = renderer.CreateTexture(RasterText.AtlasSize, RasterText.AtlasSize, raster.Pixels);
            else renderer.UpdateTexture(glyphTexture, RasterText.AtlasSize, RasterText.AtlasSize, raster.Pixels);
            glyphVersion = raster.Version;
        }
    }

    public void UpdateVideo(int width, int height, byte[] rgba)
    {
        long started = Stopwatch.GetTimestamp();
        lastVideo = rgba;
        if (renderer is null)
        {
            return;
        }
        if (videoTexture.Value != 0 && (videoWidth != width || videoHeight != height))
        {
            renderer.DisposeTexture(videoTexture);
            videoTexture = default;
        }
        videoWidth = width;
        videoHeight = height;
        if (videoTexture.Value == 0)
        {
            videoTexture = renderer.CreateTexture((uint)width, (uint)height, rgba);
        }
        else
        {
            renderer.UpdateTexture(videoTexture, (uint)width, (uint)height, rgba);
        }
        UploadedVideoFrames++;
        if (trace is not null) trace.Upload = renderer.LastTextureUpload;
        trace?.Phase("textureUploadMs", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public void ClearVideo()
    {
        if (videoTexture.Value != 0)
        {
            renderer?.DisposeTexture(videoTexture);
        }
        videoTexture = default;
        lastVideo = null;
    }

    public byte[]? Draw(bool capture = false)
    {
        long started = Stopwatch.GetTimestamp();
        if (Window.FramebufferSize.X <= 0 || Window.FramebufferSize.Y <= 0)
        {
            return null;
        }
        if (resize)
        {
            RecreateSurface();
            resize = false;
        }
        // Never block the window loop on the compositor. When Windows throttles composition (display asleep,
        // locked session) no swapchain image is free for long stretches; waiting for one used to stall input,
        // seeks and media pacing to the compositor's ~4 Hz. Skip the frame instead; a capture still renders.
        var readiness = presenter!.PollImage();
        if (readiness == NativePresentReadiness.OutOfDate)
        {
            resize = true;
            LastDrawPresented = false;
            return null;
        }
        bool present = readiness == NativePresentReadiness.Ready;
        if (!present)
        {
            SkippedFrames++;
            LastDrawPresented = false;
            if (!capture) return null;
        }
        using VulkanNativeFrameSession frame = target!.BeginFrame(new NativeFrameClearColor(0.04f, 0.05f, 0.07f, 1));
        var shapePass = frame.Present(shapeRenderer!, shapes =>
        {
            foreach (var shape in ui.Shapes) shapes.SubmitAnalyticShape(shape);
        });
        var videoPass = frame.Present(renderer!, quad =>
        {
            if (videoTexture.Value != 0)
            {
                Rect area = ProgramMonitor ?? (EditorLayout ? new(326, 64, 930, 270) : new(24, 100, 1232, 470));
                float areaX = (float)area.X, areaY = (float)area.Y;
                float areaWidth = (float)area.Width, areaHeight = (float)area.Height;
                float scale = Math.Min(areaWidth / videoWidth, areaHeight / videoHeight);
                float width = videoWidth * scale;
                float height = videoHeight * scale;
                quad.SubmitQuad(new NativeQuadSubmission(new Native2DRect(areaX + (areaWidth - width) / 2, areaY + (areaHeight - height) / 2, width, height),
                    Native2DUvRect.Full, videoTexture, Native2DTint.White));
            }
            SubmitRectangles(Canvas);
            SubmitRectangles(CanvasOverlay);
            void SubmitRectangles(IReadOnlyList<CanvasRectangle> rectangles)
            {
                foreach (var r in rectangles)
                    quad.SubmitQuad(new NativeQuadSubmission(new Native2DRect(r.X, r.Y, r.Width, r.Height), Native2DUvRect.Full,
                        whiteTexture, new Native2DTint((r.Color >> 24) / 255f, ((r.Color >> 16) & 255) / 255f, ((r.Color >> 8) & 255) / 255f, (r.Color & 255) / 255f)));
            }
            // Raster UI text draws in this pass, after the canvas, so it sits above every fill and the picture.
            if (glyphTexture.Value != 0)
                foreach (var g in ui.RasterGlyphs) quad.SubmitQuad(new NativeQuadSubmission(g.Destination, g.Uv, glyphTexture, g.Color));
        });
        var textPass = frame.Present(textRenderer!, text =>
        {
            foreach (var glyph in ui.Glyphs) text.SubmitMsdfQuad(glyph);
        });
        if (uiTexture.Value != 0)
            frame.Present(renderer!, quad => quad.SubmitQuad(new NativeQuadSubmission(
                new(0, 0, (float)logicalWidth, (float)logicalHeight), Native2DUvRect.Full, uiTexture, Native2DTint.White)));
        VulkanNativeFrameResult result = frame.EndFrame(captureReadback: capture);
        LastDrawPresented = false;
        if (!present) frameId++;
        else if (!presenter.TryPresent(frameId++))
        {
            resize = true;
        }
        else
        {
            PresentedFrames++;
            LastDrawPresented = true;
        }
        trace?.Frame(frameId - 1, started, [shapePass, videoPass, textPass], presenter.LastTimings, AtlasUploadCount, UploadedVideoFrames);
        return result.Pixels;
    }

    private void RecreateSurface()
    {
        DisposeSurface();
        var result = VulkanSwapchainFactory.Create(plant, Window,
            new VulkanSwapchainCreateOptions((uint)Window.FramebufferSize.X, (uint)Window.FramebufferSize.Y, Title: "Valencia", Visible: true));
        surface = result.Surface;
        swapchain = result.Swapchain;
        if (!result.Success || swapchain is null)
        {
            throw new InvalidOperationException("Vulkan presentation failed: " + string.Join("; ", result.Diagnostics.Select(item => item.Message)));
        }
        VulkanTextureFormat format = swapchain.Facts.SelectedFormat switch
        {
            "B8G8R8A8Srgb" => VulkanTextureFormat.Bgra8Srgb,
            "B8G8R8A8Unorm" => VulkanTextureFormat.Bgra8Unorm,
            "R8G8B8A8Srgb" => VulkanTextureFormat.Rgba8Srgb,
            "R8G8B8A8Unorm" => VulkanTextureFormat.Rgba8Unorm,
            _ => throw new NotSupportedException($"Unsupported presentation format {swapchain.Facts.SelectedFormat}.")
        };
        target = new VulkanNativeFrameTarget(plant, swapchain.Facts.Width, swapchain.Facts.Height, format);
        renderer = new VulkanOrderedQuadRenderer(plant, shader, target, Native2DPipelineOptions.SpriteLinear);
        textRenderer = new VulkanOrderedQuadRenderer(plant, textShader, target, Native2DPipelineOptions.MsdfText);
        shapeRenderer = new VulkanOrderedQuadRenderer(plant, shapeShader, target, new(Native2DPipelineKind.AnalyticShape2D));
        renderer.CaptureTimings = trace is not null;
        textRenderer.CaptureTimings = trace is not null;
        shapeRenderer.CaptureTimings = trace is not null;
        atlas = new(textRenderer);
        renderer.SetLogicalSize((float)logicalWidth, (float)logicalHeight);
        textRenderer.SetLogicalSize((float)logicalWidth, (float)logicalHeight);
        shapeRenderer.SetLogicalSize((float)logicalWidth, (float)logicalHeight);
        whiteTexture = renderer.CreateTexture(1, 1, new byte[] { 255, 255, 255, 255 });
        presenter = new VulkanNativeSwapchainPresenter(plant, target, swapchain);
        presenter.CaptureTimings = trace is not null;
        uiTexture = default;
        videoTexture = default;
        glyphTexture = default; glyphVersion = -1;
        PrepareUi();
        if (lastVideo is not null)
        {
            UpdateVideo(videoWidth, videoHeight, lastVideo);
        }
    }

    private void DisposeSurface()
    {
        presenter?.Dispose();
        atlas?.Dispose();
        textRenderer?.Dispose();
        shapeRenderer?.Dispose();
        renderer?.Dispose();
        target?.Dispose();
        swapchain?.Dispose();
        surface?.Dispose();
        presenter = null;
        renderer = null;
        textRenderer = null; shapeRenderer = null; atlas = null;
        target = null;
        swapchain = null;
        surface = null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        DisposeSurface();
        trace?.Save(new { GpuName, ValidationAvailable, timestampPolicy = "GPU raw ticks/period are distinct from host Stopwatch; no calibrated cross-domain latency claim" },
            new { errors = ValidationErrorCount, warnings = validation?.WarningCount, messages = validation?.Messages });
        validation?.Dispose();
        plant.Dispose();
        Window.Dispose();
    }
}

public readonly record struct CanvasRectangle(float X, float Y, float Width, float Height, uint Color);
