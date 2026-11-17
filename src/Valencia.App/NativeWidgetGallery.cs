using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Aurelian.Machina;
using Machina.Core.Authoring;
using Machina.Core.Nodes;
using Machina.Core.Styling;
using Machina.Layout.Geometry;
using Machina.Layout.Rows;
using Machina.Pipeline;
using Machina.Runtime.Input;
using Machina.Standard.Authoring;
using Machina.Standard.Widgets;
using Valencia.Rendering;

namespace Valencia.App;

/// <summary>A thin native consumer. All widget behavior belongs to shared Machina.Standard.</summary>
internal sealed class NativeWidgetGallery : IDisposable
{
    private readonly NativeVideoPresenter graphics;
    private readonly AurelianWidgetInput input;
    private readonly WidgetSession session;
    private readonly MachinaWidgetPipeline pipeline = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly HashSet<NodeId> open = [];
    private readonly List<double> drawTimes = [], prepareTimes = [];
    private readonly List<double> idleDrawTimes = [];
    private readonly List<string> checks = [];
    private readonly string? directory;
    private MachinaPreparedPresentation? frame;
    private bool check, switched, toggle;
    private string name = "Native components", radio = "one", selected = "one", tab = "video";
    private string comboQuery = "", comboValue = "";
    // Separate models: the checkbox group is multi-valued, the alignment toggle group is single-valued.
    private ImmutableArray<string> alignment = ["left"], flags = [], expanded = [];
    private decimal number = 42;
    private string status = "Tab / arrows / Enter / Space. Text supports selection, clipboard and local undo.";
    private int commits;
    private static readonly WidgetOption[] Options = [new("one", "First"), new("two", "Disabled", true), new("three", "Third")];

    private NativeWidgetGallery(string? directory)
    {
        this.directory = directory;
        if (directory is not null) Directory.CreateDirectory(directory);
        graphics = new(tracePath: directory is null ? null : Path.Combine(directory, "frames.json"));
        graphics.Window.Title = "Machina native component gallery";
        input = new(graphics.Window);
        session = new() { Clipboard = input };
    }

    public static int Run(string? directory)
    {
        using var gallery = new NativeWidgetGallery(directory);
        return gallery.Loop();
    }

