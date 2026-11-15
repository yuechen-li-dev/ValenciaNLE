using System.Collections.Immutable;
using Valencia.Core.Timeline;

namespace Valencia.Core.Editing;

internal static class EditReducer
{
    public static ProjectDocument Apply(ProjectDocument document, EditOperation operation)
    {
        if (operation is AddAsset add) return document with { Assets = document.Assets.Add(add.Asset) };
        if (operation is RelinkAsset relink)
        {
            ArgumentNullException.ThrowIfNull(relink.Asset);
            return document with { Assets = document.Assets.SetItem(Index(document.Assets, a => a.Id == relink.Asset.Id, "asset"), relink.Asset) };
        }
        if (operation is CreateSequence create) return document with
        {
            Sequences = document.Sequences.Add(new(create.SequenceId, create.Name, create.Video, create.AudioSampleRate, []))
        };
        Guid sequenceId = operation switch
        {
            AddTrack x => x.SequenceId, InsertClip x => x.SequenceId, MoveClip x => x.SequenceId,
            TrimClip x => x.SequenceId, SplitClip x => x.SequenceId, DeleteClip x => x.SequenceId,
            RippleDelete x => x.SequenceId, SetTrackState x => x.SequenceId,
            SetCreatorSettings x => x.SequenceId, SetClipProperties x => x.SequenceId,
            _ => throw new ArgumentException("Unsupported operation.")
        };
        int si = Index(document.Sequences, s => s.Id == sequenceId, "sequence");
        var sequence = document.Sequences[si];
        if (operation is SetCreatorSettings settings)
            return document with { Sequences = document.Sequences.SetItem(si, sequence with { Creator = settings.Settings }) };
        var tracks = sequence.Tracks;
        switch (operation)
        {
            case AddTrack x:
                DocumentValidator.Require(x.Order >= 0 && x.Order <= tracks.Length, "Invalid track insertion order.");
                tracks = tracks.Insert(x.Order, new(x.TrackId, x.Kind, x.Order, x.Name, new(), []));
                tracks = [.. tracks.Select((t, order) => t with { Order = order })];
                break;
            case InsertClip x:
            {
                ArgumentNullException.ThrowIfNull(x.Clip);
                int ti = Index(tracks, t => t.Id == x.TrackId, "track");
                Unlocked(tracks[ti]);
                tracks = tracks.SetItem(ti, WithClips(tracks[ti], tracks[ti].Clips.Add(x.Clip)));
                break;
            }
            case SetTrackState x:
            {
                int ti = Index(tracks, t => t.Id == x.TrackId, "track");
                tracks = tracks.SetItem(ti, tracks[ti] with { State = x.State });
                break;
            }
            case RippleDelete x:
                tracks = Ripple(sequence, x);
                break;
            default:
            {
                Guid clipId = operation switch
                {
                    MoveClip x => x.ClipId, TrimClip x => x.ClipId, SplitClip x => x.ClipId, DeleteClip x => x.ClipId,
                    SetClipProperties x => x.ClipId,
                    _ => throw new ArgumentException("Unsupported clip operation.")
                };
                int ti = Index(tracks, t => t.Clips.Any(c => c.Id == clipId), "clip");
                var track = tracks[ti];
                Unlocked(track);
                int ci = Index(track.Clips, c => c.Id == clipId, "clip");
                var clip = track.Clips[ci];
                var clips = track.Clips;
                switch (operation)
                {
                    case MoveClip x:
                    {
                        int destination = Index(tracks, t => t.Id == x.DestinationTrackId, "destination track");
                        Unlocked(tracks[destination]);
                        if (ti == destination) clips = clips.SetItem(ci, clip with { Start = x.Start });
                        else
                        {
                            clips = clips.RemoveAt(ci);
                            tracks = tracks.SetItem(destination, WithClips(tracks[destination], tracks[destination].Clips.Add(clip with { Start = x.Start })));
                        }
                        break;
                    }
                    case TrimClip x:
                        clips = clips.SetItem(ci, clip with { Start = x.Start, SourceIn = x.SourceIn, SourceOut = x.SourceOut });
                        break;
                    case SplitClip x:
                    {
                        DocumentValidator.Require(x.At.Ticks > clip.Start.Ticks && x.At.Ticks < clip.End.Ticks, "Split must be strictly inside the clip.");
                        var boundary = clip.MapToSource(x.At);
                        clips = clips.SetItem(ci, clip with { SourceOut = boundary }).Add(clip with
                        {
                            Id = x.RightClipId, Start = x.At, SourceIn = boundary
                        });
                        break;
                    }
                    case DeleteClip:
                        clips = clips.RemoveAt(ci);
                        break;
                    case SetClipProperties x:
                        clips = clips.SetItem(ci, clip with { Properties = x.Properties });
                        break;
                }
                tracks = tracks.SetItem(ti, WithClips(track, clips));
                break;
            }
        }
        return document with { Sequences = document.Sequences.SetItem(si, sequence with { Tracks = tracks }) };
    }

