namespace Valencia.Core.Timeline;

/// <summary>A positive rational source rate, or a source position held for a positive number of sequence ticks.</summary>
public sealed record VideoRetime(int Numerator, int Denominator, SourcePosition? Hold = null, long HoldTicks = 0)
{
    internal void Validate(TimelineClip clip)
    {
        if (Hold is { } hold)
        {
            DocumentValidator.Require(Numerator == 0 && Denominator == 1 && HoldTicks > 0
                && hold.Timestamp.Numerator == clip.SourceIn.Timestamp.Numerator
                && hold.Timestamp.Denominator == clip.SourceIn.Timestamp.Denominator
                && hold.CompareTo(clip.SourceIn) >= 0 && hold.CompareTo(clip.SourceOut) < 0,
                "Freeze frame requires a position inside the source range and a positive duration.");
            return;
        }
        DocumentValidator.Require(Numerator is > 0 and <= 1000 && Denominator is > 0 and <= 1000
            && Numerator <= 8 * Denominator && Denominator <= 8 * Numerator && HoldTicks == 0,
            "Video speed must be between 1/8x and 8x with a positive numerator and denominator of at most 1000.");
    }

    internal static long Scale(long ticks, int numerator, int denominator, TimeRounding rounding)
    {
        if (denominator <= 0 || numerator <= 0) throw new ArgumentException("Invalid video speed.");
        Int128 scaled = (Int128)ticks * numerator;
        Int128 quotient = scaled / denominator;
        Int128 remainder = scaled % denominator;
        if (remainder != 0)
            quotient += rounding switch
            {
                TimeRounding.Exact => throw new ArgumentException("This rate or edit boundary does not land on an exact sequence tick."),
                TimeRounding.Floor => remainder < 0 ? -1 : 0,
                TimeRounding.Ceiling => remainder > 0 ? 1 : 0,
                _ => throw new ArgumentOutOfRangeException(nameof(rounding))
            };
        return checked((long)quotient);
    }
}
