using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Valencia.App.Editing;

/// <summary>Borderless fullscreen on the current display, with the same HWND and display mode.</summary>
internal sealed class FullscreenWindow(nint handle)
{
    private const int StyleIndex = -16;
    private const int Overlapped = 0x00CF0000, Maximized = 0x01000000, Popup = unchecked((int)0x80000000);
    private const uint FrameChanged = 0x0020, NoMove = 0x0002, NoSize = 0x0001, NoZOrder = 0x0004, NoOwnerZOrder = 0x0200;
    private Placement savedPlacement;
    private int savedStyle;

    public void Enter()
    {
        var monitor = Screen.FromHandle(handle).Bounds;
        savedPlacement = new() { Length = Marshal.SizeOf<Placement>() };
        Check(GetWindowPlacement(handle, ref savedPlacement));
        savedStyle = GetWindowLong(handle, StyleIndex);
        Check(savedStyle != 0);
        SetStyle((savedStyle & ~(Overlapped | Maximized)) | Popup);
        if (!SetWindowPos(handle, 0, monitor.X, monitor.Y, monitor.Width, monitor.Height, FrameChanged | NoOwnerZOrder))
        {
            var error = new Win32Exception();
            Restore();
            throw error;
        }
    }

    public void Restore()
    {
        SetStyle(savedStyle);
        Check(SetWindowPlacement(handle, ref savedPlacement));
        Check(SetWindowPos(handle, 0, 0, 0, 0, 0, FrameChanged | NoSize | NoMove | NoZOrder | NoOwnerZOrder));
    }

    private void SetStyle(int style) => Check(SetWindowLong(handle, StyleIndex, style) != 0);

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Placement
    {
        public int Length, Flags, ShowCommand;
        public Point MinPosition, MaxPosition;
        public Rect NormalPosition;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(nint handle, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(nint handle, ref Placement placement);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(nint handle, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
}
