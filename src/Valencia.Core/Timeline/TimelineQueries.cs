using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Valencia.Core.Timeline;

public sealed record ActiveClip(Guid SequenceId, Guid TrackId, int TrackOrder, TrackKind Kind,
    TimelineClip Clip, MediaAsset Asset, AssetStream Stream, SourcePosition SourcePosition,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActiveTransition? Transition = null);

public sealed record ActiveTransition(Guid Id, SequenceTime Start, SequenceTime End, bool Incoming)
{
    public double Progress(SequenceTime time)
    {
        if (time.Ticks <= Start.Ticks) return 0;
        if (time.Ticks >= End.Ticks) return 1;
        return (double)(time.Ticks - Start.Ticks) / (End.Ticks - Start.Ticks);
    }

    public double EqualPower(SequenceTime time)
    {
        double angle = Progress(time) * (Math.PI / 2);
        return Incoming ? Math.Sin(angle) : Math.Cos(angle);
    }
}

/// <summary>Queries operate on a coherent immutable snapshot. Ordering is track order, then clip position.</summary>
public sealed class TimelineQueries(ProjectSnapshot snapshot)
{
    private readonly Lazy<IReadOnlyDictionary<Guid, ImmutableArray<TransitionWindow>>> transitions = new(() =>
    {
        var assets = snapshot.Document.Assets.ToDictionary(asset => asset.Id);
        return snapshot.Document.Sequences.SelectMany(sequence => sequence.Tracks).Where(track => !track.Transitions.IsEmpty)
            .ToDictionary(track => track.Id, track => TransitionRules.Windows(track, assets));
    });
    public ProjectSnapshot Project => snapshot;
    public ImmutableArray<MediaAsset> Assets => snapshot.Document.Assets;
    public ImmutableArray<SequenceDocument> Sequences => snapshot.Document.Sequences;
    public SequenceDocument Sequence(Guid id) => Sequences.First(s => s.Id == id);
    public ImmutableArray<TimelineTrack> Tracks(Guid sequenceId) => Sequence(sequenceId).Tracks;
    public ImmutableArray<TimelineClip> Clips(Guid sequenceId, Guid trackId) => Tracks(sequenceId).First(t => t.Id == trackId).Clips;
    public SequenceTime Duration(Guid sequenceId) => Sequence(sequenceId).Duration;
    public IEnumerable<SequenceTime> TransitionBoundaries(Guid sequenceId) =>
        Tracks(sequenceId).SelectMany(track => Windows(track))
            .SelectMany(window => new[] { window.Start, window.End });
    public IEnumerable<TransitionWindow> VisibleVideoTransitions(Guid sequenceId) => Tracks(sequenceId)
        .Where(track => track.Kind == TrackKind.Video && track.State.Visible).SelectMany(track => Windows(track))
        .Where(window => window.Outgoing.Properties.Enabled && window.Incoming.Properties.Enabled);

    public ImmutableArray<TransitionWindow> TrackTransitions(Guid sequenceId, Guid trackId) =>
        Windows(Tracks(sequenceId).First(track => track.Id == trackId));

    private ImmutableArray<TransitionWindow> Windows(TimelineTrack track) =>
        transitions.Value.TryGetValue(track.Id, out var windows) ? windows : [];

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
        var active = ImmutableArray.CreateBuilder<ActiveClip>();
        foreach (var track in Tracks(sequenceId))
        {
            if (track.Kind == TrackKind.Video ? !track.State.Visible : track.State.Muted) continue;
            if (!track.Transitions.IsEmpty && FindWindow(transitions.Value[track.Id], time) is { } window
                && window.Outgoing.Properties.Enabled && window.Incoming.Properties.Enabled)
            {
                var offset = new SequenceTime(time.Ticks - window.Cut.Ticks);
                Add(track, window.Outgoing, window.Outgoing.SourceOut.Shift(offset),
                    new(window.Transition.Id, window.Start, window.End, false));
                Add(track, window.Incoming, window.Incoming.SourceIn.Shift(offset),
                    new(window.Transition.Id, window.Start, window.End, true));
                continue;
            }
            foreach (var clip in track.Clips)
                if (clip.Properties.Enabled && clip.Start.Ticks <= time.Ticks && time.Ticks < clip.End.Ticks)
                    Add(track, clip, clip.MapToSource(time));
        }
        return active.ToImmutable();

        void Add(TimelineTrack track, TimelineClip clip, SourcePosition position, ActiveTransition? transition = null)
        {
            var asset = assets[clip.AssetId];
            active.Add(new(sequenceId, track.Id, track.Order, track.Kind, clip, asset,
                asset.Streams.First(stream => stream.Index == clip.StreamIndex), position, transition));
        }
    }

    private static TransitionWindow? FindWindow(ImmutableArray<TransitionWindow> windows, SequenceTime time)
    {
        int low = 0, high = windows.Length - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            var window = windows[middle];
            if (time.Ticks < window.Start.Ticks) high = middle - 1;
            else if (time.Ticks >= window.End.Ticks) low = middle + 1;
            else return window;
        }
        return null;
    }
}
