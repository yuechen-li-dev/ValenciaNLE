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
        ["blur", "pixelate", "cover"]),
        new("animated-title", "title", "sequence", "Layer one line of outlined text above video and captions for an exact sequence range.",
        [new("x", "outputFraction", .5, 0, 1), new("y", "outputFraction", .5, 0, 1),
            new("size", "outputHeightFraction", .11, .03, .34)],
        ["static", "fade", "slideUp", "pop", "impact"]),
        new("clip-motion", "clipMotion", "videoClip", "Apply clip-local entry motion or clear it while preserving the underlying placement and grade.",
        [new("strength", "ratio", 1, 0, 1)], ["punchIn", "shake"]),
        new("transform-keyframes", "transform", "videoClipOrTitle", "Animate position, scale, clockwise rotation and opacity with exact relative key times.",
        [new("x", "outputFraction", .5, -1, 2), new("y", "outputFraction", .5, -1, 2),
            new("scale", "multiplier", 1, .1, 10), new("rotation", "degrees", 0, -3600, 3600), new("opacity", "ratio", 1, 0, 1)],
        ["hold", "linear", "ease"])
    ];
}
