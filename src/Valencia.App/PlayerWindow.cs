using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Machina.Runtime.Input;
using Silk.NET.Input;
using Valencia.Core;
using Valencia.Media;
using Valencia.Rendering;
using PointerPoint = Machina.Runtime.Input.PointerPoint;

namespace Valencia.App;

[SupportedOSPlatform("windows")]
internal sealed class PlayerWindow : IDisposable
{
    private readonly NativeVideoPresenter graphics;
    private readonly IInputContext input;
    private readonly TransportView view = new();
    private readonly PlayerKeymap keymap = new();
    private readonly UiPointerActivation pointer = new();
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly SmokeScenario? smoke;
    private PlaybackSession? session;
    private Task<PlaybackSession>? opening;
    private CancellationTokenSource? operationCancellation;
    private bool resumeAfterOpen;
    private string status = "O: open    Space/K: play/pause    Left/Right: seek 5 seconds    Click seek bar to jump";
    private double lastTick;
    private double lastUi = -1;
    private bool uiDirty = true;
    private bool disposed;
    private bool focused = true;

    public PlayerWindow(string? initialPath, string? smokeDirectory)
    {
        graphics = new NativeVideoPresenter();
        input = graphics.Window.CreateInput();
        foreach (IKeyboard keyboard in input.Keyboards)
        {
            keyboard.KeyDown += (_, key, _) => keymap.Record(key, pressed: true);
            keyboard.KeyUp += (_, key, _) => keymap.Record(key, pressed: false);
        }
        foreach (IMouse mouse in input.Mice)
        {
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += OnMouseUp;
        }
        graphics.Window.FocusChanged += isFocused =>
        {
            focused = isFocused;
            if (!focused)
            {
                keymap.Reset();
                pointer.Reset();
            }
        };
        if (smokeDirectory is not null)
        {
            smoke = new SmokeScenario(smokeDirectory);
        }
        if (initialPath is not null)
        {
            Open(initialPath, 0, play: false);
        }
    }

    public PlaybackSession? Session => session;
    public bool Busy => opening is not null;
    public NativeVideoPresenter Graphics => graphics;
    public Exception? LastError { get; private set; }

    public void Run()
    {
        while (!graphics.Window.IsClosing)
        {
            graphics.Window.DoEvents();
            double now = elapsed.Elapsed.TotalSeconds;
            double delta = now - lastTick;
            lastTick = now;
            CompleteOpen();
            if (focused)
            {
                foreach (TransportAction action in keymap.Read(delta))
                {
                    Execute(action);
                }
            }
            if (session is not null)
            {
                if (session.Failure is { } error)
                {
                    session.Pause();
                    ReportError(error);
                }
                long selectedAt = Stopwatch.GetTimestamp();
                if (session.UpdateFrame() && session.CurrentFrame is { } frame)
                {
                    graphics.RecordMediaFrame(new { frame.SourceTimestamp, frame.PositionSeconds, frame.Timing,
                        selectedHostTicks = Stopwatch.GetTimestamp(), audioClockSeconds = session.PositionSeconds });
                    graphics.UpdateVideo(frame.Width, frame.Height, frame.Rgba);
                }
                graphics.RecordCpuPhase("frameSelectionAndUploadMs", Stopwatch.GetElapsedTime(selectedAt).TotalMilliseconds);
            }
            smoke?.Tick(this);
            double layoutMs = 0, uiMs = 0;
            if (uiDirty || now - lastUi >= 0.1)
            {
                long layoutStarted = Stopwatch.GetTimestamp();
                var prepared = view.Prepare(session?.Info, session?.PositionSeconds ?? 0, session?.IsPlaying == true, status, Busy);
                layoutMs = Stopwatch.GetElapsedTime(layoutStarted).TotalMilliseconds;
                graphics.RecordCpuPhase("layoutAndPresentationBuildMs", layoutMs);
                long uiStarted = Stopwatch.GetTimestamp();
                graphics.UpdateUi(prepared.PresentationFrame);
                uiMs = Stopwatch.GetElapsedTime(uiStarted).TotalMilliseconds;
                graphics.RecordCpuPhase("nativeUiPreparationMs", uiMs);
                uiDirty = false;
                lastUi = now;
            }
            long drawStarted = Stopwatch.GetTimestamp();
            graphics.Draw();
            if (session is { IsPlaying: true, CurrentFrame: { } presented })
            {
                double observedClock = session.PositionSeconds;
                graphics.ObservePresentationClock(presented.PositionSeconds, observedClock);
                smoke?.ObservePresentedSkew(Math.Abs(observedClock - presented.PositionSeconds));
            }
            smoke?.RecordFrame(layoutMs, uiMs, Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds);
            if (smoke?.Finished == true)
            {
                return;
            }
            Thread.Sleep(1);
        }
        if (smoke is not null)
        {
            throw new InvalidOperationException("Smoke window closed before its assertions completed.");
        }
    }

