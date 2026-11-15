using System.Collections.Immutable;

namespace Valencia.Core.Timeline;

public sealed record ActiveClip(Guid SequenceId, Guid TrackId, int TrackOrder, TrackKind Kind,
    TimelineClip Clip, MediaAsset Asset, AssetStream Stream, SourcePosition SourcePosition);

/// <summary>Queries operate on a coherent immutable snapshot. Ordering is track order, then clip position.</summary>
public sealed class TimelineQueries(ProjectSnapshot snapshot)
{
    public ProjectSnapshot Project => snapshot;
    public ImmutableArray<MediaAsset> Assets => snapshot.Document.Assets;
    public ImmutableArray<SequenceDocument> Sequences => snapshot.Document.Sequences;
    public SequenceDocument Sequence(Guid id) => Sequences.First(s => s.Id == id);
    public ImmutableArray<TimelineTrack> Tracks(Guid sequenceId) => Sequence(sequenceId).Tracks;
    public ImmutableArray<TimelineClip> Clips(Guid sequenceId, Guid trackId) => Tracks(sequenceId).First(t => t.Id == trackId).Clips;
    public SequenceTime Duration(Guid sequenceId) => Sequence(sequenceId).Duration;

    public ImmutableArray<TimelineClip> Intersecting(Guid sequenceId, SequenceTime start, SequenceTime end)
    {
        if (start.Ticks < 0 || end.Ticks <= start.Ticks) throw new ArgumentException("Query range must be positive [start, end).");
        return [.. Tracks(sequenceId).SelectMany(t => t.Clips).Where(c => c.Start.Ticks < end.Ticks && c.End.Ticks > start.Ticks)];
    }

    /// <summary>Muted audio, hidden video and disabled clips are omitted; locks affect edits, not evaluation.</summary>
    public ImmutableArray<ActiveClip> Evaluate(Guid sequenceId, SequenceTime time)
    {
        if (time.Ticks < 0) throw new ArgumentOutOfRangeException(nameof(time));
        var assets = Assets.ToDictionary(a => a.Id);
        return [.. Tracks(sequenceId)
            .Where(t => t.Kind == TrackKind.Video ? t.State.Visible : !t.State.Muted)
            .SelectMany(t => t.Clips.Where(c => c.Properties.Enabled && c.Start.Ticks <= time.Ticks && time.Ticks < c.End.Ticks)
                .Select(c => new ActiveClip(sequenceId, t.Id, t.Order, t.Kind, c, assets[c.AssetId],
                    assets[c.AssetId].Streams.First(s => s.Index == c.StreamIndex), c.MapToSource(time))))];
    }
}
