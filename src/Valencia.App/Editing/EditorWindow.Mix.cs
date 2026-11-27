using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;

namespace Valencia.App.Editing;

/// <summary>Mix tab commands, fader drags and the meter feed. Level changes keep playback running.</summary>
internal sealed partial class EditorWindow
{
    private const string MasterStrip = "master";
    private bool faderDragging;

    /// <summary>Meters are live while anything is still falling, so the UI refreshes faster then.</summary>
    private bool MetersMoving => Playback?.IsPlaying == true || Editor.Meter.Live;

    private void UpdateMeters()
    {
        bool playing = Playback?.IsPlaying == true;
        Editor.Meter.Observe(MonitorLevels, Editor.Sequence.Creator?.PeakCeilingDb ?? 0, ChannelLevels(playing), clock.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Channel meters: the cached waveform's peak and RMS around the playhead, times clip gain, fades and the strip's level.
    /// An estimate (the engine meters only the master output), but it moves with the faders, mutes and solo.
    /// </summary>
    private Dictionary<Guid, (double Peak, double Rms)> ChannelLevels(bool playing)
    {
        var levels = new Dictionary<Guid, (double, double)>();
        if (!playing || Editor.Source is not null) return levels;
        var sequence = Editor.Sequence; var at = Editor.Playhead; var solos = SoloedAudioTracks;
        long window = SequenceTime.TicksPerSecond / 40;
        foreach (var track in sequence.Tracks.Where(t => t.Kind == TrackKind.Audio))
        {
            if (track.State.Muted || solos.Count > 0 && !solos.Contains(track.Id)) { levels[track.Id] = (0, 0); continue; }
            var clip = track.Clips.FirstOrDefault(c => c.Properties.Enabled && c.Start.Ticks <= at.Ticks && at.Ticks < c.End.Ticks);
            if (clip is null || !Editor.Waveforms.TryGetValue((clip.AssetId, clip.StreamIndex), out var waveform)) { levels[track.Id] = (0, 0); continue; }
            long local = at.Ticks - clip.Start.Ticks;
            var bins = waveform.Visible(clip.SourceIn.Shift(new(Math.Max(0, local - window))), clip.SourceIn.Shift(new(local + window)), 1);
            double peak = 0, rms = 0;
            foreach (var bin in bins)
                foreach (var channel in bin.Channels)
                {
                    peak = Math.Max(peak, Math.Max(Math.Abs(channel.Minimum), Math.Abs(channel.Maximum)));
                    rms = Math.Max(rms, channel.Rms);
                }
            var processing = sequence.Creator?.Audio.FirstOrDefault(a => a.TrackId == track.Id);
            double db = (processing?.GainDb ?? 0) + (processing is { Envelope.Length: > 0 } p ? CaptionEvaluation.GainAt(p.Envelope, at) : 0);
            double gain = (double)clip.Properties.Gain * Math.Pow(10, db / 20);
            if (clip.Properties.Audio is { } effects) gain *= CaptionEvaluation.Fade(local, clip.Duration.Ticks, effects.FadeInTicks, effects.FadeOutTicks);
            levels[track.Id] = (peak * gain, rms * gain);
        }
        return levels;
    }

    private double StripLevel(string key) => key == MasterStrip
        ? Editor.Sequence.Creator?.MasterGainDb ?? 0
        : Editor.Sequence.Creator?.Audio.FirstOrDefault(a => a.TrackId.ToString() == key)?.GainDb ?? 0;

    /// <summary>Handles Mix tab actions; false when the name is not one.</summary>
    private bool MixAction(string name)
    {
        int colon = name.IndexOf(':');
        if (name == "ClearOver") { Editor.Meter.ClearOver(); Editor.Status = "Over lamps cleared."; return true; }
        if (name == "EqNotWired") { Editor.Status = "Voice EQ is not wired to the audio engine yet. Cleanup (noise reduction) works today."; return true; }
        if (colon < 0) return false;
        string verb = name[..colon], key = name[(colon + 1)..];
        switch (verb)
        {
            case "Solo":
            {
                var id = Guid.Parse(key);
                bool on = !SoloedAudioTracks.Contains(id);
                if (SetAudioTrackSolo(id, on)) Editor.Status = SoloedAudioTracks.Count == 0 ? "Solo off: the full mix is back." : $"Solo: {SoloedAudioTracks.Count} track(s) in the monitor mix. Export is unaffected.";
                return true;
            }
            case "MixMute":
            {
                var id = Guid.Parse(key);
                var track = Editor.Sequence.Tracks.First(t => t.Id == id);
                if (Editor.Commit(new SetTrackState(Editor.SequenceId, id, track.State with { Muted = !track.State.Muted })).Success) RefreshMonitorMix();
                return true;
            }
            case "MixUnity": SetStripLevel(key, 0); return true;
            case "MixGain+": case "MixGain-":
                SetStripLevel(key, Math.Round(StripLevel(key)) + (verb.EndsWith('+') ? 1 : -1));
                return true;
            default: return false;
        }
    }

    private void SetStripLevel(string key, double db)
    {
        db = Math.Round(Math.Clamp(db, MeterScale.Floor, MeterScale.FaderTop), 1);
        if (Math.Abs(db - StripLevel(key)) < 1e-9) return;
        EditOperation operation;
        if (key == MasterStrip)
        {
            var creator = Editor.Sequence.Creator ?? new CreatorSettings(1, [], new(), [], [], PeakCeilingDb: 0);
            operation = new SetCreatorSettings(Editor.SequenceId, creator with { MasterGainDb = Math.Min(db, 30) });
        }
        else operation = new PatchTrackGain(Editor.SequenceId, Guid.Parse(key), db, null);
        var result = Editor.Commit(operation);
        if (result.Success) RefreshMonitorMix();
        else Editor.Status = result.Message ?? result.Error.ToString();
    }

    /// <summary>Rebuild the monitor mix after a level change without stopping the transport.</summary>
    private void RefreshMonitorMix()
    {
        if (Editor.Source is not null) return;
        if (opening is not null || desired)
        {
            if (resume) Seek(Editor.Playhead, play: true, reverse: reverseRequested, keepPicture: true);
        }
        else if (Playback is { IsPlaying: true } playing) Seek(playing.Position, play: true, reverse: playing.IsReverse, keepPicture: true);
        else Seek(Editor.Playhead);
    }

    private bool BeginFader(double x, double y)
    {
        foreach (var (key, rect) in Editor.MixFaders)
        {
            if (x < rect.X - 4 || x > rect.X + rect.Width + 4 || y < rect.Y - 3 || y > rect.Y + rect.Height + 3) continue;
            faderDragging = true;
            Editor.FaderPreview = (key, FaderDb(rect, x));
            CapturePointer();
            lastUi = -1;
            return true;
        }
        return false;
    }

    private static double FaderDb(Machina.Layout.Geometry.Rect rect, double x)
    {
        double db = MeterScale.FaderDb((x - rect.X) / Math.Max(1, rect.Width));
        // Detent at unity, like a console fader: within 0.75 dB snaps to 0.
        return Math.Abs(db) < .75 ? 0 : Math.Round(db, 1);
    }

    private bool MoveFader(double x)
    {
        if (!faderDragging || Editor.FaderPreview is not { } preview) return false;
        if (Editor.MixFaders.TryGetValue(preview.Key, out var rect)) Editor.FaderPreview = (preview.Key, FaderDb(rect, x));
        lastUi = -1;
        return true;
    }

    private bool ReleaseFader(double x)
    {
        if (!faderDragging) return false;
        MoveFader(x);
        faderDragging = false;
        ReleasePointer();
        if (Editor.FaderPreview is { } preview) { Editor.FaderPreview = null; SetStripLevel(preview.Key, preview.Db); }
        lastUi = -1;
        return true;
    }
}
