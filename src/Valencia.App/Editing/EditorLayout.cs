using Machina.Layout.Geometry;

namespace Valencia.App.Editing;

internal enum EditorLayoutProfile { Desktop, VerticalTablet, VerticalPhone }

/// <summary>
/// Three explicit native roots, following Copeland TS LayoutProfiles.ts's single
/// width law (&lt;600, 600..1023, >=1024), with a native landscape-tablet override.
/// This describes panel slots, not an alternative constraint/layout engine.
/// Coordinates are window client units; the framebuffer/DPI scale belongs to Aurelian.
/// </summary>
internal sealed record EditorLayout(EditorLayoutProfile Profile, int Width, int Height,
    Rect Media, Rect Monitor, double ToolsY, double RulerY, int HeaderColumns, int ToolColumns)
{
    public bool Portrait => Profile != EditorLayoutProfile.Desktop;
    public double ClipLeft => Profile == EditorLayoutProfile.VerticalPhone ? 104 : 166;
    public double ClipRight => Width - (Portrait ? 18 : 24);
    public double TrackTop => RulerY + 20;
    public double FooterReserve => Profile switch { EditorLayoutProfile.VerticalPhone => 105, EditorLayoutProfile.VerticalTablet => 90, _ => 75 };
    public double TrackBottom => Math.Max(TrackTop, Height - FooterReserve);
    public int VisibleTracks => Math.Max(0, (int)((TrackBottom - TrackTop) / TimelineViewport.RowHeight));
    public static EditorLayout For(int width, int height) => width >= 600 && width >= height
        ? DesktopRoot(width, height) : width switch
    {
        < 600 => VerticalPhoneRoot(width, height),
        < 1024 => VerticalTabletRoot(width, height),
        _ => DesktopRoot(width, height)
    };

    private static EditorLayout DesktopRoot(int w, int h) => new(EditorLayoutProfile.Desktop, w, h,
        new(18, 57, 288, 277), new(326, 64, Math.Max(1, w - 350), 270), 345, 378, 8, 8);
    private static EditorLayout VerticalTabletRoot(int w, int h) => new(EditorLayoutProfile.VerticalTablet, w, h,
        new(18, 364, Math.Max(1, w - 36), 230), new(18, 128, Math.Max(1, w - 36), 224), 606, 714, 4, 4);
    private static EditorLayout VerticalPhoneRoot(int w, int h) => new(EditorLayoutProfile.VerticalPhone, w, h,
        new(18, 288, Math.Max(1, w - 36), 206), new(18, 128, Math.Max(1, w - 36), 148), 506, 614, 4, 4);
}
