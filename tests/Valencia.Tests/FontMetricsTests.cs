using Aurelian.Machina;
using Machina.Core.Styling;
using Machina.Fonts;
using Machina.Fonts.Generation.Typography;
using Machina.Fonts.ReferenceRendering;
using Machina.Presentation;
using SkiaSharp;
using Valencia.Rendering;
using Xunit;

namespace Valencia.Tests;

public sealed class FontMetricsTests
{
    private static readonly string PathToFont = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", TypeScale.Display);
    private static readonly FontFaceId Face = new("display-metrics-test");
    private static readonly Lazy<AurelianNativeUiFont> Font = new(() => AurelianNativeUiFont.Create(
        [new(TextSize.H1, PathToFont, Face.Value, 48)]));

    [Fact]
    public async Task DisplayGlyphsShareOneBaselineAndMatchIndependentFontMetrics()
    {
        var qualified = Font.Value.Qualify(new("metrics", new(0, 0, 700, 80), "SEQUENCE",
            new TextStyle(Size: TextSize.H1), ColorToken.White));
        var run = qualified.Primitive!.GlyphRun;
        using var face = SKTypeface.FromFile(PathToFont);
        using var skia = new SKFont(face, 48) { Hinting = SKFontHinting.None, LinearMetrics = true, Subpixel = true };
        var source = new TypographyGlyphOutlineSource(new Dictionary<FontFaceId, TypographyFontFaceSource>
        {
            [Face] = new(Face, PathToFont, 0)
        });

        for (int i = 0; i < run.Glyphs.Count; i++)
        {
            var glyph = run.Glyphs[i];
            string character = char.ConvertFromUtf32(glyph.Key.Codepoint);
            float advance = skia.MeasureText(character);
            using var path = skia.GetGlyphPath(skia.GetGlyphs(character)[0]);
            SKRect bounds = path.Bounds;
            double pair = i + 1 < run.Glyphs.Count
                ? (await source.GetPairAdjustmentAsync(glyph.Key, run.Glyphs[i + 1].Key))?.AdvanceX ?? 0 : 0;
            Assert.Equal(run.Lines[0].BaselineY, glyph.BaselineY);
            Assert.InRange(Math.Abs(glyph.Advance - pair - advance), 0, .002);
            Assert.InRange(Math.Abs(glyph.PlaneBounds.Top - bounds.Top), 0, .002);
            Assert.InRange(Math.Abs(glyph.PlaneBounds.Bottom - bounds.Bottom), 0, .002);
        }
        var q = Assert.Single(run.Glyphs, g => g.Key.Codepoint == 'Q');
        var e = run.Glyphs.First(g => g.Key.Codepoint == 'E');
        Assert.True(q.PlaneBounds.Bottom > e.PlaneBounds.Bottom + 1,
            "The font's Q has a real descender; moving its baseline would corrupt the outline.");
    }

    [Theory]
    [InlineData("AVATAR")]
    [InlineData("To WA")]
    [InlineData("SEQUENCE")]
    public async Task NativeAtlasUsesTheSamePairAdvancesAsTheOutlineRenderer(string text)
    {
        var source = new TypographyGlyphOutlineSource(new Dictionary<FontFaceId, TypographyFontFaceSource>
        {
            [Face] = new(Face, PathToFont, 0)
        });
        var outline = await new DirectOutlineStaticTextRenderer(source).RenderAsync(new(
            text, Face, 48, 700, 90, Rgba32.White, new(0, 0, 0, 0), 0, 50));
        Assert.True(outline.Success);
        var qualified = Font.Value.Qualify(new("metrics", new(0, 0, 700, 90), text,
            new TextStyle(Size: TextSize.H1), ColorToken.White));
        var run = qualified.Primitive!.GlyphRun;
        Assert.Equal(outline.Glyphs.Count, run.Glyphs.Count);
        for (int i = 0; i < run.Glyphs.Count; i++)
            Assert.Equal(outline.Glyphs[i].X, run.Glyphs[i].OriginX, 8);
        Assert.Equal(outline.Layout!.Width, run.Lines[0].AdvanceWidth, 8);
    }
}
