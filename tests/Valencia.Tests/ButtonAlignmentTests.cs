using Machina.Core.Styling;
using Machina.Presentation;
using Valencia.App.Editing;
using Valencia.Rendering;
using Xunit;

namespace Valencia.Tests;

public sealed class ButtonAlignmentTests
{
    [Fact]
    public void SharedFontCachesSupportConcurrentMeasurementAndFaceWarmup()
    {
        var raster = new RasterText();
        Parallel.For(0, 100, i =>
        {
            var size = i % 2 == 0 ? TextSize.Md : TextSize.Sm;
            var metrics = raster.Measure("PLAY " + i, size);
            Assert.True(metrics.Width > 0);
            var text = new PositionedTextOperation(new("probe.label"), new(0, 0, 180, 32), "SCRUB " + i,
                new(Size: size, AlignX: TextAlignX.Center, AlignY: TextAlignY.Center), ColorToken.Hex(0xFFFFFFFF));
            var quads = new List<RasterGlyphQuad>();
            raster.Layout(text, text.Rect, i % 2 == 0 ? 1 : 1.5f, quads);
            Assert.NotEmpty(quads);
        });
    }
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(768, 1024)]
    public void CommandLabelsCenterWithinButtonRegions(int width, int height)
    {
        var editor = new EditorDocument();
        var prepared = new EditorView().Prepare(editor, new(editor), false, false, width, height);
        var labels = prepared.PresentationFrame.Operations.OfType<PositionedTextOperation>();
        foreach (string label in new[] { "NEW", "SAVE", "SELECT", "SPLIT", "UNDO" })
        {
            var text = Assert.Single(labels, t => t.Text == label || label == "SELECT" && t.Text == "SELECT V");
            Assert.Equal(TextAlignX.Center, text.Style.AlignX);
            Assert.Equal(TextAlignY.Center, text.Style.AlignY);
            var quads = new List<RasterGlyphQuad>();
            RasterText.Instance.Layout(text, text.Rect, 1, quads);
            Assert.NotEmpty(quads);
            double inkCenter = (quads.Min(q => q.Destination.X) + quads.Max(q => q.Destination.X + q.Destination.Width)) / 2;
            Assert.InRange(Math.Abs(inkCenter - (text.Rect.X + text.Rect.Width / 2)), 0, 3);
            // A centered label needs the available button area, not only its intrinsic text width.
            Assert.True(text.Rect.Width > NativeUiText.Width(label, text.Style.Size) + 3, $"{label}: {text.Rect}");
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void HintedGlyphRunCentersUsingDeviceScaleMetrics(float scale)
    {
        foreach (var size in new[] { TextSize.Sm, TextSize.Md, TextSize.Xl })
        foreach (string label in new[] { "OVERWRITE", "V1", "H", "Play", "WWiiii" })
        {
            var text = new PositionedTextOperation(new("test.label"), new(170, 40, 300, 64), label,
                new(Size: size, AlignX: TextAlignX.Center, AlignY: TextAlignY.Center), ColorToken.Hex(0xFFFFFFFF));
            var quads = new List<RasterGlyphQuad>();
            RasterText.Instance.Layout(text, text.Rect, scale, quads);
            double center = (quads.Min(q => q.Destination.X) + quads.Max(q => q.Destination.X + q.Destination.Width)) / 2;
            double middle = (quads.Min(q => q.Destination.Y) + quads.Max(q => q.Destination.Y + q.Destination.Height)) / 2;
            Assert.InRange(Math.Abs(center - 320), 0, .51 / scale);
            Assert.InRange(Math.Abs(middle - 72), 0, .51 / scale);
        }
    }
}
