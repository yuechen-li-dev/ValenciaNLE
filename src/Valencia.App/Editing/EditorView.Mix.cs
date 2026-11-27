using System.Globalization;
using Machina.Core.Nodes;
using Machina.Core.Styling;
using Machina.Layout.Geometry;
using Machina.Standard.Authoring;
using Machina.Standard.Theme;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Rendering;
using Aurelian.Machina;

namespace Valencia.App.Editing;

/// <summary>
/// Audio: the Mix tab (a strip per audio track plus master) and the master edge meter docked on the timeline.
/// Meters follow the Edge Code rule that louder is brighter: grey, bone, card, and alert only for an over.
/// Each strip's meter runs inside its fader travel on one engraved scale, so the fader notch and the signal
/// read against the same unity line, like a console's channel legend.
/// </summary>
internal sealed partial class EditorView
{
    private const uint OverZone = 0x3A1418FF, Unlit = 0x1F2329FF, Groove = 0x0E1013FF;

    private static void MixTab(List<UiNode> items, EditorDocument editor, Rect panel, double width, double top, double bottom, bool compact)
    {
        editor.MixFaders.Clear();
        var sequence = editor.Sequence;
        var labels = TrackLabels(sequence);
        var tracks = sequence.Tracks.Where(t => t.Kind == TrackKind.Audio).ToArray();
        var solos = editor.SoloedAudioTracks;
        double rowHeight = compact ? 46 : 56, masterHeight = compact ? 58 : 70;
        double labelWidth = compact ? 34 : 42, side = 20;
        double barLeft = Pad + labelWidth + 6 + side + 2, barRight = Pad + width - side - 2;

        // Engraved legend: the dB scale every strip below shares.
        double scaleHeight = 16;
        items.Add(TextIn("DB", Pad, top, labelWidth, scaleHeight, TextSize.Xs, Ui.Edge));
        // Unity is always engraved; other marks drop out where they would collide with a neighbour.
        double unityX = barLeft + MeterScale.Fader(0) * (barRight - barLeft);
        string Mark(double mark) => mark > 0 ? "+" + mark.ToString(CultureInfo.InvariantCulture) : mark.ToString(CultureInfo.InvariantCulture);
        double unityHalf = NativeUiText.Width("0", TextSize.Xs) / 2 + 5, lastRight = barLeft - 8;
        foreach (double mark in MeterScale.Engraving)
        {
            double mx = barLeft + MeterScale.Fader(mark) * (barRight - barLeft);
            string text = Mark(mark);
            double tw = NativeUiText.Width(text, TextSize.Xs), lx = Math.Clamp(mx - tw / 2, barLeft - 4, barRight - tw);
            items.Add(Box(mx, top + scaleHeight - 2, 1, 3, mark == 0 ? Ui.Bone : Ui.Tick));
            bool clear = mark == 0 || lx >= lastRight + 4 && (lx + tw <= unityX - unityHalf || lx >= unityX + unityHalf);
            if (!clear) continue;
            items.Add(TextIn(text, lx, top, tw + 2, scaleHeight, TextSize.Xs, mark == 0 ? Ui.Bone : Ui.Edge));
            lastRight = lx + tw;
        }
        double y = top + scaleHeight + 4;
        items.Add(Box(0, y - 2, panel.Width, 1, Ui.Rule));

        double masterTop = bottom - masterHeight;
        int capacity = Math.Max(0, (int)((masterTop - 6 - y) / rowHeight));
        if (tracks.Length == 0)
            items.Add(Text(NativeUiText.Ellipsize("No audio tracks. Add one from the toolbar.", width), Pad, y + 4, width, 18, color: Ui.Mute));
        foreach (var track in tracks.Take(capacity))
        {
            var processing = sequence.Creator?.Audio.FirstOrDefault(a => a.TrackId == track.Id);
            double gain = editor.FaderPreview is { } preview && preview.Key == track.Id.ToString() ? preview.Db : processing?.GainDb ?? 0;
            bool targeted = editor.TargetTrack == track.Id, soloed = solos.Contains(track.Id);
            // A strip that solo leaves out of the monitor mix reads as muted.
            bool silent = track.State.Muted || solos.Count > 0 && !soloed;
            items.Add(Button(labels[track.Id], "Target:" + track.Id, Pad, y + 2, labelWidth, rowHeight - 8,
                targeted ? Ui.TrackTarget : Ui.TrackAudio, key: "mix"));

            double cx = Pad + labelWidth + 6, cw = width - labelWidth - 6, lineH = 18;
            double buttons = 2 * (side + 2), valueWidth = NativeUiText.Width("-60.0 DB", TextSize.Sm) + 10;
            string name = (track.Role is { } role ? role.ToUpperInvariant() + " / " : "") + track.Name.ToUpperInvariant();
            if (processing is { Envelope.Length: > 0 } env) name += $" / ENV {env.Envelope.Length}";
            if (track.State.Locked) name += " / LOCKED";
            double nameWidth = cw - buttons - valueWidth - 4;
            items.Add(TextIn(NativeUiText.Ellipsize(name, nameWidth, TextSize.Xs), cx, y + 2, nameWidth, lineH, TextSize.Xs, silent ? Ui.Edge : Ui.Mute));
            var channel = editor.Meter.Track(track.Id);
            uint valueColor = channel?.Over == true ? Ui.Alert : silent ? Ui.Edge : Ui.Bone;
            items.Add(Button(MeterScale.Text(gain) + " dB", "MixUnity:" + track.Id, cx + cw - buttons - valueWidth, y + 2, valueWidth, lineH,
                Ui.Value(valueColor), caps: false));
            items.Add(Button("M", "MixMute:" + track.Id, cx + cw - buttons + 2, y + 2, side, lineH, track.State.Muted ? Ui.Danger : Ui.Plain));
            items.Add(Button("S", "Solo:" + track.Id, cx + cw - side, y + 2, side, lineH, soloed ? Ui.Primary : Ui.Plain));

            double barY = y + lineH + 6, barH = rowHeight - lineH - 16;
            items.Add(Button("-", "MixGain-:" + track.Id, barLeft - side - 2, barY, side, barH));
            items.Add(Button("+", "MixGain+:" + track.Id, barRight + 2, barY, side, barH));
            Strip(items, barLeft, barY, barRight - barLeft, barH, silent ? null : channel, gain, 0);
            editor.MixFaders[track.Id.ToString()] = new Rect(panel.X + barLeft, panel.Y + barY, barRight - barLeft, barH);
            y += rowHeight;
            items.Add(Box(Pad, y - 2, width, 1, Ui.RowRule));
        }
        if (tracks.Length > capacity && capacity >= 0)
            items.Add(Text($"+{tracks.Length - capacity} more audio tracks (enlarge the window)", Pad, y, width, 14, TextSize.Xs, Ui.Mute));

        // Master: stereo meter on the same scale, master fader, the clipper ceiling and the over lamp.
        var creator = sequence.Creator;
        double ceiling = creator?.PeakCeilingDb ?? 0;
        double masterGain = editor.FaderPreview is { Key: "master" } mp ? mp.Db : creator?.MasterGainDb ?? 0;
        double my = masterTop;
        items.Add(Box(0, my - 4, panel.Width, 1, Ui.Rule));
        items.Add(Button("Mst", "MixMaster", Pad, my + 2, labelWidth, masterHeight - 10, Sized(Ui.Primary, TextSize.Xl)));
        double mx0 = Pad + labelWidth + 6, mw = width - labelWidth - 6;
        bool over = editor.Meter.Over;
        double overWidth = NativeUiText.Width("OVER", TextSize.Md) + 14, mValueWidth = NativeUiText.Width("-60.0 DB", TextSize.Sm) + 10;
        string peaks = $"L {MeterScale.Text(editor.Meter.Left.HoldDb)}  R {MeterScale.Text(editor.Meter.Right.HoldDb)}";
        double peaksWidth = mw - overWidth - mValueWidth - 6;
        items.Add(TextIn(NativeUiText.Ellipsize("PEAK " + peaks, peaksWidth, TextSize.Xs), mx0, my + 2, peaksWidth, 18, TextSize.Xs, Ui.Mute));
        items.Add(Button(MeterScale.Text(masterGain) + " dB", "MixUnity:master", mx0 + mw - overWidth - mValueWidth - 2, my + 2, mValueWidth, 18,
            Ui.Value(Ui.Bone), caps: false));
        items.Add(Button("Over", "ClearOver", mx0 + mw - overWidth, my + 2, overWidth, 18, over ? Ui.Danger : Ui.Dim));
        double pairY = my + 26, pairH = masterHeight - 50;
        double half = Math.Max(3, (pairH - 2) / 2);
        items.Add(Button("-", "MixGain-:master", barLeft - side - 2, pairY, side, pairH));
        items.Add(Button("+", "MixGain+:master", barRight + 2, pairY, side, pairH));
        Strip(items, barLeft, pairY, barRight - barLeft, half, editor.Meter.Left, masterGain, ceiling, notch: false);
        Strip(items, barLeft, pairY + half + 2, barRight - barLeft, half, editor.Meter.Right, masterGain, ceiling, notch: false);
        FaderNotch(items, barLeft, pairY, barRight - barLeft, pairH, masterGain);
        editor.MixFaders["master"] = new Rect(panel.X + barLeft, panel.Y + pairY, barRight - barLeft, pairH);
        string footer = $"CEIL {MeterScale.Text(ceiling)} dBFS / RMS {MeterScale.Text(Math.Max(editor.Meter.Left.RmsDb, editor.Meter.Right.RmsDb))}";
        items.Add(TextIn(NativeUiText.Ellipsize(footer, mw, TextSize.Xs), mx0, pairY + pairH + 2, mw, masterHeight - 10 - (pairY + pairH - my), TextSize.Xs, Ui.Edge));
    }

