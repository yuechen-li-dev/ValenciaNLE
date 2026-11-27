using System.Collections.Immutable;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Engine;

public sealed record SubjectSample(SequenceTime Time, double X, double Y);

public static class SubjectFraming
{
    /// <summary>Bounded smoothing in sequence time, with stable framing through short gaps and a centre fallback.</summary>
    public static ImmutableArray<FramingPoint> Build(SequenceTime start, SequenceTime end, IReadOnlyList<SubjectSample> samples,
        double fallbackX, double fallbackY)
    {
        const double deadband = .025;
        const double maximumSpeed = .22;
        long gap = 2 * SequenceTime.TicksPerSecond;
        var keys = new List<FramingPoint> { new(start, fallbackX, fallbackY) };
        long lastEvidence = start.Ticks;
        foreach (var sample in samples.OrderBy(s => s.Time.Ticks).DistinctBy(s => s.Time))
        {
            if (sample.Time.Ticks < start.Ticks || sample.Time.Ticks >= end.Ticks) continue;
            if (sample.Time.Ticks - lastEvidence > gap)
            {
                Put(new(lastEvidence + gap), keys[^1].X, keys[^1].Y);
                Put(new(Math.Min(sample.Time.Ticks, lastEvidence + 2 * gap)), fallbackX, fallbackY);
            }
            Put(sample.Time, sample.X, sample.Y);
            lastEvidence = sample.Time.Ticks;
        }
        if (end.Ticks - lastEvidence > gap)
        {
            Put(new(lastEvidence + gap), keys[^1].X, keys[^1].Y);
            Put(end, fallbackX, fallbackY);
        }
        return [.. keys];

        void Put(SequenceTime time, double x, double y)
        {
            var prev = keys[^1];
            if (time.Ticks <= prev.Time.Ticks) return;
            double dx = x - prev.X, dy = y - prev.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            double amount = distance < deadband ? 0 : Math.Min(1, maximumSpeed * (time.Ticks - prev.Time.Ticks) /
                SequenceTime.TicksPerSecond / distance);
            keys.Add(new(time, Math.Clamp(prev.X + dx * amount, 0, 1), Math.Clamp(prev.Y + dy * amount, 0, 1)));
        }
    }
}