    private int Loop()
    {
        int stage = 0, idle = 0;
        bool dirty = true;
        while (!graphics.Window.IsClosing)
        {
            graphics.Window.DoEvents();
            foreach (var item in input.Poll().Events)
            {
                Apply(session.Handle(item, clock.Elapsed));
                dirty = true;
            }
            if (graphics.Window.Size.X <= 0 || graphics.Window.Size.Y <= 0) { Thread.Sleep(8); continue; }
            if (directory is not null && clock.Elapsed.TotalSeconds > 90) throw new TimeoutException("Widget smoke timed out.");
            if (dirty || frame is null)
            {
                long started = Stopwatch.GetTimestamp();
                frame = pipeline.Prepare(session, View, graphics.Window.Size.X, graphics.Window.Size.Y, new(NativeUiText.Instance));
                var deferredChanges = session.DrainChanges();
                ApplyChanges(deferredChanges);
                graphics.UpdateUi(frame.PresentationFrame);
                prepareTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                dirty = deferredChanges.Length > 0;
            }
            long drawAt = Stopwatch.GetTimestamp();
            graphics.Draw();
            drawTimes.Add(Stopwatch.GetElapsedTime(drawAt).TotalMilliseconds);
            if (directory is not null && stage >= 27) idleDrawTimes.Add(drawTimes[^1]);
            if (drawTimes.Count > 1024) drawTimes.RemoveAt(0);
            if (prepareTimes.Count > 1024) prepareTimes.RemoveAt(0);
            if (directory is not null)
            {
                if (stage < 27) { SmokeStep(stage++); dirty = true; }
                else if (++idle >= 120)
                {
                    SavePixels(Path.Combine(directory, "gallery.png"));
                    var stats = drawTimes.Order().ToArray();
                    Require(graphics.ValidationErrorCount == 0, "Vulkan validation errors.");
                    Require(input.CallbackFailures == 0 && input.DroppedBatches == 0, "Native input transport failed.");
                    File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
                    {
                        outcome = "passed", gpu = graphics.GpuName, runtime = Environment.Version.ToString(), checks, commits,
                        input.CallbackFailures, input.DroppedBatches, graphics.ValidationAvailable, graphics.ValidationErrorCount,
                        presentedFrames = graphics.PresentedFrames, cachedDrawSamples = stats.Length,
                        drawCpuMedianMs = stats[stats.Length / 2], drawCpuP95Ms = stats[(int)(stats.Length * .95)],
                        drawCpuP99Ms = stats[(int)(stats.Length * .99)], drawCpuMaximumMs = stats[^1],
                        idleDraw = Distribution(idleDrawTimes),
                        prepareMilliseconds = prepareTimes, elapsedSeconds = clock.Elapsed.TotalSeconds,
                        scalability = MeasureLargeView(),
                        inputQualification = "Native window and Vulkan; posted WM_CHAR through Silk callbacks, synthetic coordinate/key events through the shared session. Physical input and OS accessibility are not qualified."
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                }
            }
            Thread.Sleep(1);
        }
        return directory is null ? 0 : 1;
    }

    private UiNode View(WidgetBuilder w)
    {
        int width = graphics.Window.Size.X, height = graphics.Window.Size.Y;
        if (width < 1000 || height < 650)
            return UI.Rect(UI.Text("Enlarge the gallery window to 1000 x 650.", size: TextSize.Sm, color: ColorToken.Hex(0x18181BFF)), width: width, height: height);
        var children = new List<UiNode>
        {
            StandardUI.Label("MACHINA / NATIVE COMPONENTS", 20, 8, 800, 40, size: TextSize.Xl),
            StandardUI.Label(status, 20, height - 40, width - 40, 32, size: TextSize.Sm, color: ColorToken.Hex(0x18181BFF))
        };
        void Add(UiNode node, double x, double y, double nodeWidth, double nodeHeight = 36) =>
            children.Add(StandardUI.Place(node, x, y, nodeWidth, nodeHeight));
        Add(w.Checkbox("check", "Checkbox", check), 20, 70, 260);
        Add(w.Switch("switch", "Switch", switched), 20, 116, 260);
        Add(w.Toggle("toggle", "Toggle", toggle), 20, 162, 112);
        Add(w.Input("name", name, "Project name"), 20, 208, 240);
        Add(w.NumberField("number", number, "Exact decimal", 0, 100, .5m), 20, 254, 160);
        Add(w.Slider("slider", number, "Level", step: .5m), 20, 300, 240);
        Add(w.Progress("progress", number, "Progress"), 20, 358, 240, 12);
        Add(w.Meter("meter", number, "Meter"), 20, 390, 240, 12);
        Add(w.CheckboxGroup("flags", Options, flags), 20, 430, 260, 124);
        Add(w.RadioGroup("radio", Options, radio), 330, 70, 260, 124);
        Add(w.ToggleGroup("align", [new("left", "Left"), new("center", "Center"), new("right", "Right")], alignment), 330, 208, 320);
        Add(w.Tabs("tabs", [new("video", "Video", UI.Text("Native video settings", size: TextSize.Sm, color: ColorToken.Hex(0x18181BFF))),
            new("audio", "Audio", UI.Text("Native audio settings", size: TextSize.Sm, color: ColorToken.Hex(0x18181BFF)))], tab,
            activation: WidgetActivation.Manual, width: 320, contentHeight: 76), 330, 264, 320, 124);
        Add(w.Accordion("accordion", [new("details", "Details", UI.Text("Reusable disclosure content", size: TextSize.Sm, color: ColorToken.Hex(0x18181BFF)))],
            expanded, width: 320, panelHeight: 64), 330, 410, 320, 120);
        Add(w.Toolbar("tools", [new("select", "Select"), new("split", "Split"), new("move", "Move")], width: 320), 330, 550, 320);
        Add(w.Select("select", Options, selected, open.Contains("select")), 720, 70, 240);
        Add(w.Menu("menu", "Project menu", [new("open", "Open"), new("save", "Save"), new("export", "Export")], open.Contains("menu")), 720, 116, 220);
        Add(w.Dialog("dialog", "Open dialog", "Project options", w.Input("dialog-name", name, "Project name"), open.Contains("dialog")), 720, 162, 180);
        Add(w.Dialog("alert", "Open alert", "Confirm action", w.Button("confirm", "Confirm"), open.Contains("alert"), alert: true), 720, 208, 180);
        Add(w.Popover("popover", "Open popover", UI.Text("Anchored native content", size: TextSize.Sm, color: ColorToken.Hex(0x18181BFF)), open.Contains("popover")), 720, 254, 180);
        Add(w.Tooltip("tooltip", "Focus / hover me", "Native tooltip content"), 720, 300, 180);
        Add(w.Collapsible("details", "Collapsible", open.Contains("details"), UI.Text("Shared native widget behavior", size: TextSize.Sm, color: ColorToken.Hex(0x18181BFF)),
            width: 260, contentHeight: 60), 720, 358, 260, 110);
        Add(w.Combobox("combo", Options, comboValue, comboQuery, open.Contains("combo"), "Search options"), 720, 490, 240);
        Add(w.ScrollArea("scroll", UI.Column(Enumerable.Range(0, 12).Select(index =>
            w.Button("scroll-row-" + index, "Shared row " + index, width: 240)).ToArray(), gap: 8),
            528, height: 90), 720, 540, 240, 90);
        return UI.Rect(UI.Layer(children: children), width: width, height: height, style: new(ClipToBounds: true));
    }

    private void Apply(WidgetInputResult result)
    {
        input.SetPointerCapture(result.CapturePointer);
        ApplyChanges(result.Changes);
    }

    private void ApplyChanges(IEnumerable<WidgetChange> changes)
    {
        foreach (var change in changes)
        {
            switch (change)
            {
                case WidgetCheckedChanged item:
                    if (change.Target.Value == "check") check = item.Checked;
                    else if (change.Target.Value == "switch") switched = item.Checked;
                    else if (change.Target.Value == "toggle") toggle = item.Checked;
                    break;
                case WidgetTextChanged item:
                    if (item.Target.Value == "combo") comboQuery = item.Text;
                    else name = item.Text;
                    break;
                case WidgetNumberChanged item: number = item.Value; if (item.Phase == WidgetEditPhase.Commit) commits++; break;
                case WidgetSelectionChanged item:
                    string first = item.Keys.FirstOrDefault() ?? "";
                    if (change.Target.Value == "radio") radio = first;
                    else if (change.Target.Value == "select") selected = first;
                    else if (change.Target.Value == "tabs") tab = first;
                    else if (change.Target.Value == "align") alignment = item.Keys;
                    else if (change.Target.Value == "flags") flags = item.Keys;
                    else if (change.Target.Value == "accordion") expanded = item.Keys;
                    else if (change.Target.Value == "combo") comboValue = first;
                    break;
                case WidgetOpenChanged item: if (item.Open) open.Add(change.Target); else open.Remove(change.Target); break;
            }
            status = change is WidgetOperationFailed failure ? failure.Message : change.GetType().Name + " / " + change.Target.Value;
        }
    }

    private void SmokeStep(int stage)
    {
        switch (stage)
        {
            case 0: Click("check"); Require(check, "Checkbox did not toggle."); checks.Add("pointer checkbox"); break;
            case 1: Key(UiKey.Space); Key(UiKey.Space, false); Require(!check, "Space activation failed."); checks.Add("space release"); break;
            case 2:
                session.Focus("name"); Key(UiKey.A, modifiers: new(true, false, false, false));
                NativeText("Shared native widgets"); Require(name == "Shared native widgets", "Text entry failed.");
                checks.Add("native WM_CHAR transport and text replacement"); break;
            case 3:
                session.Focus("number"); Key(UiKey.A, modifiers: new(true, false, false, false));
                Apply(session.Handle(new UiTextEntered("12.5"))); Key(UiKey.Enter);
                Require(number == 12.5m, "Decimal editing failed."); checks.Add("decimal field"); break;
            case 4:
                var rect = frame!.Resolved.Nodes[new NodeId("slider")].Rect;
                Pointer(rect.X + rect.Width / 2, rect.Y + 10, true); Pointer(rect.X + rect.Width / 2, rect.Y + 10, false);
                Require(number == 50, "Slider mapping failed."); checks.Add("slider preview and commit"); break;
            case 5: session.Focus("radio.one"); Key(UiKey.ArrowDown); Require(radio == "three", "Radio skip disabled failed."); checks.Add("roving radio"); break;
            case 6: session.Focus("tabs.tabs.video"); Key(UiKey.ArrowRight); Require(tab == "video", "Manual tab activated prematurely."); Key(UiKey.Enter); Require(tab == "audio", "Tab activation failed."); checks.Add("manual tabs"); break;
            case 7: Click("select.trigger"); Require(open.Contains("select"), "Select did not open."); break;
            case 8: Key(UiKey.End); Key(UiKey.Enter); Require(selected == "three" && !open.Contains("select"), "Select commit failed."); checks.Add("select navigation"); break;
            case 9: Click("dialog.trigger"); break;
            case 10: Key(UiKey.Tab); Require(session.Focused is { } focus && (focus.Value == "dialog.close" || focus.Value == "dialog-name"), "Modal focus escaped."); Key(UiKey.Escape); checks.Add("dialog focus and escape"); break;
            case 11: Require(session.Focused == new NodeId("dialog.trigger"), "Focus was not restored."); Click("alert.trigger"); break;
            case 12: Pointer(1, 1, true); Pointer(1, 1, false); Require(open.Contains("alert"), "Alert dismissed on outside click."); Key(UiKey.Escape); checks.Add("alert outside blocking"); break;
            case 13: Click("popover.trigger"); break;
            case 14: Key(UiKey.Escape); checks.Add("popover escape"); break;
            case 15: Click("menu.trigger"); break;
            case 16: Key(UiKey.End); Key(UiKey.Enter); Require(!open.Contains("menu"), "Menu did not close."); checks.Add("menu selection"); break;
            case 17: Click("accordion.details.trigger"); Require(expanded.Contains("details"), "Accordion did not expand."); checks.Add("accordion"); break;
            case 18: session.Focus("tooltip.trigger"); break;
            case 19: SavePixels(Path.Combine(directory!, "tooltip.png")); graphics.Window.Size = new(768, 480); break;
            case 20: graphics.Window.Size = new(1280, 720); checks.Add("compact resize and restore"); break;
            case 21: session.Focus("name"); Key(UiKey.A, modifiers: new(true, false, false, false)); Apply(session.Handle(new UiTextEntered("Undo probe"))); break;
            case 22: Key(UiKey.Z, modifiers: new(true, false, false, false)); Require(name == "Shared native widgets", "Input undo failed."); checks.Add("local text undo"); break;
            case 23:
                session.Focus("combo.input");
                Apply(session.Handle(new UiTextEntered("th")));
                break;
            case 24:
                Key(UiKey.ArrowDown); Key(UiKey.Enter);
                Require(comboValue == "three" && !open.Contains("combo"), "Combobox selection failed.");
                checks.Add("filtered combobox");
                break;
            case 25:
                session.Focus("scroll"); Key(UiKey.End);
                break;
            case 26:
                Rect row = frame!.Resolved.Nodes[new NodeId("scroll-row-11")].Rect;
                Rect viewport = frame.Resolved.Nodes[new NodeId("scroll")].Rect;
                Require(row.Y >= viewport.Y && row.Y + row.Height <= viewport.Y + viewport.Height, "Scroll focus was not visible.");
                checks.Add("scroll area keyboard navigation");
                break;
        }
    }

    private void NativeText(string text)
    {
        nint handle = graphics.Window.Native?.Win32?.Hwnd ?? 0;
        Require(OperatingSystem.IsWindows() && handle != 0, "Native text smoke requires Windows.");
        foreach (char character in text)
            Require(PostMessage(handle, 0x0102, character, 0), "Could not queue WM_CHAR.");
        graphics.Window.DoEvents();
        var batch = input.Poll();
        Require(batch.Events.OfType<UiTextEntered>().Any(), "Native character callbacks did not arrive.");
        foreach (var item in batch.Events) Apply(session.Handle(item, clock.Elapsed));
    }

    private static object MeasureLargeView()
    {
        using var benchmarkSession = new WidgetSession();
        var benchmarkPipeline = new MachinaWidgetPipeline();
        var samples = new List<double>();
        for (int iteration = 0; iteration < 6; iteration++)
        {
            long started = Stopwatch.GetTimestamp();
            benchmarkPipeline.Prepare(benchmarkSession, builder => builder.ScrollArea("large",
                UI.Column(Enumerable.Range(0, 1000).Select(index => builder.Checkbox("item-" + index,
                    "Item " + index, false)).ToArray(), gap: 8), 44_000, width: 600, height: 600), 1280, 720);
            samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        return new { widgets = 1000, prepareCpuMilliseconds = samples, virtualized = false,
            note = "CPU declaration/layout/presentation only, not a GPU draw or a 1000-widget responsiveness qualification." };
    }

    private static object Distribution(List<double> values)
    {
        var sorted = values.Order().ToArray();
        return new
        {
            samples = sorted.Length,
            cpuMedianMs = sorted[sorted.Length / 2],
            cpuP95Ms = sorted[(int)(sorted.Length * .95)],
            cpuP99Ms = sorted[(int)(sorted.Length * .99)],
            cpuMaximumMs = sorted[^1]
        };
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(nint window, uint message, nuint word, nint data);

    private void Click(NodeId id)
    {
        Rect rect = frame!.Resolved.Nodes[id].Rect;
        Pointer(rect.X + rect.Width / 2, rect.Y + rect.Height / 2, true);
        Pointer(rect.X + rect.Width / 2, rect.Y + rect.Height / 2, false);
    }
    private void Pointer(double x, double y, bool down) => Apply(session.Handle(new UiPointerButtonChanged(new(x, y), UiPointerButton.Primary, down, default), clock.Elapsed));
    private void Key(UiKey key, bool down = true, UiModifiers modifiers = default) => Apply(session.Handle(new UiKeyChanged(key, down, false, modifiers), clock.Elapsed));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private void SavePixels(string path)
    {
        byte[] pixels = graphics.Draw(true) ?? throw new InvalidOperationException("No native readback.");
        using var bitmap = new Bitmap(graphics.Window.FramebufferSize.X, graphics.Window.FramebufferSize.Y, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int index = 0; index < pixels.Length; index += 4) (pixels[index], pixels[index + 2]) = (pixels[index + 2], pixels[index]);
        var data = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); } finally { bitmap.UnlockBits(data); }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    public void Dispose() { session.Dispose(); input.Dispose(); graphics.Dispose(); }
}