    public void Execute(TransportAction action)
    {
        if (Busy)
        {
            return;
        }
        switch (action)
        {
            case TransportAction.Open:
                using (var picker = new OpenFileDialog
                {
                    Title = "Open video in Valencia",
                    Filter = "Video files|*.mp4;*.mkv;*.mov;*.webm;*.avi|All files|*.*",
                    CheckFileExists = true
                })
                {
                    if (picker.ShowDialog() == DialogResult.OK)
                    {
                        Open(picker.FileName, 0, play: false);
                    }
                }
                break;
            case TransportAction.TogglePlayback when session is not null:
                if (session.IsPlaying)
                {
                    session.Pause();
                }
                else if (session.PositionSeconds >= session.Info.DurationSeconds)
                {
                    Seek(0, play: true);
                }
                else
                {
                    session.Play();
                }
                break;
            case TransportAction.SeekBackward when session is not null:
                Seek(session.PositionSeconds - 5, session.IsPlaying);
                break;
            case TransportAction.SeekForward when session is not null:
                Seek(session.PositionSeconds + 5, session.IsPlaying);
                break;
            case TransportAction.SeekToStart when session is not null:
                Seek(0, session.IsPlaying);
                break;
        }
        uiDirty = true;
    }

    public void Seek(double seconds, bool play)
    {
        if (session is null || Busy)
        {
            return;
        }
        // Leave a decodable final frame rather than seeking past the final packet.
        Open(session.Info.Path, Math.Clamp(seconds, 0, Math.Max(0, session.Info.DurationSeconds - 0.001)), play);
    }

    private void Open(string path, double seconds, bool play)
    {
        PlaybackSession? previous = session;
        previous?.Pause();
        session = null;
        graphics.ClearVideo();
        LastError = null;
        CancellationTokenSource? previousCancellation = operationCancellation;
        operationCancellation = new CancellationTokenSource();
        CancellationToken cancellation = operationCancellation.Token;
        status = $"Opening {Path.GetFileName(path)}...";
        resumeAfterOpen = play;
        uiDirty = true;
        opening = Task.Run(async () =>
        {
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
            previousCancellation?.Dispose();
            return await PlaybackSession.OpenAsync(path, seconds, cancellation).ConfigureAwait(false);
        });
    }

    private void CompleteOpen()
    {
        if (opening?.IsCompleted != true)
        {
            return;
        }
        Task<PlaybackSession> completed = opening;
        opening = null;
        try
        {
            session = completed.GetAwaiter().GetResult();
            graphics.ClearVideo();
            if (session.CurrentFrame is { } frame)
            {
                graphics.UpdateVideo(frame.Width, frame.Height, frame.Rgba);
            }
            if (resumeAfterOpen)
            {
                session.Play();
            }
            status = $"{session.Info.Container}   |   {session.AudioBackend}   |   {graphics.GpuName}";
            Console.WriteLine($"Opened {session.Info.Path}: {session.Info.Width}x{session.Info.Height}, {session.Info.DurationSeconds:0.###}s; {session.AudioBackend}");
        }
        catch (Exception error)
        {
            ReportError(error);
        }
        uiDirty = true;
    }

