namespace Valencia.Core.Timeline;

public sealed record TitleCue(Guid Id, string Text, SequenceTime Start, SequenceTime End,
    string Motion = "static", long EntranceTicks = 0, long ExitTicks = 0,
    double X = .5, double Y = .5, double Size = .11, uint Color = 0xFFFFFFFF, uint Outline = 0x101015FF);

public readonly record struct TitlePose(double Opacity, double Scale, double OffsetX, double OffsetY);

/// <summary>Motion in normalized output space, sampled at exact sequence times.</summary>
public static class TitleMotion
{
    internal static bool IsSupported(string name) => name is "static" or "fade" or "slideUp" or "pop" or "impact";

    public static TitlePose? At(TitleCue cue, SequenceTime time)
    {
        if (time.Ticks < cue.Start.Ticks || time.Ticks >= cue.End.Ticks) return null;
        if (cue.Motion == "static") return new(1, 1, 0, 0);

        double enter = cue.EntranceTicks == 0 ? 1
            : Math.Clamp((double)(time.Ticks - cue.Start.Ticks) / cue.EntranceTicks, 0, 1);
        double exit = cue.ExitTicks == 0 ? 1
            : Math.Clamp((double)(cue.End.Ticks - time.Ticks) / cue.ExitTicks, 0, 1);
        double remaining = 1 - enter;
        double ease = 1 - remaining * remaining * remaining;
        return cue.Motion switch
        {
            "fade" => new(ease * exit, 1, 0, 0),
            "slideUp" => new(ease * exit, 1, 0, .09 * (1 - ease)),
            "pop" => new(Math.Min(1, enter * 4) * exit, 1 + remaining * remaining * (.85 - 1.4 * remaining), 0, 0),
            "impact" => new(exit, 1 + .4 * (1 - ease),
                .025 * remaining * remaining * Math.Sin(enter * Math.PI * 12), 0),
            _ => throw new ArgumentException("Unsupported title motion.", nameof(cue))
        };
    }
}
