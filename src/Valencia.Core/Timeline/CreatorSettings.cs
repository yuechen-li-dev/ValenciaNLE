using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Valencia.Core.Timeline;

public sealed record VideoEffects(double Exposure = 0, double Contrast = 1, double Saturation = 1, double Temperature = 0,
    double X = .5, double Y = .5, double Scale = 1, double CropLeft = 0, double CropTop = 0, double CropRight = 0, double CropBottom = 0,
    long FadeInTicks = 0, long FadeOutTicks = 0, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RegionEffect? Region = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClipMotion? Motion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClipTransform? Transform = null);
/// <summary>One source-relative rectangle. Size is the blur radius or pixel block size in source pixels.</summary>
public sealed record RegionEffect(string Mode, double Left, double Top, double Right, double Bottom, int Size = 8);
public sealed record ColorPatch(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Exposure = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Contrast = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Saturation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Temperature = null)
{
    public VideoEffects Apply(VideoEffects video) => video with
    {
        Exposure = Exposure ?? video.Exposure, Contrast = Contrast ?? video.Contrast,
        Saturation = Saturation ?? video.Saturation, Temperature = Temperature ?? video.Temperature
    };
}
public sealed record AudioEffects(long FadeInTicks = 0, long FadeOutTicks = 0, double NoiseReductionDb = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool AlignStartToSample = false);
public sealed record GainPoint(SequenceTime Time, double GainDb);
public sealed record TrackAudio(Guid TrackId, double GainDb, ImmutableArray<GainPoint> Envelope);
public sealed record FramingPoint(SequenceTime Time, double X, double Y, double Scale = 1);
public sealed record CaptionWord(Guid Id, Guid AssetId, int StreamIndex, SourcePosition Start, SourcePosition End, string Text);
public sealed record CaptionStyle(string Font = "Arial", int Size = 40, uint Color = 0xFFFFFFFF, uint Highlight = 0xFFFF00FF, double Y = .84,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? WordsPerLine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Backdrop = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FadeMs = null);
public sealed record OutputProfile(Guid Id, string Name, int Width, int Height, FrameRate FrameRate,
    string Fit = "fit", double FocalX = .5, double FocalY = .5, double CaptionY = .84,
    SequenceTime Start = default, SequenceTime? End = null, ImmutableArray<FramingPoint>? Framing = null);
public sealed record CreatorSettings(int Version, ImmutableArray<CaptionWord> Words, CaptionStyle CaptionStyle,
    ImmutableArray<OutputProfile> Profiles, ImmutableArray<TrackAudio> Audio, double MasterGainDb = 0, double PeakCeilingDb = -1);

public static class CreatorSemantics
{
    public static bool Equal(CreatorSettings? a, CreatorSettings? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a with { Words = b.Words, Profiles = b.Profiles, Audio = b.Audio } == b
            && a.Words.SequenceEqual(b.Words) && a.Profiles.Length == b.Profiles.Length && a.Audio.Length == b.Audio.Length
            && a.Profiles.Zip(b.Profiles).All(p => p.First with { Framing = p.Second.Framing } == p.Second && Points(p.First.Framing).SequenceEqual(Points(p.Second.Framing)))
            && a.Audio.Zip(b.Audio).All(p => p.First with { Envelope = p.Second.Envelope } == p.Second && p.First.Envelope.SequenceEqual(p.Second.Envelope));
    }
    private static IEnumerable<FramingPoint> Points(ImmutableArray<FramingPoint>? value) => value ?? [];

