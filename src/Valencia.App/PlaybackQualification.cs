namespace Valencia.App;

/// <summary>The established hardware smoke contract, shared by the native acceptance drivers.</summary>
internal static class PlaybackQualification
{
    public const double MaximumSkewSeconds = .15;

    public static bool TimingPasses(double maximumSkew) => double.IsFinite(maximumSkew)
        && maximumSkew >= 0 && maximumSkew <= MaximumSkewSeconds;

    public static void Require(double maximumSkew, int validationErrors)
    {
        if (!TimingPasses(maximumSkew))
            throw new InvalidOperationException($"Native video/audio skew exceeded the established 150 ms contract: {maximumSkew:0.000}s.");
        if (validationErrors != 0)
            throw new InvalidOperationException($"Native Vulkan validation reported {validationErrors} errors; inspect the frame trace.");
    }
}