    private static ImmutableArray<TimelineTrack> Ripple(SequenceDocument sequence, RippleDelete x)
    {
        DocumentValidator.Require(x.Start.Ticks >= 0 && x.End.Ticks > x.Start.Ticks && x.End.Ticks <= sequence.Duration.Ticks,
            "Ripple range must be positive and inside sequence duration.");
        DocumentValidator.Require(!x.TrackIds.IsDefaultOrEmpty && x.TrackIds.Distinct().Count() == x.TrackIds.Length && !x.SplitIds.IsDefault,
            "Ripple requires explicit, unique target tracks and fragment identities.");
        var targets = x.TrackIds.ToHashSet();
        DocumentValidator.Require(targets.All(id => sequence.Tracks.Any(t => t.Id == id)), "Unknown ripple track.");
        DocumentValidator.Require(x.SplitIds.All(i => i is not null) && x.SplitIds.Select(i => i.ClipId).Distinct().Count() == x.SplitIds.Length, "Invalid or duplicate ripple fragment mapping.");
        var splitIds = x.SplitIds.ToDictionary(i => i.ClipId, i => i.RightClipId);
        long removed = checked(x.End.Ticks - x.Start.Ticks);
        var tracks = sequence.Tracks;
        for (int ti = 0; ti < tracks.Length; ti++)
        {
            var track = tracks[ti];
            if (!targets.Contains(track.Id)) continue;
            Unlocked(track);
            var clips = ImmutableArray.CreateBuilder<TimelineClip>();
            foreach (var clip in track.Clips)
            {
                if (clip.End.Ticks <= x.Start.Ticks) clips.Add(clip);
                else if (clip.Start.Ticks >= x.End.Ticks) clips.Add(clip with { Start = new(checked(clip.Start.Ticks - removed)) });
                else
                {
                    bool left = clip.Start.Ticks < x.Start.Ticks;
                    bool right = clip.End.Ticks > x.End.Ticks;
                    if (left) clips.Add(clip with { SourceOut = clip.MapToSource(x.Start) });
                    if (right)
                    {
                        Guid id = clip.Id;
                        if (left)
                        {
                            DocumentValidator.Require(splitIds.Remove(clip.Id, out id), "Ripple spanning a clip requires a right-fragment ID.");
                        }
                        clips.Add(clip with { Id = id, Start = x.Start, SourceIn = clip.MapToSource(x.End) });
                    }
                }
            }
            tracks = tracks.SetItem(ti, WithClips(track, clips.ToImmutable()));
        }
        DocumentValidator.Require(splitIds.Count == 0, "Unused ripple fragment IDs.");
        return tracks;
    }

    private static TimelineTrack WithClips(TimelineTrack track, ImmutableArray<TimelineClip> clips) =>
        track with { Clips = [.. clips.OrderBy(c => c.Start.Ticks).ThenBy(c => c.Id)] };
    private static void Unlocked(TimelineTrack track) => DocumentValidator.Require(!track.State.Locked, "Track is locked.");
    private static int Index<T>(ImmutableArray<T> items, Func<T, bool> predicate, string kind)
    {
        for (int i = 0; i < items.Length; i++) if (predicate(items[i])) return i;
        throw new ArgumentException($"Unknown {kind}.");
    }
}
