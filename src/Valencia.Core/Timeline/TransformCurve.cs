using System.Collections.Immutable;

namespace Valencia.Core.Timeline;

public sealed record TransformKey(long At, double X = .5, double Y = .5, double Scale = 1,
    double Rotation = 0, double Opacity = 1, string Interpolation = "linear");

public readonly record struct TransformPose(double X, double Y, double Scale, double Rotation, double Opacity);

public sealed record TransformCurve(ImmutableArray<TransformKey> Keys)
{
    public bool Equals(TransformCurve? other) => other is not null && Keys.AsSpan().SequenceEqual(other.Keys.AsSpan());

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var key in Keys.AsSpan()) hash.Add(key);
        return hash.ToHashCode();
    }

    public TransformPose At(long ticks)
    {
        if (ticks <= Keys[0].At) return Pose(Keys[0]);
        if (ticks >= Keys[^1].At) return Pose(Keys[^1]);
        int low = 0, high = Keys.Length - 1;
        while (high - low > 1)
        {
            int middle = (low + high) / 2;
            if (Keys[middle].At <= ticks) low = middle;
            else high = middle;
        }
        var left = Keys[low];
        var right = Keys[high];
        if (left.Interpolation == "hold" || left.At == ticks) return Pose(left);
        double amount = (double)(ticks - left.At) / (right.At - left.At);
        if (left.Interpolation == "ease") amount = amount * amount * (3 - 2 * amount);
        return new(Mix(left.X, right.X), Mix(left.Y, right.Y), Mix(left.Scale, right.Scale),
            Mix(left.Rotation, right.Rotation), Mix(left.Opacity, right.Opacity));

        double Mix(double first, double second) => first + (second - first) * amount;
        static TransformPose Pose(TransformKey key) => new(key.X, key.Y, key.Scale, key.Rotation, key.Opacity);
    }

    internal void Validate(long maximum = long.MaxValue)
    {
        DocumentValidator.Require(!Keys.IsDefaultOrEmpty && Keys.Length <= 128, "A transform requires 1..128 keyframes.");
        long previous = -1;
        foreach (var key in Keys)
        {
            DocumentValidator.Require(key is not null, "Null transform key.");
            DocumentValidator.Require(key!.At > previous && key.At <= maximum && key.X is >= -1 and <= 2
                && key.Y is >= -1 and <= 2 && key.Scale is >= .1 and <= 10 && key.Rotation is >= -3600 and <= 3600
                && CreatorSemantics.Unit(key.Opacity) && key.Interpolation is "hold" or "linear" or "ease",
                "Transform keys must be ordered, within the allowed duration, and have valid coordinates, scale, rotation, opacity and interpolation.");
            previous = key.At;
        }
    }
}

/// <summary>Keys follow the original source interval through clip moves, trims and splits.</summary>
public sealed record ClipTransform(SourcePosition Origin, TransformCurve Curve);