    private void ReportError(Exception error)
    {
        if (ReferenceEquals(LastError, error))
        {
            return;
        }
        LastError = error;
        status = "ERROR: " + error.Message;
        Console.Error.WriteLine(error);
        uiDirty = true;
    }

    private PointerPoint LogicalPosition(IMouse mouse)
    {
        return new PointerPoint(mouse.Position.X * 1280 / Math.Max(1, graphics.Window.Size.X), mouse.Position.Y * 720 / Math.Max(1, graphics.Window.Size.Y));
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        if (button != MouseButton.Left)
        {
            return;
        }
        PointerPoint point = LogicalPosition(mouse);
        if (!Busy && session is not null && point.X >= 24 && point.X <= 1256 && point.Y >= 582 && point.Y <= 614)
        {
            Seek((point.X - 24) / 1232 * session.Info.DurationSeconds, session.IsPlaying);
            return;
        }
        pointer.Press(view.Prepared?.HitTest.HitTest(point));
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (button == MouseButton.Left)
        {
            string? name = pointer.Release(view.Prepared?.HitTest.HitTest(LogicalPosition(mouse)))?.Name;
            if (Enum.TryParse(name, out TransportAction action))
            {
                Execute(action);
            }
        }
    }

    public void Capture(string path)
    {
        var prepared = view.Prepare(session?.Info, session?.PositionSeconds ?? 0, session?.IsPlaying == true, status, Busy);
        graphics.UpdateUi(prepared.PresentationFrame);
        byte[] pixels = graphics.Draw(capture: true) ?? throw new InvalidOperationException("GPU frame capture was unavailable.");
        int width = graphics.Window.FramebufferSize.X;
        int height = graphics.Window.FramebufferSize.Y;
        if (session?.CurrentFrame is { } source)
        {
            VerifyVideoPixels(pixels, width, height, source);
        }
        // Aurelian normalizes native readback to tightly packed top-to-bottom RGBA8.
        for (int index = 0; index < pixels.Length; index += 4)
        {
            (pixels[index], pixels[index + 2]) = (pixels[index + 2], pixels[index]);
        }
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        bitmap.Save(path, ImageFormat.Png);
    }

    private static void VerifyVideoPixels(byte[] pixels, int width, int height, VideoFrame source)
    {
        float scale = Math.Min(1232f / source.Width, 470f / source.Height);
        float videoWidth = source.Width * scale;
        float videoHeight = source.Height * scale;
        foreach (double u in new[] { 0.1, 0.3, 0.5, 0.7, 0.9 })
        {
            foreach (double v in new[] { 0.2, 0.5, 0.8 })
            {
                int x = (int)(((1280 - videoWidth) / 2 + u * videoWidth) * width / 1280);
                int y = (int)((100 + (470 - videoHeight) / 2 + v * videoHeight) * height / 720);
                int sx = (int)(u * source.Width);
                int sy = (int)(v * source.Height);
                for (int channel = 0; channel < 3; channel++)
                {
                    int minimum = 255;
                    int maximum = 0;
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int sampleX = Math.Clamp(sx + dx, 0, source.Width - 1);
                            int sampleY = Math.Clamp(sy + dy, 0, source.Height - 1);
                            int value = source.Rgba[(sampleY * source.Width + sampleX) * 4 + channel];
                            minimum = Math.Min(minimum, value);
                            maximum = Math.Max(maximum, value);
                        }
                    }
                    int actual = pixels[(y * width + x) * 4 + channel];
                    if (actual < minimum - 8 || actual > maximum + 8)
                    {
                        throw new InvalidOperationException($"GPU readback differs from decoded video at ({u}, {v}), channel {channel}: {actual}, expected {minimum}..{maximum}.");
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        operationCancellation?.Cancel();
        if (opening is not null)
        {
            try
            {
                opening.GetAwaiter().GetResult().DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Closing cancels the open operation and its owned decoder workers.
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
            }
        }
        session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        operationCancellation?.Dispose();
        input.Dispose();
        graphics.Dispose();
    }
}
