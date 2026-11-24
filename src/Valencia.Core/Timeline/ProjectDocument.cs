using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Valencia.Core.Timeline;

public enum TrackKind { Video, Audio }

public sealed record AssetStream(int Index, TrackKind Kind, string Codec,
    int TimeBaseNumerator, int TimeBaseDenominator,
    MediaTimestamp? Start = null, MediaTimestamp? End = null,
    int? Width = null, int? Height = null, int? SampleRate = null, int? Channels = null, FrameRate? FrameRate = null);

/// <summary>SourcePath is absolute in memory; persistence relocates it relative to the project file where possible.</summary>
public sealed record MediaAsset(Guid Id, string Name, string SourcePath, string Container, ImmutableArray<AssetStream> Streams, string? ProxyKey = null);
public sealed record VideoFormat(int Width, int Height, FrameRate FrameRate);
public sealed record TrackState(bool Locked = false, bool Muted = false, bool Visible = true);
public sealed record ClipProperties(string Name = "", bool Enabled = true, decimal Opacity = 1m, decimal Gain = 1m,
    VideoEffects? Video = null, AudioEffects? Audio = null);

/// <summary>A 1x-speed half-open interval. No nominal-FPS normalization or implicit source rounding.</summary>
public sealed record TimelineClip(Guid Id, Guid AssetId, int StreamIndex, SequenceTime Start,
    SourcePosition SourceIn, SourcePosition SourceOut, ClipProperties Properties)
{
    [JsonIgnore] public SequenceTime Duration => SourceOut.Since(SourceIn);
    [JsonIgnore] public SequenceTime End => new(checked(Start.Ticks + Duration.Ticks));

    public SourcePosition MapToSource(SequenceTime timelineTime)
    {
        if (timelineTime.Ticks < Start.Ticks || timelineTime.Ticks >= End.Ticks)
            throw new ArgumentOutOfRangeException(nameof(timelineTime), "Time must be inside [start, end).");
        return SourceIn.Shift(new(checked(timelineTime.Ticks - Start.Ticks)));
    }

    public SequenceTime MapToTimeline(SourcePosition source)
    {
        if (source.CompareTo(SourceIn) < 0 || source.CompareTo(SourceOut) >= 0)
            throw new ArgumentOutOfRangeException(nameof(source), "Source must be inside [in, out).");
        return new(checked(Start.Ticks + source.Since(SourceIn).Ticks));
    }
}

public sealed record TimelineTrack(Guid Id, TrackKind Kind, int Order, string Name, TrackState State,
    ImmutableArray<TimelineClip> Clips);
public sealed record SequenceMarker(Guid Id, SequenceTime At, string Label);

public sealed record SequenceDocument(Guid Id, string Name, VideoFormat Video, int AudioSampleRate,
    ImmutableArray<TimelineTrack> Tracks, CreatorSettings? Creator = null)
{
    public ImmutableArray<SequenceMarker> Markers { get; init; } = [];
    public ImmutableArray<TitleCue> Titles { get; init; } = [];
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
            p.First with { Tracks = p.Second.Tracks, Creator = p.Second.Creator, Markers = p.Second.Markers, Titles = p.Second.Titles } == p.Second &&
            p.First.Titles.SequenceEqual(p.Second.Titles) &&
            p.First.Markers.SequenceEqual(p.Second.Markers) && CreatorSemantics.Equal(p.First.Creator, p.Second.Creator) && p.First.Tracks.Length == p.Second.Tracks.Length &&
            p.First.Tracks.Zip(p.Second.Tracks).All(t => t.First with { Clips = t.Second.Clips } == t.Second &&
                t.First.Clips.SequenceEqual(t.Second.Clips)));
}
