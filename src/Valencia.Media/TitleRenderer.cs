using SkiaSharp;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Media;

/// <summary>
/// Draws one title clip onto the frame at its track's position (alpha over lower tracks, under higher ones).
/// "card" and "lowerThird" are Valencia's title-card look: a heavy condensed display serif in bone, an orange rule or
/// tab, and subtitles in expanded caps. "outline" is outlined text over the picture. Fonts are embedded (OFL), so
/// preview and export draw identical pixels on any machine.
/// </summary>
internal sealed class TitleRenderer : IDisposable
{
    public const uint Ink = 0x0B0C0EFF, Bone = 0xEDE7DAFF, Orange = 0xFF5A1FFF, Mute = 0x8A909BFF;
    private readonly SKTypeface display, caps;
    private readonly SKFont font;
    private readonly SKPaint outline = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint fill = new() { IsAntialias = true };

    public TitleRenderer()
    {
        display = Load("Valencia.Media.TitleDisplay.ttf");
        caps = Load("Valencia.Media.TitleFont.ttf");
        font = new SKFont(caps, 1) { Subpixel = true, Edging = SKFontEdging.Antialias };
    }

    private static SKTypeface Load(string name)
    {
        using var stream = typeof(TitleRenderer).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded title font {name} is missing.");
        using var data = SKData.Create(stream);
        return SKTypeface.FromData(data) ?? throw new InvalidOperationException($"Embedded title font {name} could not be loaded.");
    }

