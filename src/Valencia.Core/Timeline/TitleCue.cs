using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Valencia.Core.Timeline;

/// <summary>
/// What a title clip draws. Titles are clips on video tracks (as in Premiere): they trim, ripple, split, lock and layer
/// like any clip, and composite at their track's position. <see cref="Style"/>: "card" (Valencia's full-frame title
/// card), "lowerThird" (a slab with an orange tab) or "outline" (outlined text over the picture).
/// Entrance and exit run from the clip's edges; <see cref="Transform"/> keys are relative to the title's own clock,
/// so they stay with the text when the clip's head is trimmed.
/// </summary>
public sealed record TitleContent(string Text, string Style = TitleStyles.Card, string Subtitle = "",
    string Motion = "static", long EntranceTicks = 0, long ExitTicks = 0,
    double X = .5, double Y = .5, double Size = .11, uint Color = 0xFFFFFFFF, uint Outline = 0x101015FF,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TransformCurve? Transform = null);

public static class TitleStyles
{
    public const string Card = "card", LowerThird = "lowerThird", Outline = "outline";
    public static bool IsSupported(string? style) => style is Card or LowerThird or Outline;
}

/// <summary>
/// Read model of a title clip for scripts, plan diffs and old project files (where titles were a sequence layer).
/// Projects store titles only as clips; <see cref="TitleMigration"/> converts the old layer when a project opens.
/// </summary>
public sealed record TitleCue(Guid Id, string Text, SequenceTime Start, SequenceTime End,
    string Motion = "static", long EntranceTicks = 0, long ExitTicks = 0,
    double X = .5, double Y = .5, double Size = .11, uint Color = 0xFFFFFFFF, uint Outline = 0x101015FF,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TransformCurve? Transform = null,
    string Style = TitleStyles.Outline, string Subtitle = "",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? TrackId = null)
{
    public TitleContent Content => new(Text, Style, Subtitle, Motion, EntranceTicks, ExitTicks, X, Y, Size, Color, Outline, Transform);
}

public readonly record struct TitlePose(double Opacity, double Scale, double OffsetX, double OffsetY);

/// <summary>Title clips read from a reserved, unbounded title source: no file, no decoder, handles on both sides.</summary>
public static class TitleClips
{
    public static readonly Guid AssetId = new("7a1e0000-0000-4000-8000-000000000001");
    public const int TimeBase = 90_000;
    public static readonly SourcePosition Origin = new(new MediaTimestamp(0, 1, TimeBase));
    /// <summary>Never stored in a project; the evaluator, validator and queries resolve title clips to it.</summary>
    public static readonly MediaAsset Asset = new(AssetId, "Title", "", "valencia-title",
        [new AssetStream(0, TrackKind.Video, "title", 1, TimeBase)]);

    public static bool IsTitle(this TimelineClip clip) => clip.Title is not null;

    public static TimelineClip Create(Guid id, SequenceTime start, SequenceTime duration, TitleContent content, string name = "") =>
        new(id, AssetId, 0, start, Origin, Origin.Shift(duration), new(name)) { Title = content };

    /// <summary>Time on the title's own clock (what transform keys refer to).</summary>
    public static long LocalTicks(TimelineClip clip, SequenceTime time) => clip.MapToSource(time).Since(Origin).Ticks;

    public static TitleCue View(TimelineClip clip, Guid? trackId = null)
    {
        var c = clip.Title ?? throw new ArgumentException("Not a title clip.", nameof(clip));
        return new(clip.Id, c.Text, clip.Start, clip.End, c.Motion, c.EntranceTicks, c.ExitTicks, c.X, c.Y, c.Size, c.Color, c.Outline,
            c.Transform, c.Style, c.Subtitle, trackId);
    }

    public static ImmutableArray<TitleCue> Views(SequenceDocument sequence) =>
        [.. sequence.Tracks.Where(t => t.Kind == TrackKind.Video).SelectMany(t => t.Clips.Where(IsTitle).Select(c => View(c, t.Id)))
            .OrderBy(cue => cue.Start.Ticks).ThenBy(cue => cue.Id)];
}

/// <summary>Motion in normalized output space, sampled at exact sequence times.</summary>
public static class TitleMotion
{
    internal static bool IsSupported(string name) => name is "static" or "fade" or "slideUp" or "pop" or "impact";

    public static TitlePose? At(TitleCue cue, SequenceTime time) =>
        At(cue.Motion, cue.EntranceTicks, cue.ExitTicks, cue.Start, cue.End, time);

    public static TitlePose? At(TimelineClip clip, SequenceTime time) => clip.Title is { } c
        ? At(c.Motion, c.EntranceTicks, c.ExitTicks, clip.Start, clip.End, time) : null;

