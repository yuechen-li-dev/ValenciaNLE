using System.Numerics;

namespace Valencia.Core.Timeline;

public enum TimeRounding { Exact, TowardZero, Floor, Ceiling, NearestAwayFromZero }
public readonly record struct FrameRate(int Numerator, int Denominator);

/// <summary>An original source timestamp plus an exact offset. Splitting never changes the original timebase.</summary>
public readonly record struct SourcePosition(MediaTimestamp Timestamp, SequenceTime Offset = default)
{
    public long SampleIndex(int rate, TimeRounding rounding) => Fraction.Round(Fraction.From(this).Numerator * rate, Fraction.From(this).Denominator, rounding);
    public MediaTimestamp ToTimestamp(TimeRounding rounding) => new(checked(Timestamp.Value + Fraction.Round(
        (BigInteger)Offset.Ticks * Timestamp.Denominator, (BigInteger)SequenceTime.TicksPerSecond * Timestamp.Numerator, rounding)), Timestamp.Numerator, Timestamp.Denominator);
    public SourcePosition Shift(SequenceTime delta) => this with { Offset = new(checked(Offset.Ticks + delta.Ticks)) };
    public int CompareTo(SourcePosition other) => Fraction.From(this).CompareTo(Fraction.From(other));
    public bool EquivalentTo(SourcePosition other) => CompareTo(other) == 0;
    public SequenceTime Since(SourcePosition other, TimeRounding rounding = TimeRounding.Exact) =>
        new((Fraction.From(this) - Fraction.From(other)).Ticks(rounding));
}

public static class TimelineTime
{
    public static MediaTimestamp Rebase(SourcePosition position, int numerator, int denominator, TimeRounding rounding)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(numerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(denominator);
        var fraction = Fraction.From(position);
        return new(Fraction.Round(fraction.Numerator * denominator, fraction.Denominator * numerator, rounding), numerator, denominator);
    }
    public static long SampleIndex(SequenceTime time, int rate, TimeRounding rounding) => Fraction.Round((BigInteger)time.Ticks * rate, SequenceTime.TicksPerSecond, rounding);
    public static long FrameIndex(SequenceTime time, FrameRate rate, TimeRounding rounding) => Fraction.Round((BigInteger)time.Ticks * rate.Numerator, (BigInteger)SequenceTime.TicksPerSecond * rate.Denominator, rounding);
    public static SequenceTime Convert(MediaTimestamp timestamp, TimeRounding rounding = TimeRounding.Exact) =>
        new(Fraction.From(new SourcePosition(timestamp)).Ticks(rounding));

    public static SequenceTime FromFrames(long frames, FrameRate rate, TimeRounding rounding = TimeRounding.Exact)
    {
        RequireRate(rate);
        return new(new Fraction((BigInteger)frames * rate.Denominator, rate.Numerator).Ticks(rounding));
    }

    public static SequenceTime FromSamples(long samples, int rate, TimeRounding rounding = TimeRounding.Exact)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        return new(new Fraction(samples, rate).Ticks(rounding));
    }

    public static SequenceTime SnapToFrame(SequenceTime time, FrameRate rate, TimeRounding rounding)
    {
        RequireRate(rate);
        long frame = Fraction.Round((BigInteger)time.Ticks * rate.Numerator,
            (BigInteger)SequenceTime.TicksPerSecond * rate.Denominator, rounding);
        return FromFrames(frame, rate);
    }

    public static SequenceTime SnapToSample(SequenceTime time, int sampleRate, TimeRounding rounding)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        long sample = Fraction.Round((BigInteger)time.Ticks * sampleRate, SequenceTime.TicksPerSecond, rounding);
        return FromSamples(sample, sampleRate);
    }

    internal static void RequireRate(FrameRate rate)
    {
        if (rate.Numerator <= 0 || rate.Denominator <= 0) throw new ArgumentException("Frame rate must be positive.");
    }
}

// Arbitrary precision is used only for arithmetic intermediates. Authoritative sequence ticks remain checked Int64.
internal readonly record struct Fraction(BigInteger Numerator, BigInteger Denominator) : IComparable<Fraction>
{
    public static Fraction From(SourcePosition value)
    {
        if (value.Timestamp.Numerator <= 0 || value.Timestamp.Denominator <= 0)
            throw new ArgumentException("Source timebase must be positive.");
        return new((BigInteger)value.Timestamp.Value * value.Timestamp.Numerator * SequenceTime.TicksPerSecond
            + (BigInteger)value.Offset.Ticks * value.Timestamp.Denominator,
            (BigInteger)value.Timestamp.Denominator * SequenceTime.TicksPerSecond);
    }
    public static Fraction operator -(Fraction a, Fraction b) =>
        new(a.Numerator * b.Denominator - b.Numerator * a.Denominator, a.Denominator * b.Denominator);
    public int CompareTo(Fraction other) => (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);
    public long Ticks(TimeRounding rounding) => Round(Numerator * SequenceTime.TicksPerSecond, Denominator, rounding);
    public static long Round(BigInteger numerator, BigInteger denominator, TimeRounding rounding)
    {
        if (denominator <= 0) throw new ArgumentOutOfRangeException(nameof(denominator));
        BigInteger value = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
        value += rounding switch
        {
            TimeRounding.Exact when remainder != 0 => throw new ArgumentException("Time is not exactly representable on the requested grid."),
            TimeRounding.Exact or TimeRounding.TowardZero => 0,
            TimeRounding.Floor => remainder.Sign < 0 ? -1 : 0,
            TimeRounding.Ceiling => remainder.Sign > 0 ? 1 : 0,
            TimeRounding.NearestAwayFromZero => BigInteger.Abs(remainder) * 2 >= denominator ? remainder.Sign : 0,
            _ => throw new ArgumentOutOfRangeException(nameof(rounding))
        };
        return checked((long)value);
    }
}