    /// <summary>A meter in a fader groove: segmented RMS fill, dimmer peak, hold tick, unity line and the fader notch.</summary>
    private static void Strip(List<UiNode> items, double x, double y, double w, double h, MeterChannel? channel, double gainDb, double overDb, bool notch = true)
    {
        items.Add(Box(x, y, w, h, Groove));
        const double pitch = 4;
        int count = Math.Max(1, (int)((w - 1) / pitch));
        double step = (w - 1) / count;
        double rms = channel?.RmsDb ?? MeterScale.Floor, peak = channel?.PeakDb ?? MeterScale.Floor;
        for (int i = 0; i < count; i++)
        {
            double centre = (i + .5) / count, db = MeterScale.FaderDb(centre);
            uint color;
            if (channel is not null && centre <= MeterScale.Fader(rms) && rms > MeterScale.Floor) color = MeterScale.Zone(db, overDb);
            else if (channel is not null && centre <= MeterScale.Fader(peak) && peak > MeterScale.Floor) color = (MeterScale.Zone(db, overDb) & 0xFFFFFF00) | 0x80;
            else color = db >= overDb ? OverZone : Unlit;
            items.Add(Box(x + 1 + i * step, y + 1, Math.Max(1, step - 1), h - 2, color));
        }
        double unity = x + MeterScale.Fader(0) * w;
        items.Add(Box(unity, y - 1, 1, h + 2, Ui.Mute));
        if (channel is { HoldDb: > MeterScale.Floor } held)
            items.Add(Box(x + MeterScale.Fader(held.HoldDb) * (w - 2), y, 2, h, held.HoldDb >= overDb ? Ui.Alert : Ui.Card));
        if (channel?.Over == true) items.Add(Box(x + w - 4, y, 4, h, Ui.Alert));
        if (notch) FaderNotch(items, x, y, w, h, gainDb);
    }

