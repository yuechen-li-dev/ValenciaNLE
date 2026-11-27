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
            SetOutputFraming x => x.SequenceId,
            PatchClipColor x => x.SequenceId, SetClipRegion x => x.SequenceId, SetClipMotion x => x.SequenceId,
            SetSequenceMarker x => x.SequenceId, RemoveSequenceMarker x => x.SequenceId, ShiftSequenceAnnotations x => x.SequenceId,
            SetTitle x => x.SequenceId, RemoveTitle x => x.SequenceId, SetTitleContent x => x.SequenceId,
            SetTitleTransform x => x.SequenceId, SetClipTransform x => x.SequenceId,
            SetClipSpeed x => x.SequenceId, FreezeClip x => x.SequenceId,
            SetClipGain x => x.SequenceId,
            PlaceSoundHit x => x.SequenceId, PatchTrackGain x => x.SequenceId, SetTrackTransition x => x.SequenceId,
            _ => throw new ArgumentException("Unsupported operation.")
        };
        int si = Index(document.Sequences, s => s.Id == sequenceId, "sequence");
        var sequence = document.Sequences[si];
        if (operation is SetSequenceMarker set)
        {
            ArgumentNullException.ThrowIfNull(set.Marker);
            DocumentValidator.Require(set.Marker.At.Ticks <= sequence.Duration.Ticks, "Marker must be within sequence duration.");
            var markers = sequence.Markers.Where(marker => marker.Id != set.Marker.Id).Append(set.Marker)
                .OrderBy(marker => marker.At.Ticks).ThenBy(marker => marker.Id);
            return document with { Sequences = document.Sequences.SetItem(si, sequence with { Markers = [.. markers] }) };
        }
        if (operation is ShiftSequenceAnnotations shift)
        {
            DocumentValidator.Require(shift.At.Ticks >= 0 && shift.Shift.Ticks > 0, "An annotation shift requires a non-negative point and a positive length.");
            return document with { Sequences = document.Sequences.SetItem(si, SequenceAnnotations.Insert(sequence, shift.At, shift.Shift)) };
        }
        if (operation is RemoveSequenceMarker remove)
        {
            int index = Index(sequence.Markers, marker => marker.Id == remove.MarkerId, "marker");
            return document with { Sequences = document.Sequences.SetItem(si, sequence with { Markers = sequence.Markers.RemoveAt(index) }) };
        }
        if (operation is SetCreatorSettings settings)
            return document with { Sequences = document.Sequences.SetItem(si, sequence with { Creator = settings.Settings }) };
        if (operation is SetTitle or RemoveTitle or SetTitleTransform or SetTitleContent)
            return document with { Sequences = document.Sequences.SetItem(si, sequence with { Tracks = Titles(sequence, operation, document.Assets) }) };
        if (operation is PatchTrackGain gain)
        {
            var track = sequence.Tracks[Index(sequence.Tracks, t => t.Id == gain.TrackId, "audio track")];
            Unlocked(track);
            DocumentValidator.Require(track.Kind == TrackKind.Audio && (gain.GainDb is not null || gain.Envelope is not null),
                "Track gain requires an audio track and a level or envelope.");
            var creator = sequence.Creator ?? new CreatorSettings(1, [], new(), [], [], PeakCeilingDb: 0);
            var previous = creator.Audio.FirstOrDefault(t => t.TrackId == gain.TrackId);
            var updated = new TrackAudio(gain.TrackId, gain.GainDb ?? previous?.GainDb ?? 0, gain.Envelope ?? previous?.Envelope ?? []);
            var audio = previous is null ? creator.Audio.Add(updated) : creator.Audio.Replace(previous, updated);
            return document with { Sequences = document.Sequences.SetItem(si, sequence with { Creator = creator with { Audio = audio } }) };
        }
        if (operation is SetOutputFraming framing)
        {
            var creator = sequence.Creator ?? new CreatorSettings(1, [], new(), [], []);
            var profile = creator.Profiles.FirstOrDefault(p => p.Id == framing.ProfileId);
            if (profile is null)
            {
                DocumentValidator.Require(framing.Width.HasValue && framing.Height.HasValue && !string.IsNullOrWhiteSpace(framing.Name),
                    "A new output profile needs a name, width and height.");
                profile = new(framing.ProfileId, framing.Name!, framing.Width!.Value, framing.Height!.Value,
                    sequence.Video.FrameRate, CaptionY: creator.CaptionStyle.Y);
            }
            var changed = profile with { Name = framing.Name ?? profile.Name, Width = framing.Width ?? profile.Width, Height = framing.Height ?? profile.Height,
                Fit = framing.Fit ?? profile.Fit, FocalX = framing.FocalX ?? profile.FocalX, FocalY = framing.FocalY ?? profile.FocalY };
            DocumentValidator.Require(!string.IsNullOrWhiteSpace(changed.Name) && changed.Name.Length <= 120, "An output profile name must contain 1..120 characters.");
            var profiles = creator.Profiles.Contains(profile) ? creator.Profiles.Replace(profile, changed) : creator.Profiles.Add(changed);
            return document with { Sequences = document.Sequences.SetItem(si, sequence with { Creator = creator with { Profiles = profiles } }) };
        }
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
                tracks = tracks.SetItem(ti, WithClips(tracks[ti], tracks[ti].Clips.Add(x.Clip), document.Assets));
                break;
            }
            case PlaceSoundHit x:
            {
                int ti = Index(tracks, t => t.Id == x.TrackId, "audio track");
                var track = tracks[ti];
                Unlocked(track);
                DocumentValidator.Require(track.Kind == TrackKind.Audio && x.At.Ticks >= 0
                    && x.SourceOffset.Ticks >= 0 && x.Duration.Ticks > 0 && x.GainDb is >= -120 and <= 30,
                    "A sound hit requires an audio track, nonnegative placement and offset, positive duration and valid gain.");
                var rate = sequence.Video.FrameRate;
                var frame = TimelineTime.FrameIndex(x.At, rate, TimeRounding.Floor);
                DocumentValidator.Require(TimelineTime.FromFrames(frame, rate) == x.At, "A sound hit must start on a sequence frame boundary.");
                var asset = document.Assets[Index(document.Assets, a => a.Id == x.AssetId, "sound asset")];
                var stream = asset.Streams[Index(asset.Streams, s => s.Index == x.StreamIndex && s.Kind == TrackKind.Audio, "sound stream")];
                var origin = stream.Start ?? new MediaTimestamp(0, stream.TimeBaseNumerator, stream.TimeBaseDenominator);
                var source = new SourcePosition(origin).Shift(x.SourceOffset);
                var clip = new TimelineClip(x.ClipId, asset.Id, stream.Index, x.At, source, source.Shift(x.Duration),
                    new(asset.Name, Gain: (decimal)Math.Pow(10, x.GainDb / 20), Audio: new(AlignStartToSample: true)));
                tracks = tracks.SetItem(ti, WithClips(track, track.Clips.Add(clip), document.Assets));
                break;
            }
            case SetTrackTransition x:
            {
                int ti = Index(tracks, t => t.Id == x.TrackId, "transition track");
                var track = tracks[ti];
                Unlocked(track);
                if (x.Transition is { } transition)
                {
                    DocumentValidator.Require(transition.Id == x.TransitionId, "Transition ID mismatch.");
                    track = track with { Transitions = [.. track.Transitions.Where(item => item.Id != x.TransitionId), transition] };
                    track = track with { Transitions = [.. TransitionRules.Windows(track, document.Assets.ToDictionary(asset => asset.Id))
                        .Select(window => window.Transition)] };
                }
                else
                    track = track with { Transitions = track.Transitions.RemoveAt(Index(track.Transitions, item => item.Id == x.TransitionId, "transition")) };
                tracks = tracks.SetItem(ti, track);
                break;
            }
            case SetTrackState x:
            {
                int ti = Index(tracks, t => t.Id == x.TrackId, "track");
                tracks = tracks.SetItem(ti, tracks[ti] with { State = x.State });
                break;
            }
            case RippleDelete x:
                tracks = Ripple(sequence, x, document.Assets);
                // A ripple can shorten a title clip: keep its entrance and exit inside what is left.
                tracks = [.. tracks.Select(t => t.Clips.Any(c => c.IsTitle()) ? t with { Clips = [.. t.Clips.Select(FitTitle)] } : t)];
                // Titles and markers sit over the picture: they ripple with any video track, never with audio alone.
                if (x.CarryAnnotations && sequence.Tracks.Any(t => t.Kind == TrackKind.Video && x.TrackIds.Contains(t.Id)))
                    sequence = SequenceAnnotations.Remove(sequence, x.Start, x.End);
                break;
            default:
            {
                Guid clipId = operation switch
                {
                    MoveClip x => x.ClipId, TrimClip x => x.ClipId, SplitClip x => x.ClipId, DeleteClip x => x.ClipId,
                    SetClipProperties x => x.ClipId, PatchClipColor x => x.ClipId, SetClipRegion x => x.ClipId, SetClipMotion x => x.ClipId,
                    SetClipTransform x => x.ClipId,
                    SetClipSpeed x => x.ClipId, FreezeClip x => x.ClipId,
                    SetClipGain x => x.ClipId,
                    _ => throw new ArgumentException("Unsupported clip operation.")
                };
                int ti = Index(tracks, t => t.Clips.Any(c => c.Id == clipId), "clip");
                var track = tracks[ti];
                Unlocked(track);
                int ci = Index(track.Clips, c => c.Id == clipId, "clip");
                var clip = track.Clips[ci];
                DocumentValidator.Require(!clip.IsTitle() || operation is MoveClip or TrimClip or SplitClip or DeleteClip or SetClipProperties,
                    "Titles have their own style, motion and keyframes; picture effects, speed and audio gain apply to footage.");
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
                            tracks = tracks.SetItem(destination, WithClips(tracks[destination], tracks[destination].Clips.Add(clip with { Start = x.Start }), document.Assets));
                        }
                        break;
                    }
                    case TrimClip x:
                    {
                        var retime = clip.Retime;
                        if (retime?.Hold is not null)
                        {
                            DocumentValidator.Require(x.HoldTicks is > 0 && x.SourceIn == clip.SourceIn && x.SourceOut == clip.SourceOut,
                                "A freeze trim changes timeline duration and keeps its source range.");
                            retime = retime with { HoldTicks = x.HoldTicks!.Value };
                        }
                        else DocumentValidator.Require(x.HoldTicks is null, "Only a freeze frame can set a trim duration.");
                        clips = clips.SetItem(ci, FitTitle(clip with { Start = x.Start, SourceIn = x.SourceIn, SourceOut = x.SourceOut, Retime = retime }));
                        break;
                    }
                    case SetClipSpeed x:
                    {
                        var rate = new VideoRetime(x.Numerator, x.Denominator);
                        rate.Validate(clip);
                        ApplyRetime(x.Numerator == x.Denominator ? null : rate);
                        break;
                    }
                    case FreezeClip x:
                        ApplyRetime(new(0, 1, clip.MapToSource(x.At), x.Duration.Ticks));
                        break;
                    case SplitClip x:
                    {
                        DocumentValidator.Require(x.At.Ticks > clip.Start.Ticks && x.At.Ticks < clip.End.Ticks, "Split must be strictly inside the clip.");
                        var left = clip.Slice(clip.Start, x.At);
                        var right = clip.Slice(x.At, clip.End) with { Id = x.RightClipId };
                        // A split title plays continuously: the entrance stays on the left half, the exit on the right.
                        if (clip.Title is { } split)
                        {
                            left = FitTitle(left with { Title = split with { ExitTicks = 0 } });
                            right = FitTitle(right with { Title = split with { EntranceTicks = 0 } });
                        }
                        clips = clips.SetItem(ci, left).Add(right);
                        break;
                    }
                    case DeleteClip:
                        clips = clips.RemoveAt(ci);
                        break;
                    case SetClipProperties x:
                        clips = clips.SetItem(ci, clip with { Properties = x.Properties });
                        break;
                    case SetClipGain x:
                        DocumentValidator.Require(track.Kind == TrackKind.Audio && x.GainDb is >= -120 and <= 30,
                            "Clip gain requires an audio clip and a level from -120 to +30 dB.");
                        clips = clips.SetItem(ci, clip with { Properties = clip.Properties with { Gain = (decimal)Math.Pow(10, x.GainDb / 20) } });
                        break;
                    case PatchClipColor x:
                    {
                        DocumentValidator.Require(track.Kind == TrackKind.Video && x.Color is not null, "Basic Color requires a video clip and color parameters.");
                        var video = x.Color!.Apply(clip.Properties.Video ?? new());
                        clips = clips.SetItem(ci, clip with { Properties = clip.Properties with
                            { Video = clip.Properties.Video is null && video == new VideoEffects() ? null : video } });
                        break;
                    }
                    case SetClipRegion x:
                    {
                        DocumentValidator.Require(track.Kind == TrackKind.Video, "Region Effects require a video clip.");
                        var video = (clip.Properties.Video ?? new()) with { Region = x.Region };
                        clips = clips.SetItem(ci, clip with { Properties = clip.Properties with
                            { Video = clip.Properties.Video is null && video == new VideoEffects() ? null : video } });
                        break;
                    }
                    case SetClipMotion x:
                    {
                        DocumentValidator.Require(track.Kind == TrackKind.Video, "Clip motion requires a video clip.");
                        var video = (clip.Properties.Video ?? new()) with { Motion = x.Motion };
                        clips = clips.SetItem(ci, clip with { Properties = clip.Properties with
                            { Video = x.Motion is null && video == new VideoEffects() ? null : video } });
                        break;
                    }
                    case SetClipTransform x:
                    {
                        DocumentValidator.Require(track.Kind == TrackKind.Video, "A clip transform requires a video clip.");
                        var origin = x.PreserveOrigin ? ClipTransform.KeyOrigin(clip) : clip.SourceIn;
                        x.Curve?.Validate(x.PreserveOrigin ? long.MaxValue : clip.Duration.Ticks);
                        var video = (clip.Properties.Video ?? new()) with
                            { Transform = x.Curve is null ? null : new ClipTransform(origin, x.Curve) };
                        clips = clips.SetItem(ci, clip with { Properties = clip.Properties with
                            { Video = x.Curve is null && video == new VideoEffects() ? null : video } });
                        break;
                    }
                }
                tracks = tracks.SetItem(ti, WithClips(track, clips, document.Assets));
                break;

                void ApplyRetime(VideoRetime? retime)
                {
                    DocumentValidator.Require(track.Kind == TrackKind.Video, "Speed and freeze frame require a video clip. Audio keeps its original timing.");
                    DocumentValidator.Require(!track.Transitions.Any(t => t.IncomingClipId == clip.Id || t.OutgoingClipId == clip.Id),
                        "Remove the clip's transitions before changing its speed or applying a freeze frame.");
                    retime?.Validate(clip);
                    clips = clips.SetItem(ci, clip with { Retime = retime });
                }
            }
        }
        return document with { Sequences = document.Sequences.SetItem(si, sequence with { Tracks = tracks }) };
    }

    /// <summary>
    /// Title clips. <see cref="SetTitle"/> upserts by clip ID: an existing title keeps its track and its title clock
    /// (so keyframes stay put); a new one goes on the cue's track, else the highest unlocked video track that is free
    /// over its range, else a new video track above the rest whose ID is derived from the clip ID (never invented).
    /// </summary>
    private static ImmutableArray<TimelineTrack> Titles(SequenceDocument sequence, EditOperation operation, ImmutableArray<MediaAsset> assets)
    {
        var tracks = sequence.Tracks;
        Guid id = operation switch
        {
            SetTitle x => x.Cue?.Id ?? throw new ArgumentNullException(nameof(operation)),
            RemoveTitle x => x.TitleId, SetTitleTransform x => x.TitleId, SetTitleContent x => x.ClipId,
            _ => throw new ArgumentException("Unsupported title operation.")
        };
        int ti = tracks.IndexOf(tracks.FirstOrDefault(t => t.Clips.Any(c => c.Id == id))!);
        TimelineClip? existing = ti >= 0 ? tracks[ti].Clips.First(c => c.Id == id) : null;
        DocumentValidator.Require(existing is null || existing.IsTitle(), "That clip is not a title.");
        if (operation is not SetTitle set)
        {
            DocumentValidator.Require(existing is not null, "Unknown title.");
            var track = tracks[ti]; Unlocked(track);
            var clips = track.Clips.Remove(existing!);
            if (operation is SetTitleTransform transform)
                clips = clips.Add(existing! with { Title = existing.Title! with { Transform = transform.Curve } });
            else if (operation is SetTitleContent content)
            {
                ArgumentNullException.ThrowIfNull(content.Content);
                clips = clips.Add(existing! with { Title = content.Content });
            }
            return tracks.SetItem(ti, WithClips(track, clips, assets));
        }

        var cue = set.Cue;
        long duration = cue.End.Ticks - cue.Start.Ticks;
        DocumentValidator.Require(cue.Start.Ticks >= 0 && duration > 0, "A title needs a positive range on the sequence.");
        TimelineClip placed;
        if (existing is not null)
        {
            var track = tracks[ti]; Unlocked(track);
            tracks = tracks.SetItem(ti, WithClips(track, track.Clips.Remove(existing), assets));
            placed = existing with
            {
                Start = cue.Start, SourceOut = existing.SourceIn.Shift(new(duration)),
                Title = cue.Content with { Transform = cue.Transform ?? existing.Title!.Transform },
            };
        }
        else placed = TitleClips.Create(cue.Id, cue.Start, new(duration), cue.Content);

        int target = cue.TrackId is { } wanted ? Index(tracks, t => t.Id == wanted, "title track")
            : existing is not null && cue.TrackId is null ? ti
            : tracks.Select((t, i) => (t, i)).Where(p => p.t.Kind == TrackKind.Video && !p.t.State.Locked
                && !p.t.Clips.Any(c => c.Start.Ticks < cue.End.Ticks && c.End.Ticks > cue.Start.Ticks))
                .Select(p => p.i).DefaultIfEmpty(-1).Last();
        if (target < 0)
        {
            int above = tracks.Select((t, i) => (t, i)).Where(p => p.t.Kind == TrackKind.Video).Select(p => p.i).DefaultIfEmpty(-1).Last() + 1;
            var bytes = cue.Id.ToByteArray(); bytes[0] ^= 0x7A; bytes[1] ^= 0x1E; bytes[2] ^= 0xC1;
            tracks = tracks.Insert(above, new(new Guid(bytes), TrackKind.Video, above, $"V{tracks.Count(t => t.Kind == TrackKind.Video) + 1}", new(), []));
            tracks = [.. tracks.Select((t, order) => t with { Order = order })];
            target = above;
        }
        DocumentValidator.Require(tracks[target].Kind == TrackKind.Video, "Titles go on video tracks.");
        Unlocked(tracks[target]);
        return tracks.SetItem(target, WithClips(tracks[target], tracks[target].Clips.Add(placed), assets));
    }

    private static ImmutableArray<TimelineTrack> Ripple(SequenceDocument sequence, RippleDelete x, ImmutableArray<MediaAsset> assets)
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
                    if (left) clips.Add(clip.Slice(clip.Start, x.Start));
                    if (right)
                    {
                        Guid id = clip.Id;
                        if (left)
                        {
                            DocumentValidator.Require(splitIds.Remove(clip.Id, out id), "Ripple spanning a clip requires a right-fragment ID.");
                        }
                        clips.Add(clip.Slice(x.End, clip.End) with { Id = id, Start = x.Start });
                    }
                }
            }
            tracks = tracks.SetItem(ti, WithClips(track, clips.ToImmutable(), assets));
        }
        DocumentValidator.Require(splitIds.Count == 0, "Unused ripple fragment IDs.");
        return tracks;
    }

    private static TimelineTrack WithClips(TimelineTrack track, ImmutableArray<TimelineClip> clips, ImmutableArray<MediaAsset> assets)
    {
        track = track with { Clips = [.. clips.OrderBy(c => c.Start.Ticks).ThenBy(c => c.Id)] };
        if (track.Transitions.IsEmpty) return track;
        return track with { Transitions = [.. TransitionRules.Windows(track, assets.ToDictionary(asset => asset.Id), prune: true)
            .Select(window => window.Transition)] };
    }
    /// <summary>Shortening a title keeps its entrance and exit inside it.</summary>
    private static TimelineClip FitTitle(TimelineClip clip)
    {
        if (clip.Title is not { } title) return clip;
        long duration = clip.Duration.Ticks, entrance = Math.Clamp(title.EntranceTicks, 0, Math.Max(0, duration));
        return clip with { Title = title with { EntranceTicks = entrance, ExitTicks = Math.Clamp(title.ExitTicks, 0, Math.Max(0, duration - entrance)) } };
    }
    private static void Unlocked(TimelineTrack track) => DocumentValidator.Require(!track.State.Locked, "Track is locked.");
    private static int Index<T>(ImmutableArray<T> items, Func<T, bool> predicate, string kind)
    {
        for (int i = 0; i < items.Length; i++) if (predicate(items[i])) return i;
        throw new ArgumentException($"Unknown {kind}.");
    }
}
