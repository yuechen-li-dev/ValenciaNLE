using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.Engine;

/// <summary>Shared import placement helper extracted from the native editor. All edits still validate through the reducer.</summary>
public static class InsertionCommands
{
    public static IReadOnlyList<EditOperation> Build(ProjectSnapshot snapshot, Guid sequenceId, ImportedAsset imported,
        SequenceTime at, Guid? targetTrack = null)
    {
        var operations = new List<EditOperation>();
        var tracks = new TimelineQueries(snapshot).Sequence(sequenceId).Tracks;
        foreach (var stream in imported.Asset.Streams)
        {
            // Target first, then the following tracks of the same kind: linked streams stack downward from the target.
            var used = operations.OfType<InsertClip>().Select(i => i.TrackId).ToHashSet();
            var track = TrackLayout.FromTarget(tracks, stream.Kind, targetTrack).FirstOrDefault(t => !used.Contains(t.Id));
            if (track is null)
            {
                // New tracks keep video grouped above audio.
                int order = TrackLayout.NewTrackOrder(tracks, stream.Kind);
                track = new(Guid.NewGuid(), stream.Kind, order, stream.Kind.ToString(), new(), []);
                tracks = [.. tracks.Insert(order, track).Select((t, i) => t with { Order = i })];
                operations.Add(new AddTrack(sequenceId, track.Id, track.Kind, order, track.Name));
            }
            var start = stream.Start ?? new MediaTimestamp(0, stream.TimeBaseNumerator, stream.TimeBaseDenominator);
            long offset = Math.Max(0, TimelineTime.Convert(start, TimeRounding.Floor).Ticks - TimelineTime.Convert(imported.ContainerStart, TimeRounding.Floor).Ticks);
            long length = stream.End is { } end ? new SourcePosition(end).Since(new(start)).Ticks : Math.Max(1, imported.Duration.Ticks - offset);
            var clip = new TimelineClip(Guid.NewGuid(), imported.Asset.Id, stream.Index, new(checked(at.Ticks + offset)),
                new(start), new SourcePosition(start).Shift(new(length)), new(imported.Asset.Name));
            operations.Add(new InsertClip(sequenceId, track.Id, clip));
        }
        return operations;
    }
}