    private static void FaderNotch(List<UiNode> items, double x, double y, double w, double h, double gainDb)
    {
        double nx = x + MeterScale.Fader(gainDb) * w;
        items.Add(Box(nx - 3, y - 3, 6, h + 6, Ui.Card));
        items.Add(Box(nx - .5, y - 1, 1, h + 2, Ui.Ink));
    }

    /// <summary>Master edge meter: two segmented columns on the timeline's right edge, over lamps on the ruler row.</summary>
    private static void EdgeMeter(List<UiNode> children, List<CanvasRectangle> quads, EditorDocument editor, EditorLayout layout)
    {
        if (!layout.HasEdgeMeter) return;
        double x = layout.ClipRight + 4, w = layout.Width - Gap - x, column = Math.Floor((w - 2) / 2);
        double top = layout.RulerY, lampH = EditorLayout.RulerHeight - 6;
        double meterTop = layout.TrackTop + 2, meterBottom = layout.TrackBottom - 2, height = meterBottom - meterTop;
        double ceiling = editor.Sequence.Creator?.PeakCeilingDb ?? 0;
        quads.Add(new((float)x, (float)top, (float)w, (float)(meterBottom - top + 2), Ui.Panel));
        var meter = editor.Meter;
        children.Add(Button("", "ClearOver", x, top + 2, w, lampH, Ui.Tab, key: "edge"));
        foreach (var (channel, index) in new[] { (meter.Left, 0), (meter.Right, 1) })
        {
            float cx = (float)(x + index * (column + 2));
            quads.Add(new(cx, (float)top + 2, (float)column, (float)lampH, channel.Over ? Ui.Alert : OverZone));
            if (height < 20) continue;
            quads.Add(new(cx, (float)meterTop, (float)column, (float)height, Groove));
            // Continuous zone fills keep the canvas bounded (a handful of quads per column, never one per segment).
            double overTop = meterBottom - MeterScale.Full(ceiling - .05) * height;
            if (overTop > meterTop) quads.Add(new(cx, (float)meterTop, (float)column, (float)Math.Max(2, overTop - meterTop), OverZone));
            Fill(channel.PeakDb, 0x80);
            Fill(channel.RmsDb, 0xFF);
            void Fill(double level, uint alpha)
            {
                if (level <= MeterScale.Floor) return;
                double[] edges = new[] { MeterScale.Floor, -18, -6, ceiling - .05, 0 }.Order().ToArray();
                for (int z = 0; z + 1 < edges.Length; z++)
                {
                    double lo = edges[z], hi = Math.Min(edges[z + 1], level);
                    if (hi <= lo) break;
                    double y0 = meterBottom - MeterScale.Full(hi) * height, y1 = meterBottom - MeterScale.Full(lo) * height;
                    quads.Add(new(cx, (float)y0, (float)column, (float)Math.Max(1, y1 - y0), (MeterScale.Zone((lo + hi) / 2, ceiling) & 0xFFFFFF00) | alpha));
                }
            }
            if (channel.HoldDb > MeterScale.Floor)
                quads.Add(new(cx, (float)(meterBottom - MeterScale.Full(channel.HoldDb) * height), (float)column, 2, channel.HoldDb >= ceiling - .05 ? Ui.Alert : Ui.Card));
        }
        // Reference ticks at -6 and -18 dBFS, where the zones change.
        foreach (double mark in new[] { -6.0, -18.0 })
            quads.Add(new((float)x - 3, (float)(meterBottom - MeterScale.Full(mark) * height), 2, 1, Ui.Tick));
    }
}
