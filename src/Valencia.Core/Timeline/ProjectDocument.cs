using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Valencia.Core.Timeline;

public enum TrackKind { Video, Audio }

public sealed record AssetStream(int Index, TrackKind Kind, string Codec,
    int TimeBaseNumerator, int TimeBaseDenominator,
    MediaTimestamp? Start = null, MediaTimestamp? End = null,
    int? Width = null, int? Height = null, int? SampleRate = null, int? Channels = null, FrameRate? FrameRate = null);

/// <summary>SourcePath is absolute in memory; persistence relocates it relative to the project file where possible.</summary>
public sealed record MediaAsset(Guid Id, string Name, string SourcePath, string Container, ImmutableArray<AssetStream> Streams, string? ProxyKey = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Role { get; init; }
}
public sealed record VideoFormat(int Width, int Height, FrameRate FrameRate);
public sealed record TrackState(bool Locked = false, bool Muted = false, bool Visible = true);
public sealed record ClipProperties(string Name = "", bool Enabled = true, decimal Opacity = 1m, decimal Gain = 1m,
    VideoEffects? Video = null, AudioEffects? Audio = null);

/// <summary>A half-open interval on the sequence, backed by a rational source clock.</summary>
public sealed record TimelineClip(Guid Id, Guid AssetId, int StreamIndex, SequenceTime Start,
    SourcePosition SourceIn, SourcePosition SourceOut, ClipProperties Properties)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VideoRetime? Retime { get; init; }
    /// <summary>Set on title clips, which read from <see cref="TitleClips.Asset"/> instead of a media file.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TitleContent? Title { get; init; }
    [JsonIgnore] public SequenceTime Duration => Retime switch
    {
        { Hold: not null } hold => new(hold.HoldTicks),
        { } rate => new(VideoRetime.Scale(SourceOut.Since(SourceIn).Ticks, rate.Denominator, rate.Numerator, TimeRounding.Exact)),
        _ => SourceOut.Since(SourceIn)
    };
    [JsonIgnore] public SequenceTime End => new(checked(Start.Ticks + Duration.Ticks));

    public SourcePosition MapToSource(SequenceTime timelineTime)
    {
        if (timelineTime.Ticks < Start.Ticks || timelineTime.Ticks >= End.Ticks)
            throw new ArgumentOutOfRangeException(nameof(timelineTime), "Time must be inside [start, end).");
        return Retime?.Hold ?? SourceIn.Shift(SourceDelta(new(timelineTime.Ticks - Start.Ticks), TimeRounding.Floor));
    }

    public SequenceTime MapToTimeline(SourcePosition source)
    {
        if (source.CompareTo(SourceIn) < 0 || source.CompareTo(SourceOut) >= 0)
            throw new ArgumentOutOfRangeException(nameof(source), "Source must be inside [in, out).");
        return MapSourceBoundary(source);
    }

    /// <summary>Map a source endpoint to the sequence, including the exclusive out-point.</summary>
    public SequenceTime MapSourceBoundary(SourcePosition source)
    {
        if (source.CompareTo(SourceIn) < 0 || source.CompareTo(SourceOut) > 0)
            throw new ArgumentOutOfRangeException(nameof(source), "Source is outside the clip source boundaries.");
        long offset = source.Since(SourceIn).Ticks;
        if (Retime is { Hold: not null }) throw new InvalidOperationException("A freeze frame has no unique inverse source mapping.");
        if (Retime is { } rate) offset = VideoRetime.Scale(offset, rate.Denominator, rate.Numerator, TimeRounding.Floor);
        return new(checked(Start.Ticks + offset));
    }

    /// <summary>Edits must land exactly on the retained source grid. Evaluation rounds down between source ticks.</summary>
    public SequenceTime SourceDelta(SequenceTime delta, TimeRounding rounding = TimeRounding.Exact) =>
        Retime is { } rate ? new(VideoRetime.Scale(delta.Ticks, rate.Numerator, rate.Denominator, rounding)) : delta;

    internal TimelineClip Slice(SequenceTime start, SequenceTime end)
    {
        if (Retime is { Hold: not null } hold) return this with { Start = start, Retime = hold with { HoldTicks = end.Ticks - start.Ticks } };
        return this with
        {
            Start = start,
            SourceIn = start == Start ? SourceIn : SourceIn.Shift(SourceDelta(new(start.Ticks - Start.Ticks))),
            SourceOut = end == End ? SourceOut : SourceIn.Shift(SourceDelta(new(end.Ticks - Start.Ticks)))
        };
    }
}

public sealed record TimelineTrack(Guid Id, TrackKind Kind, int Order, string Name, TrackState State,
    ImmutableArray<TimelineClip> Clips)
{
    public ImmutableArray<TimelineTransition> Transitions { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Role { get; init; }
}
public sealed record SequenceMarker(Guid Id, SequenceTime At, string Label);

public sealed record SequenceDocument(Guid Id, string Name, VideoFormat Video, int AudioSampleRate,
    ImmutableArray<TimelineTrack> Tracks, CreatorSettings? Creator = null)
{
    public ImmutableArray<SequenceMarker> Markers { get; init; } = [];
    /// <summary>The old title layer, read from older project files only; <see cref="TitleMigration"/> turns it into clips.</summary>
    [JsonPropertyName("Titles")] public ImmutableArray<TitleCue> LegacyTitles { get; init; } = [];
    /// <summary>Title clips on this sequence's video tracks, as cues (read model for scripts and plan diffs).</summary>
    [JsonIgnore] public ImmutableArray<TitleCue> Titles => TitleClips.Views(this);
    [JsonIgnore] public SequenceTime Duration => new(Tracks.SelectMany(t => t.Clips).Select(c => c.End.Ticks).DefaultIfEmpty(0).Max());
}
public sealed record ProjectDocument(Guid Id, int SchemaVersion, string Name,
    ImmutableArray<MediaAsset> Assets, ImmutableArray<SequenceDocument> Sequences)
{
    public const int CurrentSchemaVersion = 1;
    public static ProjectDocument Create(Guid id, string name) => new(id, CurrentSchemaVersion, name, [], []);
}

/// <summary>Revision is a monotonic session commit number, separate from undoable semantic content.</summary>
public sealed record ProjectSnapshot(ProjectDocument Document, long Revision);

public static class DocumentSemantics
{
    public static bool Equal(ProjectDocument a, ProjectDocument b) =>
        a.Id == b.Id && a.SchemaVersion == b.SchemaVersion && a.Name == b.Name &&
        a.Assets.Length == b.Assets.Length && a.Assets.Zip(b.Assets).All(p =>
            p.First with { Streams = p.Second.Streams } == p.Second && p.First.Streams.SequenceEqual(p.Second.Streams)) &&
        a.Sequences.Length == b.Sequences.Length && a.Sequences.Zip(b.Sequences).All(p =>
            p.First with { Tracks = p.Second.Tracks, Creator = p.Second.Creator, Markers = p.Second.Markers, LegacyTitles = p.Second.LegacyTitles } == p.Second &&
            p.First.LegacyTitles.SequenceEqual(p.Second.LegacyTitles) &&
            p.First.Markers.SequenceEqual(p.Second.Markers) && CreatorSemantics.Equal(p.First.Creator, p.Second.Creator) && p.First.Tracks.Length == p.Second.Tracks.Length &&
            p.First.Tracks.Zip(p.Second.Tracks).All(t => t.First with { Clips = t.Second.Clips, Transitions = t.Second.Transitions } == t.Second &&
                t.First.Clips.SequenceEqual(t.Second.Clips) && t.First.Transitions.SequenceEqual(t.Second.Transitions)));
}
