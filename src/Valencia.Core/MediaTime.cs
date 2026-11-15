namespace Valencia.Core;

/// <summary>A source clock tick. Value and time base remain rational until conversion is requested.</summary>
public readonly record struct MediaTimestamp(long Value, int Numerator, int Denominator)
{
    public SequenceTime ToSequenceTime()
    {
        if (Numerator <= 0 || Denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Denominator), "A media time base must be positive.");
        }

        Int128 scaled = checked((Int128)Value * Numerator * SequenceTime.TicksPerSecond);
        return new SequenceTime(checked((long)(scaled / Denominator)));
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public double TotalSeconds => (double)Value * Numerator / Denominator;
}

/// <summary>Exact sequence ticks; conversion from arbitrary source clocks rounds toward zero.</summary>
public readonly record struct SequenceTime(long Ticks)
{
    public const long TicksPerSecond = 254_016_000_000;

    [System.Text.Json.Serialization.JsonIgnore]
    public double TotalSeconds => (double)Ticks / TicksPerSecond;

    public static SequenceTime FromSamples(long samples, int sampleRate)
    {
        return new MediaTimestamp(samples, 1, sampleRate).ToSequenceTime();
    }
}
