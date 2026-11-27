using Valencia.Media;

namespace Valencia.App.Editing;

/// <summary>
/// Shared dB scales. The mix strip puts signal and fader on one engraved scale (-60 to +12 dB, unity at 85%),
/// so a channel's meter reads "signal at this fader". The edge meter reads dBFS (-60 to 0).
/// </summary>
internal static class MeterScale
{
    public const double Floor = -60, FaderTop = 12;
    public static readonly double[] Engraving = [-40, -20, -10, -6, 0, 6, 12];

    public static double Db(double linear) => linear <= 1e-6 ? Floor : Math.Max(Floor, 20 * Math.Log10(linear));

    /// <summary>Fader travel: compressed below -30 dB, most of the throw between -30 and unity, the top 15% for boost.</summary>
    public static double Fader(double db) => db <= Floor ? 0
        : db < -30 ? (db - Floor) / 30 * .25
        : db < 0 ? .25 + (db + 30) / 30 * .6
        : Math.Min(1, .85 + db / FaderTop * .15);

    public static double FaderDb(double position)
    {
        position = Math.Clamp(position, 0, 1);
        return position < .25 ? Floor + position / .25 * 30
            : position < .85 ? -30 + (position - .25) / .6 * 30
            : (position - .85) / .15 * FaderTop;
    }

    /// <summary>dBFS travel for the edge meter: the bottom 30% holds -60 to -30.</summary>
    public static double Full(double db) => db <= Floor ? 0 : db < -30 ? (db - Floor) / 30 * .3 : Math.Min(1, .3 + (db + 30) / 30 * .7);
    public static double FullDb(double position) => position < .3 ? Floor + position / .3 * 30 : -30 + (position - .3) / .7 * 30;

    /// <summary>Louder is brighter. Alert is reserved for the segment at or past the over point.</summary>
    public static uint Zone(double db, double over) =>
        db >= over ? EditorView.Ui.Alert : db >= -6 ? EditorView.Ui.Card : db >= -18 ? EditorView.Ui.Bone : EditorView.Ui.Mute;

    public static string Text(double db) => db <= Floor ? "-inf" : db.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>One meter channel's ballistics: instant attack, 20 dB/s peak release, 300 ms RMS, 1.5 s peak hold, latched over.</summary>
internal sealed class MeterChannel
{
    public const double ReleaseDbPerSecond = 20, RmsSeconds = .3, HoldSeconds = 1.5;
    private double holdUntil;
    public double PeakDb { get; private set; } = MeterScale.Floor;
    public double RmsDb { get; private set; } = MeterScale.Floor;
    public double HoldDb { get; private set; } = MeterScale.Floor;
    /// <summary>Latched when the signal reached the over point; cleared only by the user.</summary>
    public bool Over { get; set; }
    public bool Live => PeakDb > MeterScale.Floor || HoldDb > MeterScale.Floor;

    /// <summary>Layout smoke: show fixed readings.</summary>
    internal void Set(double peakDb, double rmsDb, double holdDb, bool over) { PeakDb = peakDb; RmsDb = rmsDb; HoldDb = holdDb; Over = over; }

    public void Observe(double peakLinear, double rmsLinear, double overDb, double now, double dt)
    {
        double peak = MeterScale.Db(peakLinear), rms = MeterScale.Db(rmsLinear);
        PeakDb = peak >= PeakDb ? peak : Math.Max(peak, PeakDb - ReleaseDbPerSecond * dt);
        RmsDb += (rms - RmsDb) * (1 - Math.Exp(-dt / RmsSeconds));
        if (RmsDb < MeterScale.Floor + .05) RmsDb = MeterScale.Floor;
        if (PeakDb >= HoldDb) { HoldDb = PeakDb; holdUntil = now + HoldSeconds; }
        else if (now > holdUntil) HoldDb = Math.Max(PeakDb, HoldDb - ReleaseDbPerSecond * dt);
        if (peakLinear > 0 && peak >= overDb - .05) Over = true;
    }
}

/// <summary>
/// Frontend meter state. The engine reports linear peak/RMS for the last device buffer
/// (<see cref="AudioOutputLevels"/>); dB, smoothing, hold and the over latch live here.
/// Channel meters are estimated from the cached waveform peaks at the playhead times the strip's gain,
/// because the engine taps only the master output.
/// </summary>
internal sealed class AudioMeter
{
    private double last = -1;
    public MeterChannel Left { get; } = new();
    public MeterChannel Right { get; } = new();
    public Dictionary<Guid, MeterChannel> Tracks { get; } = [];
    public bool Over => Left.Over || Right.Over || Tracks.Values.Any(t => t.Over);
    public bool Live => Left.Live || Right.Live || Tracks.Values.Any(t => t.Live);

    /// <summary>Layout smoke: hold the current readings instead of following playback.</summary>
    internal bool Pinned { get; set; }

    public void Observe(AudioOutputLevels master, double ceilingDb, IReadOnlyDictionary<Guid, (double Peak, double Rms)> tracks, double now)
    {
        if (Pinned) return;
        double dt = last < 0 ? 0 : Math.Clamp(now - last, 0, .5);
        last = now;
        bool live = master.Frames > 0;
        Left.Observe(live ? master.LeftPeak : 0, live ? master.LeftRms : 0, ceilingDb, now, dt);
        Right.Observe(live ? master.RightPeak : 0, live ? master.RightRms : 0, ceilingDb, now, dt);
        foreach (var id in Tracks.Keys.Except(tracks.Keys).ToArray())
        {
            Tracks[id].Observe(0, 0, 0, now, dt);
            if (!Tracks[id].Live && !Tracks[id].Over) Tracks.Remove(id);
        }
        foreach (var (id, level) in tracks)
        {
            if (!Tracks.TryGetValue(id, out var channel)) Tracks[id] = channel = new();
            channel.Observe(level.Peak, level.Rms, 0, now, dt);
        }
    }

    public MeterChannel? Track(Guid id) => Tracks.GetValueOrDefault(id);

    public void ClearOver()
    {
        Left.Over = Right.Over = false;
        foreach (var track in Tracks.Values) track.Over = false;
    }
}
