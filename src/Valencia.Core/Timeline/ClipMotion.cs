namespace Valencia.Core.Timeline;

/// <summary>Clip-local entry motion, expressed relative to the existing video placement.</summary>
public sealed record ClipMotion(string Preset, long DurationTicks, double Strength = 1)
{
    public VideoEffects Apply(VideoEffects effects, long offsetTicks, long clipDurationTicks)
    {
        long duration = Math.Min(DurationTicks, clipDurationTicks);
        if (offsetTicks < 0 || offsetTicks >= duration || Strength == 0) return effects;

        double progress = (double)offsetTicks / duration;
        double remaining = 1 - progress;
        double amplitude = Strength * remaining * remaining;
        return Preset switch
        {
            "punchIn" => effects with { Scale = effects.Scale * (1 + .38 * amplitude) },
            "shake" => effects with
            {
                Scale = effects.Scale * (1 + .11 * amplitude),
                X = effects.X + .033 * amplitude * Math.Sin(progress * Math.PI * 12),
                Y = effects.Y + .021 * amplitude * Math.Sin(progress * Math.PI * 10)
            },
            _ => throw new ArgumentException("Unsupported clip motion preset.", nameof(Preset))
        };
    }
}
