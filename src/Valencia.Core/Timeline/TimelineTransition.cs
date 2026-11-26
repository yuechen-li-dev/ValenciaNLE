using System.Collections.Immutable;

namespace Valencia.Core.Timeline;

/// <summary>A dissolve on a video track or an equal-power crossfade on an audio track.</summary>
public sealed record TimelineTransition(Guid Id, Guid OutgoingClipId, Guid IncomingClipId, long BeforeTicks, long AfterTicks);

public sealed record TransitionWindow(TimelineTransition Transition, TimelineClip Outgoing, TimelineClip Incoming)
{
    public SequenceTime Cut => Incoming.Start;
    public SequenceTime Start => new(Cut.Ticks - Transition.BeforeTicks);
    public SequenceTime End => new(checked(Cut.Ticks + Transition.AfterTicks));
}

internal static class TransitionRules
{
    // Bounds are required only for the source handles actually sampled outside a clip.
    internal static string? Resolve(IReadOnlyDictionary<Guid, TimelineClip> clips, IReadOnlyDictionary<Guid, MediaAsset> assets,
        TimelineTransition transition, out TransitionWindow? window)
    {
        window = null;
        if (!clips.TryGetValue(transition.OutgoingClipId, out var outgoing) || !clips.TryGetValue(transition.IncomingClipId, out var incoming)
            || outgoing.End != incoming.Start)
            return "A transition requires an outgoing and incoming clip at the same cut on one track.";
        if (outgoing.Retime is not null || incoming.Retime is not null)
            return "Transitions require two clips at their original speed.";
        if (transition.BeforeTicks < 0 || transition.AfterTicks < 0 || (transition.BeforeTicks == 0 && transition.AfterTicks == 0)
            || transition.BeforeTicks > outgoing.Duration.Ticks || transition.AfterTicks > incoming.Duration.Ticks)
            return "Transition sides must fit inside both clips around the cut.";
        if (transition.BeforeTicks > 0 && (!TryStream(incoming, assets, out var incomingStream) || incomingStream!.Start is not { } first
            || incoming.SourceIn.Shift(new(-transition.BeforeTicks)).CompareTo(new(first)) < 0))
            return "The incoming clip lacks a known source handle before its in-point.";
        if (transition.AfterTicks > 0 && (!TryStream(outgoing, assets, out var outgoingStream) || outgoingStream!.End is not { } last
            || outgoing.SourceOut.Shift(new(transition.AfterTicks)).CompareTo(new(last)) > 0))
            return "The outgoing clip lacks a known source handle after its out-point.";
        window = new(transition, outgoing, incoming);
        return null;
    }

    private static bool TryStream(TimelineClip clip, IReadOnlyDictionary<Guid, MediaAsset> assets, out AssetStream? stream)
    {
        stream = assets.TryGetValue(clip.AssetId, out var asset) ? asset.Streams.FirstOrDefault(item => item.Index == clip.StreamIndex) : null;
        return stream is not null;
    }

    internal static ImmutableArray<TransitionWindow> Windows(TimelineTrack track, IReadOnlyDictionary<Guid, MediaAsset> assets, bool prune = false)
    {
        if (track.Transitions.IsEmpty) return [];
        var clips = track.Clips.ToDictionary(clip => clip.Id);
        var windows = new List<TransitionWindow>(track.Transitions.Length);
        foreach (var transition in track.Transitions)
        {
            string? error = Resolve(clips, assets, transition, out var window);
            if (error is null) windows.Add(window!);
            else if (!prune) throw new ArgumentException(error);
        }
        var active = ImmutableArray.CreateBuilder<TransitionWindow>();
        long previousEnd = 0;
        foreach (var window in windows.OrderBy(item => item.Start.Ticks).ThenBy(item => item.Transition.Id))
        {
            if (window.Start.Ticks < previousEnd)
            {
                if (prune) continue;
                throw new ArgumentException("Transition windows cannot overlap on a track.");
            }
            active.Add(window);
            previousEnd = window.End.Ticks;
        }
        return active.ToImmutable();
    }
}
