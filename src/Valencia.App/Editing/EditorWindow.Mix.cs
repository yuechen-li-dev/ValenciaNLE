using Valencia.Analysis;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Media;

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
        var meter = Playback?.MeterReading ?? AudioMeterReading.Empty;
        double ceiling = Editor.Source is null ? Editor.Sequence.Creator?.PeakCeilingDb ?? 0 : 0;
        Editor.Meter.Observe(meter, ceiling, clock.Elapsed.TotalSeconds);
    }

    private double StripLevel(string key) => key == MasterStrip
        ? Editor.Sequence.Creator?.MasterGainDb ?? 0
        : Editor.Sequence.Creator?.Audio.FirstOrDefault(a => a.TrackId.ToString() == key)?.GainDb ?? 0;

    /// <summary>Handles Mix tab actions; false when the name is not one.</summary>
    private bool MixAction(string name)
    {
        int colon = name.IndexOf(':');
        if (name == "ClearOver") { Editor.Meter.ClearOver(); Editor.Status = "Over lamps cleared."; return true; }
        if (name == "NormalizeMix") { RunJob(NormalizeMix); return true; }
        if (name == "ListenMix")
        {
            Editor.ListenBefore = !Editor.ListenBefore;
            RefreshMonitorMix();
            Editor.Status = Editor.ListenBefore ? "Listening before clip EQ and cleanup." : "Listening with clip EQ and cleanup.";
            return true;
        }
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
            {
                double current = StripLevel(key);
                SetStripLevel(key, Math.Round(current) + (verb.EndsWith('+') ? 1 : -1), Math.Max(MeterScale.FaderTop, current));
            }
                return true;
            default: return false;
        }
    }

    /// <summary>The output the transport is viewing decides the loudness target, the same rule export finishing uses.</summary>
    private (double Lufs, string Destination) MixTarget()
    {
        var sequence = Editor.Sequence;
        var profile = sequence.Creator?.Profiles.FirstOrDefault(p => p.Id == Editor.SelectedProfile);
        return profile is null ? LoudnessTargets.For(sequence.Name, sequence.Video.Width, sequence.Video.Height)
            : LoudnessTargets.For(profile.Name, profile.Width, profile.Height);
    }

    private AudioFinish MonitorLoudnessFinish() => new(MixTarget().Lufs);

    private async Task NormalizeMix()
    {
        var snapshot = Editor.Session.Snapshot;
        var sequence = Editor.Sequence;
        if (sequence.Duration.Ticks == 0) { Editor.Status = "Add audio to the sequence before normalizing."; return; }
        var (target, destination) = MixTarget();
        Editor.Status = $"Measuring the program mix for {destination} ({target:0} LUFS).";
        var token = jobCancellation?.Token ?? default;
        var measured = await Task.Run(() => LoudnessAnalyzer.MeasureForNormalizationAsync(snapshot, sequence.Id, token), token);
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(snapshot, Editor.Session.Snapshot) || Editor.SequenceId != sequence.Id)
        { Editor.Status = "The sequence changed during loudness measurement."; return; }
        measured = measured with { TargetLufs = target };
        if (measured.SafeGainDb is not { } safe || measured.IntegratedLufs is not { } loudness)
        { Editor.Status = "The program mix has no measurable loudness."; return; }
        // The master fader tops out at +12 dB; static gain never goes past what the fader can show and undo by hand.
        double gain = Math.Round(Math.Min(safe, MeterScale.FaderTop), 1), wanted = target - loudness;
        var creator = sequence.Creator ?? new CreatorSettings(1, [], new(), [], [], PeakCeilingDb: 0);
        var updated = creator with { MasterGainDb = gain, PeakCeilingDb = measured.PeakCeilingDb };
        if (updated != creator)
        {
            if (!Editor.Commit(new SetCreatorSettings(sequence.Id, updated)).Success) return;
            RefreshMonitorMix();
        }
        double reached = loudness + gain;
        string limit = gain >= wanted - .05 ? ""
            : safe <= MeterScale.FaderTop ? $"; true peak stops static gain at {measured.PeakCeilingDb:0.#} dBTP"
            : "; the master fader stops at +12 dB";
        Editor.Status = gain >= wanted - .05
            ? FormattableString.Invariant($"Master {gain:+0.0;-0.0;0.0} dB: {reached:0.0} LUFS for {destination} (target {target:0}), ceiling {measured.PeakCeilingDb:0.#} dBTP.")
            : FormattableString.Invariant($"Master {gain:+0.0;-0.0;0.0} dB reaches {reached:0.0} LUFS, short of {target:0} for {destination}{limit}. Export finishing makes up the rest.");
    }

    /// <param name="top">Highest level allowed; relative nudges pass the current level so a strip set past the fader's
    /// travel (older projects, agent plans) steps down from where it is instead of jumping to +12.</param>
    private void SetStripLevel(string key, double db, double top = MeterScale.FaderTop)
    {
        db = Math.Round(Math.Clamp(db, MeterScale.Floor, Math.Min(30, top)), 1);
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
        else Seek(Editor.Playhead, keepPicture: true);
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
