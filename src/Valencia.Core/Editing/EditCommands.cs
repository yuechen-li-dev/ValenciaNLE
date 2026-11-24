using System.Collections.Immutable;
using Valencia.Core.Timeline;

namespace Valencia.Core.Editing;

// All object IDs are supplied by the caller. Applying a command never invents IDs or reads clocks/files.
public abstract record EditOperation;
public sealed record AddAsset(MediaAsset Asset) : EditOperation;
public sealed record RelinkAsset(MediaAsset Asset) : EditOperation;
public sealed record CreateSequence(Guid SequenceId, string Name, VideoFormat Video, int AudioSampleRate) : EditOperation;
public sealed record SetSequenceMarker(Guid SequenceId, SequenceMarker Marker) : EditOperation;
public sealed record RemoveSequenceMarker(Guid SequenceId, Guid MarkerId) : EditOperation;
public sealed record AddTrack(Guid SequenceId, Guid TrackId, TrackKind Kind, int Order, string Name) : EditOperation;
public sealed record InsertClip(Guid SequenceId, Guid TrackId, TimelineClip Clip) : EditOperation;
/// <summary>Move within a sequence; both original and destination tracks must be unlocked.</summary>
public sealed record MoveClip(Guid SequenceId, Guid ClipId, Guid DestinationTrackId, SequenceTime Start) : EditOperation;
/// <summary>Explicit replacement of start/in/out; no implicit anchoring, slipping or neighbor movement.</summary>
public sealed record TrimClip(Guid SequenceId, Guid ClipId, SequenceTime Start, SourcePosition SourceIn, SourcePosition SourceOut) : EditOperation;
/// <summary>The left fragment keeps ClipId; the caller supplies the right fragment's stable ID.</summary>
public sealed record SplitClip(Guid SequenceId, Guid ClipId, SequenceTime At, Guid RightClipId) : EditOperation;
public sealed record DeleteClip(Guid SequenceId, Guid ClipId) : EditOperation;
public sealed record RippleSplitId(Guid ClipId, Guid RightClipId);
/// <summary>Remove [Start, End) on exactly TrackIds; all later clips on those tracks shift by the full range length.</summary>
public sealed record RippleDelete(Guid SequenceId, ImmutableArray<Guid> TrackIds, SequenceTime Start, SequenceTime End,
    ImmutableArray<RippleSplitId> SplitIds) : EditOperation;
/// <summary>Permitted on a locked track, so it can be unlocked; other edits cannot bypass the lock.</summary>
public sealed record SetTrackState(Guid SequenceId, Guid TrackId, TrackState State) : EditOperation;
public sealed record SetCreatorSettings(Guid SequenceId, CreatorSettings Settings) : EditOperation;
public sealed record SetOutputFraming(Guid SequenceId, Guid ProfileId, string? Name = null, int? Width = null, int? Height = null,
    string? Fit = null, double? FocalX = null, double? FocalY = null) : EditOperation;
public sealed record SetClipProperties(Guid SequenceId, Guid ClipId, ClipProperties Properties) : EditOperation;
public sealed record PatchClipColor(Guid SequenceId, Guid ClipId, ColorPatch Color) : EditOperation;
public sealed record SetClipRegion(Guid SequenceId, Guid ClipId, RegionEffect? Region) : EditOperation;

public sealed record EditCommand(Guid Id, int SchemaVersion, EditOperation Operation, long? ExpectedRevision = null)
{
    public const int CurrentSchemaVersion = 1;
}
public sealed record EditTransaction(Guid Id, int SchemaVersion, long ExpectedRevision, ImmutableArray<EditCommand> Commands);
public sealed record SessionRequest(Guid Id, int SchemaVersion, long ExpectedRevision);
public enum EditError { None, InvalidCommand, StaleRevision, InvalidEdit, Overflow, NoChange, NothingToUndo, NothingToRedo }
public sealed record EditResult(bool Success, Guid RequestId, long Revision, ProjectSnapshot Snapshot,
    EditError Error = EditError.None, string? Message = null, int? FailedCommandIndex = null);
