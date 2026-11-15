using Machina.Core.Actions;
using Machina.Core.Authoring;
using Machina.Core.Nodes;
using Machina.Core.Styling;
using Machina.Pipeline;
using Machina.Standard.Authoring;
using Valencia.Core;

namespace Valencia.App;

internal sealed class TransportView
{
    private readonly MachinaPresentationPipeline pipeline = new();
    public MachinaPreparedPresentation? Prepared { get; private set; }

    public MachinaPreparedPresentation Prepare(MediaInfo? info, double position, bool playing, string status, bool busy)
    {
        var children = new List<UiNode>
        {
            Text("VALENCIA", 24, 20, 220, 35, TextSize.H1),
            Text("M0  /  Native video player", 256, 27, 700, 24),
            StandardUI.Button("Open video", UiAction.Named("Open"), 1080, 18, 176, 40, disabled: busy),
            Text(info is null ? "Open an MP4 or MKV to begin" : Shorten(Path.GetFileName(info.Path), 94), 24, 70, 1232, 24),
            StandardUI.Place(UI.Rect(color: ColorToken.Hex(0x090B10FF)), 24, 100, 1232, 470),
            StandardUI.Place(UI.Rect(color: ColorToken.Hex(0x30394BFF)), 24, 595, 1232, 10),
            StandardUI.Button(playing ? "Pause" : "Play", UiAction.Named("TogglePlayback"), 24, 623, 130, 40, disabled: info is null || busy),
            StandardUI.Button("-5 sec", UiAction.Named("SeekBackward"), 170, 623, 130, 40, disabled: info is null || busy),
            StandardUI.Button("+5 sec", UiAction.Named("SeekForward"), 316, 623, 130, 40, disabled: info is null || busy),
            Text($"{FormatTime(position)} / {FormatTime(info?.DurationSeconds ?? 0)}", 480, 634, 320, 25),
            Text(Shorten(status, 99), 24, 684, 1232, 24, TextSize.Sm)
        };
        if (info is not null)
        {
            double progress = Math.Clamp(position / info.DurationSeconds, 0, 1);
            children.Add(StandardUI.Place(UI.Rect(color: ColorToken.Hex(0x8A93FFFF)), 24, 595, Math.Max(1, 1232 * progress), 10));
            children.Add(Text($"{info.Width}x{info.Height}   {info.NominalFrameRate:0.###} fps   {info.Streams.Count(stream => stream.Kind == "Audio")} audio stream(s)", 820, 634, 440, 25, TextSize.Sm));
        }
        Prepared = pipeline.Prepare(UI.Surface(color: ColorToken.Hex(0x171C27FF), children: children), 1280, 720,
            new Machina.Core.Lowering.UiLoweringOptions(Valencia.Rendering.NativeUiText.Instance));
        return Prepared;
    }

    private static UiNode Text(string value, double x, double y, double width, double height, TextSize size = TextSize.Md)
    {
        return StandardUI.Place(UI.Text(value, color: ColorToken.Hex(0xE4E9F4FF), size: size), x, y, width, height);
    }

    private static string Shorten(string value, int length) => value.Length <= length ? value : value[..(length - 3)] + "...";
    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss\.fff");
}
