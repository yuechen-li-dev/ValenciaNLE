using Machina.Layout.Geometry;

namespace Valencia.App.Editing;

internal enum EditorLayoutProfile { Desktop, VerticalTablet, VerticalPhone }

/// <summary>
/// Three explicit native roots, following Copeland TS LayoutProfiles.ts's single
/// width law (&lt;600, 600..1023, >=1024), with a native landscape-tablet override.
/// This describes panel slots, not an alternative constraint/layout engine.
/// Coordinates are window client units; the framebuffer/DPI scale belongs to Aurelian.
///
/// Desktop follows the Premiere editing workspace: panel (media/transcript/record) top-left,
/// monitor (program or source) with its transport underneath, then an inspector column when the monitor
/// would otherwise be wider than 16:9, full-width timeline below, status bar last.
/// Portrait roots stack the same slots: monitor, transport, panel, timeline.
/// </summary>
internal sealed record EditorLayout(EditorLayoutProfile Profile, int Width, int Height,
    Rect Header, Rect Panel, Rect Monitor, Rect Transport, Rect Toolbar, double RulerY, double ClipLeft, Rect Inspector = default)
{
    /// <summary>Header command row: first button x, button width and spacing on single-row headers.</summary>
    public const double HeaderCommandsLeft = 196, HeaderCommandWidth = 84, HeaderCommandGap = 2;
    public const double Gap = 8, HeaderRowHeight = 40, TabHeight = 30, TransportRowHeight = 36, ToolbarRowHeight = 32,
        RulerHeight = 24, StatusHeight = 26;
    public bool Portrait => Profile != EditorLayoutProfile.Desktop;
    public Rect Media => Panel;
    public int HeaderRows => Header.Height > HeaderRowHeight ? 2 : 1;
    public int TransportRows => Transport.Height > TransportRowHeight + 1 ? 2 : 1;
    public int ToolbarRows => Toolbar.Height > ToolbarRowHeight + 1 ? 2 : 1;
    /// <summary>Desktop docks the master edge meter on the timeline's right edge.</summary>
    public const double EdgeMeterWidth = 26;
    public double ClipRight => Width - Gap - (Portrait ? 0 : EdgeMeterWidth);
    public bool HasEdgeMeter => !Portrait;
    public double TrackTop => RulerY + RulerHeight;
    public double StatusY => Height - StatusHeight;
    public double FooterReserve => StatusHeight;
    public double TrackBottom => Math.Max(TrackTop, StatusY - 2);
    public bool HasInspector => Inspector.Width > 0;

    public static EditorLayout For(int width, int height, int captionHeight = 0)
    {
        var layout = Content(width, Math.Max(1, height - captionHeight));
        if (captionHeight == 0) return layout;
        Rect Shift(Rect box) => box with { Y = box.Y + captionHeight };
        return layout with
        {
            Height = height, Header = Shift(layout.Header), Panel = Shift(layout.Panel),
            Monitor = Shift(layout.Monitor), Transport = Shift(layout.Transport), Toolbar = Shift(layout.Toolbar),
            Inspector = Shift(layout.Inspector), RulerY = layout.RulerY + captionHeight
        };
    }

    private static EditorLayout Content(int width, int height) => width >= 600 && width >= height
        ? DesktopRoot(width, height) : width switch
    {
        < 600 => Stacked(EditorLayoutProfile.VerticalPhone, width, height),
        < 1024 => Stacked(EditorLayoutProfile.VerticalTablet, width, height),
        _ => DesktopRoot(width, height)
    };

    private static EditorLayout DesktopRoot(int w, int h)
    {
        double top = HeaderRowHeight, content = Math.Max(0, h - top - StatusHeight);
        double upper = Math.Clamp(Math.Round(content * (h >= 1200 ? .55 : .5)), 200, Math.Max(200, content - 150));
        double panelWidth = Math.Clamp(Math.Round(w * .24), 280, 460);
        double areaX = Gap + panelWidth + Gap, areaWidth = Math.Max(1, w - areaX - Gap);
        // Spend width beyond a 16:9 picture on an inspector column instead of pillarbox.
        double pictureHeight = Math.Max(1, upper - Gap - TransportRowHeight - 4);
        double spare = areaWidth - pictureHeight * 16 / 9 - Gap;
        double inspectorWidth = spare >= 280 ? Math.Min(440, spare) : 0;
        double monitorWidth = inspectorWidth > 0 ? areaWidth - inspectorWidth - Gap : areaWidth;
        double transportHeight = monitorWidth < 720 ? 2 * TransportRowHeight : TransportRowHeight;
        var panel = new Rect(Gap, top + Gap, panelWidth, Math.Max(1, upper - Gap));
        var transport = new Rect(areaX, top + upper - transportHeight, monitorWidth, transportHeight);
        var monitor = new Rect(areaX, top + Gap, monitorWidth, Math.Max(1, transport.Y - top - Gap - 4));
        var inspector = inspectorWidth > 0 ? new Rect(areaX + monitorWidth + Gap, top + Gap, inspectorWidth, Math.Max(1, upper - Gap)) : default;
        var toolbar = new Rect(Gap, top + upper + Gap, w - 2 * Gap, w < 1100 ? 2 * ToolbarRowHeight + 4 : ToolbarRowHeight);
        return new(EditorLayoutProfile.Desktop, w, h, new(0, 0, w, HeaderRowHeight), panel, monitor, transport, toolbar,
            toolbar.Y + toolbar.Height + 4, 158, inspector);
    }

    private static EditorLayout Stacked(EditorLayoutProfile profile, int w, int h)
    {
        bool phone = profile == EditorLayoutProfile.VerticalPhone;
        double header = phone || w < 760 ? 2 * HeaderRowHeight : HeaderRowHeight;
        double inner = Math.Max(1, w - 2 * Gap);
        var monitor = new Rect(Gap, header + Gap, inner, Math.Max(1, Math.Round(h * (phone ? .22 : .28))));
        double transportHeight = phone ? 2 * TransportRowHeight : TransportRowHeight;
        var transport = new Rect(Gap, monitor.Y + monitor.Height + 4, inner, transportHeight);
        var panel = new Rect(Gap, transport.Y + transport.Height + Gap, inner, Math.Max(1, Math.Round(h * (phone ? .2 : .22))));
        double toolbarHeight = 2 * ToolbarRowHeight + 4;
        var toolbar = new Rect(Gap, panel.Y + panel.Height + Gap, inner, toolbarHeight);
        return new(profile, w, h, new(0, 0, w, header), panel, monitor, transport, toolbar,
            toolbar.Y + toolbar.Height + 4, phone ? 104 : 158);
    }
}
