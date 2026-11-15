using Valencia.App;
using Xunit;

namespace Valencia.Tests;

public sealed class PlaybackQualificationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(.033)]
    [InlineData(.15)]
    public void EstablishedTimingBoundaryPasses(double skew) => PlaybackQualification.Require(skew, 0);

    [Theory]
    [InlineData(.150001)]
    [InlineData(.435)]
    [InlineData(2.833)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void HistoricalFailuresAndInvalidMeasurementsCannotPass(double skew) =>
        Assert.Throws<InvalidOperationException>(() => PlaybackQualification.Require(skew, 0));

    [Fact]
    public void GoodTimingDoesNotOverrideVulkanErrors() =>
        Assert.Throws<InvalidOperationException>(() => PlaybackQualification.Require(.01, 1));
}