    private static TitlePose? At(string motion, long entrance, long exitTicks, SequenceTime start, SequenceTime end, SequenceTime time)
    {
        if (time.Ticks < start.Ticks || time.Ticks >= end.Ticks) return null;
        if (motion == "static") return new(1, 1, 0, 0);

        double enter = entrance == 0 ? 1
            : Math.Clamp((double)(time.Ticks - start.Ticks) / entrance, 0, 1);
        double exit = exitTicks == 0 ? 1
            : Math.Clamp((double)(end.Ticks - time.Ticks) / exitTicks, 0, 1);
        double remaining = 1 - enter;
        double ease = 1 - remaining * remaining * remaining;
        return motion switch
        {
            "fade" => new(ease * exit, 1, 0, 0),
            "slideUp" => new(ease * exit, 1, 0, .09 * (1 - ease)),
            "pop" => new(Math.Min(1, enter * 4) * exit, 1 + remaining * remaining * (.85 - 1.4 * remaining), 0, 0),
            "impact" => new(exit, 1 + .4 * (1 - ease),
                .025 * remaining * remaining * Math.Sin(enter * Math.PI * 12), 0),
            _ => throw new ArgumentException("Unsupported title motion.", nameof(motion))
        };
    }

    internal static void Validate(TitleContent content, long duration)
    {
        DocumentValidator.Require(content.EntranceTicks >= 0 && content.ExitTicks >= 0
            && content.EntranceTicks <= duration && content.ExitTicks <= duration - content.EntranceTicks,
            "Title entrance and exit must fit inside the title clip.");
        DocumentValidator.Require(Line(content.Text, 1) && Line(content.Subtitle, 0),
            "Title text requires one line of 1..120 characters; the subtitle allows 0..120.");
        DocumentValidator.Require(IsSupported(content.Motion) && TitleStyles.IsSupported(content.Style)
            && CreatorSemantics.Unit(content.X) && CreatorSemantics.Unit(content.Y) && content.Size is >= .03 and <= .34,
            "Invalid title style, motion, placement or size.");
        content.Transform?.Validate();
        static bool Line(string? text, int minimum) => text is not null && text.Length >= minimum && text.Length <= 120
            && (minimum == 0 && text.Length == 0 || !string.IsNullOrWhiteSpace(text))
            && !text.Any(char.IsControl) && !text.Contains('\u2028') && !text.Contains('\u2029');
    }
}

/// <summary>Converts the old sequence-level title layer into title clips on new tracks above the existing video.</summary>
public static class TitleMigration
{
    public static ProjectDocument Upgrade(ProjectDocument document)
    {
        if (document.Sequences.All(s => s.LegacyTitles.IsDefaultOrEmpty)) return document;
        return document with { Sequences = [.. document.Sequences.Select(Upgrade)] };
    }

    private static SequenceDocument Upgrade(SequenceDocument sequence)
    {
        if (sequence.LegacyTitles.IsDefaultOrEmpty) return sequence with { LegacyTitles = [] };
        // Greedy lanes: each lane is one new track; overlapping cues stack upward in their old draw order.
        var lanes = new List<List<TitleCue>>();
        foreach (var cue in sequence.LegacyTitles.OrderBy(c => c.Start.Ticks).ThenBy(c => c.Id))
        {
            var lane = lanes.FirstOrDefault(l => l[^1].End.Ticks <= cue.Start.Ticks);
            if (lane is null) lanes.Add(lane = []);
            lane.Add(cue);
        }
        int video = sequence.Tracks.Count(t => t.Kind == TrackKind.Video);
        var tracks = sequence.Tracks.ToList();
        int insertAt = tracks.FindLastIndex(t => t.Kind == TrackKind.Video) + 1;
        for (int i = 0; i < lanes.Count; i++)
        {
            var clips = lanes[i].Select(cue => TitleClips.Create(cue.Id, cue.Start, new(cue.End.Ticks - cue.Start.Ticks),
                cue.Content with { Style = TitleStyles.IsSupported(cue.Style) ? cue.Style : TitleStyles.Outline }));
            tracks.Insert(insertAt + i, new(LaneId(sequence.Id, i), TrackKind.Video, 0, $"V{video + i + 1}", new(), [.. clips]));
        }
        return sequence with { Tracks = [.. tracks.Select((t, order) => t with { Order = order })], LegacyTitles = [] };
    }

    /// <summary>Deterministic, so migrating the same file twice yields the same document.</summary>
    private static Guid LaneId(Guid sequence, int lane)
    {
        var bytes = sequence.ToByteArray();
        bytes[0] ^= 0x7A; bytes[1] ^= 0x1E; bytes[15] ^= (byte)(lane + 1);
        return new Guid(bytes);
    }
}