    public unsafe void Draw(byte[] rgba, int width, int height, TimelineClip clip, TitleContent title, SequenceTime time,
        SourcePosition position, float opacity, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        // Inside a dissolve handle the title holds its first or last pose, as a still graphic would.
        var held = new SequenceTime(Math.Clamp(time.Ticks, clip.Start.Ticks, clip.End.Ticks - 1));
        if (TitleMotion.At(clip, held) is not { } pose) return;
        var transform = title.Transform?.At(position.Since(TitleClips.Origin).Ticks) ?? new TransformPose(title.X, title.Y, 1, 0, 1);
        double alpha = opacity * pose.Opacity * transform.Opacity;
        if (alpha <= 0) return;
        fixed (byte* pointer = rgba)
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque), (nint)pointer, width * 4)
                ?? throw new InvalidOperationException("Title raster surface could not be created.");
            var canvas = surface.Canvas;
            switch (title.Style)
            {
                case TitleStyles.Card: Card(canvas, width, height, title, pose, transform, alpha); break;
                case TitleStyles.LowerThird: LowerThird(canvas, width, height, title, pose, transform, alpha); break;
                default: Outlined(canvas, width, height, title, pose, transform, alpha); break;
            }
            canvas.Flush();
        }
    }

    /// <summary>Full-frame card: ink field, the head set large on the left, an orange rule, the subtitle in caps.</summary>
    private void Card(SKCanvas canvas, int width, int height, TitleContent title, TitlePose pose, TransformPose transform, double alpha)
    {
        fill.Color = Tint(Ink, alpha);
        canvas.DrawRect(0, 0, width, height, fill);
        float margin = width * .075f, available = width - 2 * margin;
        float head = Fit(display, title.Text, (float)(title.Size * height * 2.3), available);
        float sub = height * .034f;
        float rule = height * .012f, gap = height * .045f;
        bool hasSub = title.Subtitle.Length > 0;
        // Optical centre: the head's cap height plus rule and subtitle sit just above the frame's middle.
        float block = head * .72f + gap + rule + (hasSub ? gap * .8f + sub : 0);
        float baseline = height * .5f - block / 2 + head * .72f;
        Group(canvas, width, height, title, pose, transform, () =>
        {
            Text(canvas, display, title.Text, head, margin, baseline, Color(title.Color, Bone), alpha);
            fill.Color = Tint(Orange, alpha);
            canvas.DrawRect(margin, baseline + gap, Math.Min(available, width * .085f), rule, fill);
            if (hasSub) Text(canvas, caps, title.Subtitle.ToUpperInvariant(), Fit(caps, title.Subtitle.ToUpperInvariant(), sub, available),
                margin, baseline + gap + rule + gap * .8f + sub * .78f, Mute, alpha);
        });
    }

    /// <summary>Lower third: an ink slab with an orange tab, the subtitle as a caps label above the head.</summary>
    private void LowerThird(SKCanvas canvas, int width, int height, TitleContent title, TitlePose pose, TransformPose transform, double alpha)
    {
        float left = width * .06f, tab = Math.Max(4, width * .009f), padX = height * .03f, padY = height * .022f;
        float available = width * .7f;
        float head = Fit(display, title.Text, (float)(title.Size * height), available);
        string label = title.Subtitle.ToUpperInvariant();
        bool hasSub = label.Length > 0;
        float sub = hasSub ? Fit(caps, label, height * .028f, available) : 0, subGap = hasSub ? height * .016f : 0;
        float textWidth = Math.Max(Measure(display, title.Text, head), hasSub ? Measure(caps, label, sub) : 0);
        float slabHeight = padY + (hasSub ? sub + subGap : 0) + head * .78f + padY * 1.2f;
        // Sits above the caption band (captions default to 84% down), so a name and live captions never collide.
        float bottom = height * .74f, top = bottom - slabHeight;
        Group(canvas, width, height, title, pose, transform, () =>
        {
            fill.Color = Tint(Ink, alpha * .94);
            canvas.DrawRect(left, top, tab + padX * 2 + textWidth, slabHeight, fill);
            fill.Color = Tint(Orange, alpha);
            canvas.DrawRect(left, top, tab, slabHeight, fill);
            float x = left + tab + padX, y = top + padY;
            if (hasSub) { Text(canvas, caps, label, sub, x, y + sub * .78f, Orange, alpha); y += sub + subGap; }
            Text(canvas, display, title.Text, head, x, y + head * .78f, Color(title.Color, Bone), alpha);
        });
    }

    /// <summary>Outlined text anchored at the title's position (the original script title look).</summary>
    private void Outlined(SKCanvas canvas, int width, int height, TitleContent title, TitlePose pose, TransformPose transform, double alpha)
    {
        font.Typeface = caps;
        font.Size = (float)(title.Size * height);
        font.MeasureText(title.Text, out SKRect bounds);
        float stroke = Math.Max(1, font.Size * .12f);
        float peakScale = title.Motion switch { "impact" => 1.4f, "pop" => 1.05f, _ => 1f };
        float fit = Math.Min(1, width * .9f / ((bounds.Width + stroke) * peakScale));
        font.Size *= fit * (float)(pose.Scale * transform.Scale);
        font.MeasureText(title.Text, out bounds);
        outline.StrokeWidth = Math.Max(1, font.Size * .12f);
        outline.Color = Tint(title.Outline, alpha);
        fill.Color = Tint(title.Color, alpha);
        float x = (float)((transform.X + pose.OffsetX) * width);
        float y = (float)((transform.Y + pose.OffsetY) * height);
        float left = x - bounds.MidX;
        float baseline = y - bounds.MidY;
        canvas.Save();
        if (transform.Rotation % 360 != 0) canvas.RotateDegrees((float)(transform.Rotation % 360), x, y);
        canvas.DrawText(title.Text, left, baseline, font, outline);
        canvas.DrawText(title.Text, left, baseline, font, fill);
        canvas.Restore();
    }

    /// <summary>Card and lower-third layouts are fixed; motion and keyframes move, scale and rotate them as one group.</summary>
    private static void Group(SKCanvas canvas, int width, int height, TitleContent title, TitlePose pose, TransformPose transform, Action draw)
    {
        canvas.Save();
        float cx = width / 2f, cy = height / 2f;
        canvas.Translate((float)((transform.X - title.X + pose.OffsetX) * width), (float)((transform.Y - title.Y + pose.OffsetY) * height));
        float scale = (float)(pose.Scale * transform.Scale);
        if (scale != 1) canvas.Scale(scale, scale, cx, cy);
        if (transform.Rotation % 360 != 0) canvas.RotateDegrees((float)(transform.Rotation % 360), cx, cy);
        draw();
        canvas.Restore();
    }

    private void Text(SKCanvas canvas, SKTypeface face, string text, float size, float x, float baseline, uint color, double alpha)
    {
        font.Typeface = face; font.Size = size;
        fill.Color = Tint(color, alpha);
        font.MeasureText(text, out SKRect bounds);
        // Align the ink, not the advance origin, to the layout edge so heavy serifs sit flush with the rule.
        canvas.DrawText(text, x - bounds.Left, baseline, font, fill);
    }

    private float Measure(SKTypeface face, string text, float size)
    {
        font.Typeface = face; font.Size = size;
        font.MeasureText(text, out SKRect bounds);
        return bounds.Width;
    }

    /// <summary>The requested size, or smaller so the text fits the available width.</summary>
    private float Fit(SKTypeface face, string text, float size, float available)
    {
        float measured = Measure(face, text, size);
        return measured <= available || measured <= 0 ? size : size * available / measured;
    }

    private static uint Color(uint requested, uint fallback) => requested == 0xFFFFFFFF ? fallback : requested;

    private static SKColor Tint(uint rgba, double opacity) =>
        new((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)Math.Round((byte)rgba * Math.Clamp(opacity, 0, 1)));

    public void Dispose()
    {
        fill.Dispose();
        outline.Dispose();
        font.Dispose();
        display.Dispose();
        caps.Dispose();
    }
}
