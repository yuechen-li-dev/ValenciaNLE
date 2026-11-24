using System.Collections.Immutable;

namespace Valencia.Engine;

public sealed record EffectParameter(string Name, string Unit, double Default, double Minimum, double Maximum);
public sealed record EffectDescriptor(string Id, string Operation, string Target, string Description,
    ImmutableArray<EffectParameter> Parameters, ImmutableArray<string> Modes);

/// <summary>Public descriptions for discovery. Editing validates through the plan schema and document reducer.</summary>
public static class EffectCatalog
{
    public const int Version = 1;
    public static ImmutableArray<EffectDescriptor> Effects { get; } =
    [
        new("basic-color", "color", "videoClip", "Patch selected SDR color values, leaving other clip settings intact.",
        [new("exposure", "stops", 0, -5, 5), new("contrast", "multiplier", 1, 0, 4),
            new("saturation", "multiplier", 1, 0, 4), new("temperature", "warmth", 0, -1, 1)], []),
        new("region", "region", "videoClip", "Set or clear one rectangle in original source coordinates. Cover fills it with black.",
        [new("left", "sourceFraction", 0, 0, 1), new("top", "sourceFraction", 0, 0, 1),
            new("right", "sourceFraction", 1, 0, 1), new("bottom", "sourceFraction", 1, 0, 1), new("size", "sourcePixels", 8, 1, 64)],
        ["blur", "pixelate", "cover"])
    ];
}
