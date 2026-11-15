using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Media;
using Xunit;

namespace Valencia.Tests;

public sealed class AssetMetadataTests(MediaFixture fixture) : IClassFixture<MediaFixture>
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void ImportRetainsOriginalStreamClocksWithoutDependingOnPlaybackDoubles(bool offset)
    {
        var asset = MediaAssetProbe.Read(Guid.NewGuid(), offset ? fixture.Offset : fixture.Mp4);
        var session = new EditingSession(ProjectDocument.Create(Guid.NewGuid(), "Import"));
        var result = session.Execute(new EditCommand(Guid.NewGuid(), 1, new AddAsset(asset), 0));
        Assert.True(result.Success, result.Message);
        Assert.Equal(2, asset.Streams.Length);
        var video = Assert.Single(asset.Streams, s => s.Kind == TrackKind.Video);
        Assert.Equal(new FrameRate(30000, 1001), video.FrameRate);
        Assert.Equal(160, video.Width);
        Assert.True(video.TimeBaseNumerator > 0 && video.TimeBaseDenominator > 0);
        Assert.NotNull(video.Start);
        Assert.Equal(video.TimeBaseDenominator, video.Start.Value.Denominator);
        if (offset) Assert.True(video.Start.Value.TotalSeconds > 11);
        else Assert.NotNull(video.End);
    }
}
