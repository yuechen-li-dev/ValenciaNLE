using Machina.Core.Actions;
using Machina.Core.Authoring;
using Machina.Core.Nodes;
using Machina.Core.Styling;
using Machina.Pipeline;
using Machina.Standard.Authoring;
using Valencia.Core.Timeline;
using Valencia.Rendering;

namespace Valencia.App.Editing;

internal sealed class EditorView
{
    private readonly MachinaPresentationPipeline pipeline = new();
    public MachinaPreparedPresentation? Prepared { get; private set; }
    public IReadOnlyList<CanvasRectangle> Canvas { get; private set; } = [];
    public int VisibleClips { get; private set; }
    public EditorLayout Layout { get; private set; } = EditorLayout.For(1280, 720);
    public MachinaPreparedPresentation Prepare(EditorDocument editor, TimelineGesture gesture, bool playing, bool razor,
        int width = 1280, int height = 720)
    {
        var layout = Layout = EditorLayout.For(width, height);
        var sequence = editor.Sequence;
        var viewport = editor.Viewport;
        viewport.Configure(layout);
        viewport.FirstTrack = Math.Clamp(viewport.FirstTrack, 0, Math.Max(0, sequence.Tracks.Length - viewport.VisibleTracks));
        var children = new List<UiNode> { Text("VALENCIA", 18, 6, 200, 42, TextSize.H1) };
        var commands = new[] { ("New", "New"), ("Open", "Open"), ("Save", "Save"), ("Save as", "SaveAs"), ("Import", "Import"), ("Insert", "Insert"), ("Export", "Export"), ("Cancel job", "Cancel") };
        double headerX = layout.Portrait ? 18 : 226;
        double headerCell = (width - headerX - 18) / layout.HeaderColumns;
        for (int i = 0; i < commands.Length; i++)
            children.Add(Button(commands[i].Item1, commands[i].Item2, headerX + i % layout.HeaderColumns * headerCell,
                (layout.Portrait ? 48 : 12) + i / layout.HeaderColumns * 36, headerCell - 6, 30));
        children.Add(StandardUI.Frame(layout.Media.X, layout.Media.Y, layout.Media.Width, layout.Media.Height,
            MediaPanel(editor, layout.Portrait, layout.Media.Width)));
        children.Add(StandardUI.Place(UI.Rect(color: ColorToken.Hex(0x000000FF)), layout.Monitor.X, layout.Monitor.Y, layout.Monitor.Width, layout.Monitor.Height));
        var tools = new[] { (playing ? "Pause" : "Play", "TogglePlay"), ("Split", "Split"), ("Delete", "Delete"), ("Ripple", "RippleDelete"), ("Undo", "Undo"), ("Redo", "Redo"), ("- Zoom", "ZoomOut"), ("+ Zoom", "ZoomIn") };
        double toolCell = (layout.Portrait ? width - 36 : Math.Max(600, width - 466)) / (double)layout.ToolColumns;
        for (int i = 0; i < tools.Length; i++)
            children.Add(Button(tools[i].Item1, tools[i].Item2, 18 + i % layout.ToolColumns * toolCell,
                layout.ToolsY + i / layout.ToolColumns * 36, toolCell - 6, 32));
        double infoY = layout.ToolsY + (layout.Portrait ? 74 : 7);
        double infoX = layout.Portrait ? 18 : width - 442;
        children.Add(Text($"{editor.Playhead.TotalSeconds:0.000}s / {sequence.Duration.TotalSeconds:0.000}s", infoX, infoY, 215, 24));
        children.Add(Button("View: " + (sequence.Creator?.Profiles.FirstOrDefault(p => p.Id == editor.SelectedProfile)?.Name ?? "sequence"), "OutputProfile",
            layout.Portrait ? width - 188 : width - 220, layout.Portrait ? infoY - 3 : layout.ToolsY, 170, 28));
        double addWidth = (layout.ClipLeft - 34) / 2;
        children.Add(Button("+V", "AddVideo", 18, layout.RulerY - 2, addWidth - 4, 21));
        children.Add(Button("+A", "AddAudio", 18 + addWidth, layout.RulerY - 2, addWidth - 4, 21));
        double spacing = Math.Pow(2, Math.Ceiling(Math.Log2(100 / viewport.PixelsPerSecond)));
        double first = Math.Ceiling(viewport.Origin.TotalSeconds / spacing) * spacing;
        for (double second = first; second < viewport.Time(viewport.ClipRight).TotalSeconds; second += spacing)
            children.Add(Text($"{second:0.##}", viewport.X(new((long)(second * Valencia.Core.SequenceTime.TicksPerSecond))), layout.RulerY, 76, 19, TextSize.Sm));
        var quads = new List<CanvasRectangle>(); VisibleClips = 0;
        for (int i = viewport.FirstTrack; i < Math.Min(sequence.Tracks.Length, viewport.FirstTrack + viewport.VisibleTracks); i++)
        {
            var track = sequence.Tracks[i]; float y = (float)(viewport.TrackTop + (i - viewport.FirstTrack) * TimelineViewport.RowHeight);
            children.Add(Text(track.Name, 18, y + 4, layout.ClipLeft - 32, 18, TextSize.Sm));
            double headerStep = (layout.ClipLeft - 30) / 3;
            children.Add(Button(track.State.Locked ? "L*" : "L", "Lock:" + track.Id, 18, y + 21, headerStep - 4, 25));
            children.Add(Button(track.Kind == TrackKind.Audio ? track.State.Muted ? "M*" : "M" : track.State.Visible ? "On" : "Off", "State:" + track.Id, 18 + headerStep, y + 21, headerStep - 4, 25));
            children.Add(Button(editor.TargetTrack == track.Id ? ">>" : ">", "Target:" + track.Id, 18 + 2 * headerStep, y + 21, headerStep - 4, 25));
            quads.Add(new((float)viewport.ClipLeft, y, (float)(viewport.ClipRight - viewport.ClipLeft), 47, i % 2 == 0 ? 0x242D3EFFu : 0x202839FFu));
            foreach (var clip in viewport.Visible(track))
            {
                VisibleClips++;
                uint color = clip.Id == editor.SelectedClip ? 0xD8AE4FFFu : track.Kind == TrackKind.Video ? 0x647BD4FFu : 0x359E86FFu;
                ClipRect(clip, y, color);
                if (track.Kind == TrackKind.Audio && editor.Waveforms.TryGetValue((clip.AssetId, clip.StreamIndex), out var waveform))
                {
                    var visibleStart = new Valencia.Core.SequenceTime(Math.Max(clip.Start.Ticks, viewport.Origin.Ticks));
                    var visibleEnd = new Valencia.Core.SequenceTime(Math.Min(clip.End.Ticks, viewport.Time(viewport.ClipRight).Ticks));
                    var sourceStart = clip.SourceIn.Shift(new(visibleStart.Ticks - clip.Start.Ticks));
                    var sourceEnd = clip.SourceIn.Shift(new(visibleEnd.Ticks - clip.Start.Ticks));
                    int pixels = Math.Max(1, (int)(viewport.X(visibleEnd) - viewport.X(visibleStart)));
                    foreach (var bin in waveform.Visible(sourceStart, sourceEnd, pixels))
                    {
                        long from = Math.Max(visibleStart.Ticks, clip.Start.Ticks + bin.Start.Since(clip.SourceIn).Ticks);
                        long to = Math.Min(visibleEnd.Ticks, clip.Start.Ticks + bin.End.Since(clip.SourceIn).Ticks);
                        float x = (float)viewport.X(new(from));
                        float binWidth = Math.Max(1, (float)(viewport.X(new(to)) - x));
                        for (int channel = 0; channel < bin.Channels.Length; channel++)
                        {
                            var peak = bin.Channels[channel];
                            float amplitude = Math.Clamp(Math.Max(Math.Abs(peak.Minimum), Math.Abs(peak.Maximum)), 0, 1);
                            float center = y + 7 + (channel + .5f) * 32 / bin.Channels.Length;
                            float binHeight = Math.Max(1, amplitude * 30 / bin.Channels.Length);
                            quads.Add(new(x, center - binHeight / 2, binWidth, binHeight, 0xB2F2DFFF));
                        }
                    }
                }
            }
            if (gesture.Ghost is { } ghost && (gesture.Proposal is Valencia.Core.Editing.MoveClip move ? move.DestinationTrackId == track.Id : track.Clips.Any(c => c.Id == ghost.Id)))
                ClipRect(ghost, y, gesture.Valid ? 0x85D87DCC : 0xE64C58CC);
        }
        float playhead = (float)viewport.X(editor.Playhead);
        if (editor.ProposedCut is { } proposal)
            foreach (var removal in proposal.Removed)
            {
                float left = (float)Math.Clamp(viewport.X(removal.Start), viewport.ClipLeft, viewport.ClipRight);
                float right = (float)Math.Clamp(viewport.X(removal.End), viewport.ClipLeft, viewport.ClipRight);
                if (right > left) quads.Add(new(left, (float)viewport.TrackTop, right - left, (float)(viewport.TrackBottom - viewport.TrackTop), 0xE65A5A55));
            }
        if (editor.ProposedTextCut is { } textProposal)
            foreach (var removal in textProposal.Removed)
            {
                float left = (float)Math.Clamp(viewport.X(removal.Start), viewport.ClipLeft, viewport.ClipRight), right = (float)Math.Clamp(viewport.X(removal.End), viewport.ClipLeft, viewport.ClipRight);
                if (right > left) quads.Add(new(left, (float)viewport.TrackTop, right - left, (float)(viewport.TrackBottom - viewport.TrackTop), 0xE65A5A55));
            }
        if (editor.MarkIn is not null || editor.MarkOut is not null)
        {
            var markEnd = editor.MarkOut ?? sequence.Duration;
            float markLeft = (float)Math.Clamp(viewport.X(editor.MarkIn ?? default), viewport.ClipLeft, viewport.ClipRight);
            float markRight = (float)Math.Clamp(viewport.X(markEnd), viewport.ClipLeft, viewport.ClipRight);
            if (markRight > markLeft) quads.Add(new(markLeft, (float)layout.RulerY, markRight - markLeft, 19, 0x6FA8FF66));
        }
        if (playhead >= viewport.ClipLeft && playhead <= viewport.ClipRight) quads.Add(new(playhead, (float)layout.RulerY, 2, (float)(viewport.TrackBottom - layout.RulerY), 0xFFCE72FF));
        string selection = editor.Selection is { } chosen ? $"{Short(chosen.Properties.Name, 24)}  [{chosen.Start.TotalSeconds:0.000}, {chosen.End.TotalSeconds:0.000})  |  " : "";
        double footerY = height - layout.FooterReserve + 12;
        children.Add(Text(NativeUiText.Ellipsize(selection + editor.Status, width - 36), 18, footerY, width - 36, 20, TextSize.Sm));
        var hints = NativeUiText.WrapHints(["Ctrl+I import", "Ctrl+K split", "Del / Shift+Del", "Ctrl+Z undo", "Ctrl+Shift+Z redo",
            "Wheel scroll", "Ctrl+wheel zoom", "Shift+wheel tracks"], width - 36);
        for (int i = 0; i < hints.Count; i++)
            children.Add(Text(hints[i], 18, footerY + 24 + i * 18, width - 36, 18, TextSize.Sm));
        Canvas = quads; Prepared = pipeline.Prepare(UI.Surface(color: ColorToken.Hex(0x171C27FF), children: children), width, height,
            new Machina.Core.Lowering.UiLoweringOptions(NativeUiText.Instance)); return Prepared;

        void ClipRect(TimelineClip c, float y, uint color)
        {
            float left = (float)Math.Clamp(viewport.X(c.Start), viewport.ClipLeft, viewport.ClipRight), right = (float)Math.Clamp(viewport.X(c.End), viewport.ClipLeft, viewport.ClipRight);
            if (right <= left) return;
            quads.Add(new(left, y + 5, Math.Max(1, right - left), 37, color));
            quads.Add(new(left, y + 5, Math.Min(3, right - left), 37, 0xD3DBF4FF));
            quads.Add(new(Math.Max(left, right - 3), y + 5, Math.Min(3, right - left), 37, 0xD3DBF4FF));
        }
    }
    private static IReadOnlyList<UiNode> MediaPanel(EditorDocument editor, bool compact, double width)
    {
        var items = new List<UiNode>();
        double half = (width - 10) / 2;
        double footer = compact ? 138 : 207;
        items.Add(Button(editor.TranscriptMode ? "Media" : "Transcript", "TranscriptMode", 0, 0, half, 30));
        items.Add(Button(editor.ObsMode ? "Back to media" : "OBS", "ObsMode", half + 10, 0, half, 30));
        items.Add(Text((editor.Dirty ? "* " : "") + Short(Path.GetFileName(editor.ProjectPath ?? "Untitled.valencia"), compact ? 24 : 20), 0, 37, width - 98, 22));
        items.Add(Button("Speech", "Transcribe", width - 88, 34, 88, 28));
        if (editor.ObsMode)
        {
            items.Add(Text(Short(editor.ObsConnection + " / " + editor.ObsRecording, (int)(width / 7)), 0, 65, width, 22, TextSize.Sm));
            double connectY = compact ? 86 : 91, step = compact ? 31 : 33;
            items.Add(Button("Connect/disconnect", "ObsConnect", 0, connectY, width, 27));
            items.Add(Button("Start", "ObsStart", 0, connectY + step, half, 27));
            items.Add(Button("Stop", "ObsStop", half + 10, connectY + step, half, 27));
            items.Add(Button("Pause/resume", "ObsPause", 0, connectY + 2 * step, half, 27));
            items.Add(Button("Mark", "ObsMarker", half + 10, connectY + 2 * step, half, 27));
            items.Add(Button("Import completed", "ObsImport", 0, connectY + 3 * step, width, 27));
            return items;
        }
        if (editor.TranscriptMode && editor.Transcript?.Transcript is { } transcript)
        {
            var active = new TimelineQueries(editor.Session.Snapshot).Evaluate(editor.SequenceId, editor.Playhead);
            int row = 0;
            foreach (var word in transcript.Words.Skip(editor.TranscriptFirstWord).Take(compact ? 3 : 6))
            {
                int index = editor.TranscriptFirstWord + row++;
                bool selected = editor.TranscriptSelectionStart is { } a && editor.TranscriptSelectionEnd is { } b && index >= Math.Min(a, b) && index <= Math.Max(a, b);
                bool speaking = active.Any(c => c.Asset.Id == editor.Transcript.AssetId && c.SourcePosition.CompareTo(word.Range.Start) >= 0 && c.SourcePosition.CompareTo(word.Range.End) < 0);
                items.Add(Button(Short((selected ? "> " : speaking ? "* " : "") + word.Text + (word.TimingUsable ? "" : " [?]"), (int)(width / 8)), "TranscriptWord:" + index, 0, 65 + (row - 1) * 23, width, 21));
            }
            double third = (width - 20) / 3;
            items.Add(Button("Find", "TranscriptFind", 0, footer, third, 28));
            items.Add(Button("Prev", "TranscriptPrev", third + 10, footer, third, 28));
            items.Add(Button("Next", "TranscriptNext", 2 * third + 20, footer, third, 28));
            if (editor.ProposedTextCut is null)
            {
                items.Add(Button("Retain", "TextRetain", 0, footer + 36, half, 28));
                items.Add(Button("Remove", "TextRemove", half + 10, footer + 36, half, 28));
            }
        }
        else
        {
            int index = 0;
            foreach (var asset in editor.Session.Snapshot.Document.Assets.Skip(editor.FirstAsset).Take(4))
            {
                int column = compact ? index % 2 : 0, row = compact ? index / 2 : index;
                double cell = compact ? half : width;
                items.Add(Button((editor.SelectedAsset == asset.Id ? "> " : "") + Short(asset.Name, (int)(cell / 8)), "Asset:" + asset.Id,
                    column * (half + 10), 65 + row * 34, cell, 30));
                index++;
            }
            items.Add(Button("Analyze", "Analyze", 0, footer, half, 28));
            items.Add(Button("Rough cut", "RoughCut", half + 10, footer, half, 28));
            if (editor.ProposedTextCut is null)
            {
                items.Add(Button(editor.ProposedCut is null ? "Relink" : "Apply", editor.ProposedCut is null ? "Relink" : "ApplyCut", 0, footer + 36, half, 28));
                items.Add(Button(editor.ProposedCut is null ? "Run script" : "Cancel", editor.ProposedCut is null ? "RunScript" : "CancelCut", half + 10, footer + 36, half, 28));
            }
        }
        if (editor.ProposedTextCut is not null)
        {
            items.Add(Button("Apply", "ApplyTextCut", 0, footer + 36, half, 28));
            items.Add(Button("Cancel", "CancelTextCut", half + 10, footer + 36, half, 28));
        }
        return items;
    }
    private static string Short(string value, int count) => value.Length <= count ? value : value[..(count - 3)] + "...";
    private static UiNode Text(string text, double x, double y, double w, double h, TextSize size = TextSize.Md) => StandardUI.Place(UI.Text(text, color: ColorToken.Hex(0xE4E9F4FF), size: size), x, y, w, h);
    private static UiNode Button(string text, string action, double x, double y, double w, double h) => StandardUI.Button(text, UiAction.Named(action), x, y, w, h);
}