    public static void Validate(CreatorSettings value, SequenceDocument sequence, ImmutableArray<MediaAsset> assets)
    {
        DocumentValidator.Require(value.Version == 1 && !value.Words.IsDefault && !value.Profiles.IsDefault && !value.Audio.IsDefault
            && value.Words.Length <= 100000 && value.Profiles.Length <= 32 && value.Audio.Length <= sequence.Tracks.Length, "Invalid creator data/version/budget.");
        DocumentValidator.Require(double.IsFinite(value.MasterGainDb) && value.MasterGainDb is >= -60 and <= 30
            && double.IsFinite(value.PeakCeilingDb) && value.PeakCeilingDb is >= -12 and <= 0, "Invalid master gain/peak ceiling.");
        DocumentValidator.Require(value.CaptionStyle is not null && value.CaptionStyle.Font is "Arial" or "Consolas"
            && value.CaptionStyle.Size is >= 8 and <= 200 && Unit(value.CaptionStyle.Y)
            && value.CaptionStyle.WordsPerLine is null or >= 1 and <= 12
            && value.CaptionStyle.Backdrop is null or "plate" or "outline" or "shadow"
            && value.CaptionStyle.FadeMs is null or >= 0 and <= 500, "Unsupported caption font/style.");
        var wordIds = new HashSet<Guid>();
        foreach (var word in value.Words)
        {
            var stream = assets.FirstOrDefault(a => a.Id == word.AssetId)?.Streams.FirstOrDefault(s => s.Index == word.StreamIndex);
            DocumentValidator.Require(word.Id != Guid.Empty && wordIds.Add(word.Id) && stream is not null && stream.Kind == TrackKind.Audio
                && word.End.CompareTo(word.Start) > 0 && !string.IsNullOrWhiteSpace(word.Text) && word.Text.Length <= 128, "Invalid caption source word.");
        }
        var profiles = new HashSet<Guid>();
        foreach (var profile in value.Profiles)
        {
            DocumentValidator.Require(profile.Id != Guid.Empty && profiles.Add(profile.Id) && profile.Width is >= 16 and <= 3840 && profile.Height is >= 16 and <= 3840
                && profile.Width % 2 == 0 && profile.Height % 2 == 0 && (long)profile.Width * profile.Height <= 8294400
                && profile.Fit is "fit" or "fill" or "center" && Unit(profile.FocalX) && Unit(profile.FocalY) && Unit(profile.CaptionY)
                && profile.Start.Ticks >= 0 && (profile.End is null || profile.End.Value.Ticks > profile.Start.Ticks), "Invalid output profile.");
            TimelineTime.RequireRate(profile.FrameRate);
            long previous = -1;
            foreach (var point in Points(profile.Framing))
            {
                DocumentValidator.Require(point.Time.Ticks > previous && Unit(point.X) && Unit(point.Y) && double.IsFinite(point.Scale) && point.Scale is >= .1 and <= 10, "Invalid linear framing keys.");
                previous = point.Time.Ticks;
            }
        }
        var tracks = new HashSet<Guid>();
        foreach (var track in value.Audio)
        {
            DocumentValidator.Require(tracks.Add(track.TrackId) && sequence.Tracks.Any(t => t.Id == track.TrackId && t.Kind == TrackKind.Audio)
                && !track.Envelope.IsDefault && track.Envelope.Length <= 20000 && double.IsFinite(track.GainDb) && track.GainDb is >= -120 and <= 30, "Invalid audio track processing.");
            long previous = -1;
            foreach (var point in track.Envelope)
            {
                DocumentValidator.Require(point is not null && point.Time.Ticks > previous && double.IsFinite(point.GainDb)
                    && point.GainDb is >= -120 and <= 30, "Invalid gain envelope.");
                previous = point!.Time.Ticks;
            }
        }
    }
    internal static bool Unit(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
    internal static void ValidateEffects(ClipProperties properties)
    {
        if (properties.Video is { } v)
            DocumentValidator.Require(new[] { v.Exposure, v.Contrast, v.Saturation, v.Temperature, v.Scale }.All(double.IsFinite)
                && v.Exposure is >= -5 and <= 5 && v.Contrast is >= 0 and <= 4 && v.Saturation is >= 0 and <= 4 && v.Temperature is >= -1 and <= 1
                && v.Scale is >= .1 and <= 10 && Unit(v.X) && Unit(v.Y) && Unit(v.CropLeft) && Unit(v.CropTop) && Unit(v.CropRight) && Unit(v.CropBottom)
                && v.CropLeft + v.CropRight < 1 && v.CropTop + v.CropBottom < 1 && v.FadeInTicks >= 0 && v.FadeOutTicks >= 0, "Invalid video effects.");
        if (properties.Video?.Region is { } r)
            DocumentValidator.Require(r.Mode is "blur" or "pixelate" or "cover" && r.Size is >= 1 and <= 64
                && Unit(r.Left) && Unit(r.Top) && Unit(r.Right) && Unit(r.Bottom) && r.Left < r.Right && r.Top < r.Bottom,
                "Invalid region effect. Use blur, pixelate or cover and a nonempty source rectangle in [0, 1].");
        if (properties.Video?.Motion is { } motion)
            DocumentValidator.Require(motion.Preset is "punchIn" or "shake"
                && motion.DurationTicks > 0 && motion.DurationTicks <= 10 * SequenceTime.TicksPerSecond
                && Unit(motion.Strength), "Invalid clip motion. Use punchIn or shake, strength in [0, 1], and a duration up to ten seconds.");
        if (properties.Audio is { } a)
            DocumentValidator.Require(a.FadeInTicks >= 0 && a.FadeOutTicks >= 0 && double.IsFinite(a.NoiseReductionDb) && a.NoiseReductionDb is >= 0 and <= 40, "Invalid audio effects.");
    }
}
