using System.Collections.Immutable;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Testing;
using Xunit;
using static Valencia.Testing.TestProject;

namespace Valencia.Core.Tests;

public sealed class EditingTests
{
    [Theory]
    [InlineData("asset")][InlineData("sequence")][InlineData("track")][InlineData("insert")]
    [InlineData("move")][InlineData("cross-track")][InlineData("trim")][InlineData("split")]
    [InlineData("delete")][InlineData("ripple")][InlineData("state")]
    public void EveryOperationUndoesAndRedoesExactSemanticState(string kind)
    {
        var session = Create();
        var before = session.Snapshot;
        EditOperation operation = kind switch
        {
            "asset" => new AddAsset(Asset() with { Id = Id(8) }),
            "sequence" => new CreateSequence(Id(8), "Other", new(1280, 720, new(24, 1)), 44100),
            "track" => new AddTrack(SequenceId, Id(8), TrackKind.Audio, 1, "A2"),
            "insert" => new InsertClip(SequenceId, Video1, Clip(Id(8), 12, 1, 4)),
            "move" => new MoveClip(SequenceId, ClipId, Video1, T(2)),
            "cross-track" => new MoveClip(SequenceId, ClipId, Video2, T(2)),
            "trim" => new TrimClip(SequenceId, ClipId, T(1), S(1), S(9)),
            "split" => new SplitClip(SequenceId, ClipId, T(5), Id(8)),
            "delete" => new DeleteClip(SequenceId, ClipId),
            "ripple" => new RippleDelete(SequenceId, [Video1], T(3), T(5), [new(ClipId, Id(8))]),
            "state" => new SetTrackState(SequenceId, Video1, new(Locked: true, Visible: false)),
            _ => throw new ArgumentException(kind)
        };
        var edit = Apply(session, operation);
        Assert.True(edit.Success, edit.Message);
        Assert.Equal(before.Revision + 1, edit.Revision);
        var after = edit.Snapshot;
        Assert.False(DocumentSemantics.Equal(before.Document, after.Document));
        Assert.True(session.Undo(Request(session)).Success);
        Assert.True(DocumentSemantics.Equal(before.Document, session.Snapshot.Document));
        Assert.True(session.Redo(Request(session)).Success);
        Assert.True(DocumentSemantics.Equal(after.Document, session.Snapshot.Document));
        Assert.Equal(before.Revision + 3, session.Snapshot.Revision);
        DocumentValidator.Validate(session.Snapshot.Document);
    }

    [Fact]
    public void GapAdjacencyAndMultipleIndependentTracksAreValid()
    {
        var session = Create();
        Assert.True(Apply(session, new InsertClip(SequenceId, Video1, Clip(Id(8), 10, 0, 2))).Success);
        Assert.True(Apply(session, new InsertClip(SequenceId, Video1, Clip(Id(9), 15, 0, 2))).Success);
        Assert.True(Apply(session, new InsertClip(SequenceId, Video2, Clip(Id(10), 0, 0, 5))).Success);
        Assert.Empty(Query(session).Evaluate(SequenceId, T(14)));
        Assert.Equal(2, Query(session).Evaluate(SequenceId, T(2)).Length);
        Assert.Equal(T(17), Query(session).Duration(SequenceId));
        Assert.Equal([ClipId, Id(8), Id(10)], Query(session).Intersecting(SequenceId, T(4), T(11)).Select(c => c.Id));
    }

    [Theory]
    [InlineData("overlap")][InlineData("zero")][InlineData("negative-start")][InlineData("source-before")]
    [InlineData("source-after")][InlineData("missing-asset")][InlineData("missing-stream")][InlineData("wrong-kind")]
    [InlineData("duplicate-id")][InlineData("timebase")][InlineData("overflow")][InlineData("null")]
    public void InvalidInsertLeavesSnapshotAndHistoryUnchanged(string kind)
    {
        var session = Create();
        var clip = Clip(Id(8), 12, 0, 2);
        clip = kind switch
        {
            "overlap" => clip with { Start = T(9) }, "zero" => clip with { SourceOut = S(0) },
            "negative-start" => clip with { Start = T(-1) }, "source-before" => clip with { SourceIn = S(-1) },
            "source-after" => clip with { SourceOut = S(21) }, "missing-asset" => clip with { AssetId = Id(999) },
            "missing-stream" => clip with { StreamIndex = 8 }, "wrong-kind" => clip with { StreamIndex = 1 },
            "duplicate-id" => clip with { Id = ClipId }, "timebase" => clip with { SourceIn = new(new(0, 1, 1000)) },
            "overflow" => clip with { Start = new(long.MaxValue) }, "null" => null!, _ => clip
        };
        var before = session.Snapshot;
        var result = Apply(session, new InsertClip(SequenceId, Video1, clip));
        Assert.False(result.Success);
        Assert.Same(before, session.Snapshot);
        Assert.True(session.CanUndo);
        Assert.False(session.CanRedo);
    }

    [Fact]
    public void TransactionRollsBackAndSuccessfulGroupIsOneUndoEntry()
    {
        var session = Create();
        var before = session.Snapshot;
        ImmutableArray<EditCommand> operations = [
            new(Id(201), 1, new SplitClip(SequenceId, ClipId, T(5), Id(8))),
            new(Id(202), 1, new MoveClip(SequenceId, Id(8), Video2, T(7))),
            new(Id(203), 1, new TrimClip(SequenceId, ClipId, T(0), S(0), S(0)))];
        var failed = session.Execute(new EditTransaction(Id(200), 1, before.Revision, operations));
        Assert.False(failed.Success);
        Assert.Equal(2, failed.FailedCommandIndex);
        Assert.Same(before, session.Snapshot);
        var result = session.Execute(new EditTransaction(Id(204), 1, before.Revision, operations.RemoveAt(2)));
        Assert.True(result.Success, result.Message);
        Assert.Equal(before.Revision + 1, result.Revision);
        Assert.True(session.Undo(Request(session)).Success);
        Assert.True(DocumentSemantics.Equal(before.Document, session.Snapshot.Document));
        Assert.True(session.Redo(Request(session)).Success);
        Assert.True(DocumentSemantics.Equal(result.Snapshot.Document, session.Snapshot.Document));
    }

    [Fact]
    public void FailurePreservesRedoButNewCommitInvalidatesIt()
    {
        var session = Create();
        Assert.True(Apply(session, new SplitClip(SequenceId, ClipId, T(5), Id(8))).Success);
        Assert.True(session.Undo(Request(session)).Success);
        Assert.True(session.CanRedo);
        Assert.False(Apply(session, new SplitClip(SequenceId, ClipId, T(0), Id(8))).Success);
        Assert.True(session.CanRedo);
        Assert.True(Apply(session, new MoveClip(SequenceId, ClipId, Video1, T(1))).Success);
        Assert.False(session.CanRedo);
        Assert.Equal(EditError.NothingToRedo, session.Redo(Request(session)).Error);
    }

    [Theory]
    [InlineData("insert")][InlineData("move")][InlineData("trim")][InlineData("split")][InlineData("delete")][InlineData("ripple")][InlineData("move-in")]
    public void LockedTracksRejectAllClipEdits(string kind)
    {
        var session = Create();
        Assert.True(Apply(session, new InsertClip(SequenceId, Video2, Clip(Id(8)))).Success);
        Assert.True(Apply(session, new SetTrackState(SequenceId, Video1, new(Locked: true))).Success);
        EditOperation operation = kind switch
        {
            "insert" => new InsertClip(SequenceId, Video1, Clip(Id(9), 12)),
            "move" => new MoveClip(SequenceId, ClipId, Video2, T(12)),
            "move-in" => new MoveClip(SequenceId, Id(8), Video1, T(12)),
            "trim" => new TrimClip(SequenceId, ClipId, T(1), S(1), S(9)),
            "split" => new SplitClip(SequenceId, ClipId, T(5), Id(9)),
            "delete" => new DeleteClip(SequenceId, ClipId),
            _ => new RippleDelete(SequenceId, [Video2, Video1], T(3), T(5), [new(ClipId, Id(9)), new(Id(8), Id(10))])
        };
        var before = session.Snapshot;
        Assert.False(Apply(session, operation).Success);
        Assert.Same(before, session.Snapshot);
        Assert.True(Apply(session, new SetTrackState(SequenceId, Video1, new())).Success);
    }

    [Fact]
    public void RippleScopeClosesGapAndPreservesDiscontinuousSourcePieces()
    {
        var session = Create();
        Assert.True(Apply(session, new InsertClip(SequenceId, Video2, Clip(Id(8), 4))).Success);
        Assert.True(Apply(session, new InsertClip(SequenceId, Video1, Clip(Id(9), 12, 0, 2))).Success);
        var other = Query(session).Clips(SequenceId, Video2);
        Assert.True(Apply(session, new RippleDelete(SequenceId, [Video1], T(3), T(5), [new(ClipId, Id(10))])).Success);
        var clips = Query(session).Clips(SequenceId, Video1);
        Assert.Equal(3, clips.Length);
        Assert.Equal(T(3), clips[0].End);
        Assert.Equal(T(3), clips[1].Start);
        Assert.True(S(5).EquivalentTo(clips[1].SourceIn));
        Assert.Equal(T(8), clips[1].End);
        Assert.Equal(T(10), clips[2].Start);
        Assert.Equal(other, Query(session).Clips(SequenceId, Video2));
        Assert.Equal(Id(10), Query(session).Evaluate(SequenceId, T(3)).First(x => x.TrackId == Video1).Clip.Id);
    }

    [Theory]
    [InlineData(0, 2, 0, 2, 10)] // keep right, original ID
    [InlineData(8, 10, 0, 0, 8)] // keep left
    [InlineData(0, 10, 0, 0, 0)] // remove entire clip
    public void RippleHandlesClipEdges(long from, long to, long start, long sourceIn, long sourceOut)
    {
        var session = Create();
        Assert.True(Apply(session, new RippleDelete(SequenceId, [Video1], T(from), T(to), [])).Success);
        var clips = Query(session).Clips(SequenceId, Video1);
        if (sourceOut == 0) Assert.Empty(clips);
        else
        {
            var clip = Assert.Single(clips);
            Assert.Equal(ClipId, clip.Id);
            Assert.Equal(T(start), clip.Start);
            Assert.True(clip.SourceIn.EquivalentTo(S(sourceIn)));
            Assert.True(clip.SourceOut.EquivalentTo(S(sourceOut)));
        }
    }

    [Fact]
    public void RevisionSchemaIdentityNoChangeAndBoundedHistoryAreEnforced()
    {
        var session = Create(limit: 1);
        var snapshot = session.Snapshot;
        Assert.Equal(EditError.StaleRevision, session.Execute(new EditCommand(Id(100), 1, new DeleteClip(SequenceId, ClipId), 0)).Error);
        Assert.Equal(EditError.InvalidCommand, session.Execute(new EditCommand(Guid.Empty, 1, new DeleteClip(SequenceId, ClipId), snapshot.Revision)).Error);
        Assert.Equal(EditError.InvalidCommand, session.Execute(new EditCommand(Id(100), 2, new DeleteClip(SequenceId, ClipId), snapshot.Revision)).Error);
        Assert.Equal(EditError.NoChange, Apply(session, new MoveClip(SequenceId, ClipId, Video1, T(0))).Error);
        Assert.Same(snapshot, session.Snapshot);
        Assert.True(session.Undo(Request(session)).Success);
        Assert.Equal(EditError.NothingToUndo, session.Undo(Request(session)).Error);
        Assert.True(session.Redo(Request(session)).Success);
        var max = new EditingSession(new ProjectSnapshot(snapshot.Document, long.MaxValue));
        Assert.Equal(EditError.Overflow, Apply(max, new DeleteClip(SequenceId, ClipId)).Error);
        Assert.False(max.CanUndo);
    }

    [Fact]
    public async Task ConcurrentCommitsWithSameRevisionHaveExactlyOneWinner()
    {
        var session = Create();
        long revision = session.Snapshot.Revision;
        var results = await Task.WhenAll(Enumerable.Range(1, 12).Select(i => Task.Run(() => session.Execute(
            new EditCommand(Id(300 + i), 1, new MoveClip(SequenceId, ClipId, Video1, T(i)), revision)))));
        Assert.Single(results, r => r.Success);
        Assert.Equal(11, results.Count(r => r.Error == EditError.StaleRevision));
        Assert.Equal(revision + 1, session.Snapshot.Revision);
    }

    [Theory]
    [InlineData("zero")][InlineData("reversed")][InlineData("negative")][InlineData("outside")]
    [InlineData("no-targets")][InlineData("unknown-target")][InlineData("duplicate-target")]
    [InlineData("missing-id")][InlineData("unused-id")][InlineData("duplicate-id")]
    public void InvalidRippleIsAtomic(string kind)
    {
        var session = Create();
        var command = new RippleDelete(SequenceId, [Video1], T(3), T(5), [new(ClipId, Id(8))]);
        command = kind switch
        {
            "zero" => command with { End = T(3) }, "reversed" => command with { End = T(1) },
            "negative" => command with { Start = T(-1) }, "outside" => command with { End = T(11) },
            "no-targets" => command with { TrackIds = [] }, "unknown-target" => command with { TrackIds = [Id(99)] },
            "duplicate-target" => command with { TrackIds = [Video1, Video1] }, "missing-id" => command with { SplitIds = [] },
            "unused-id" => command with { SplitIds = [new(ClipId, Id(8)), new(Id(99), Id(9))] },
            "duplicate-id" => command with { SplitIds = [new(ClipId, ClipId)] }, _ => command
        };
        var before = session.Snapshot;
        Assert.False(Apply(session, command).Success);
        Assert.Same(before, session.Snapshot);
    }

    [Theory]
    [InlineData(0)][InlineData(10)][InlineData(11)]
    public void SplitRejectsBoundariesAndOutsideRange(long at)
    {
        var session = Create();
        var before = session.Snapshot;
        Assert.False(Apply(session, new SplitClip(SequenceId, ClipId, T(at), Id(8))).Success);
        Assert.Same(before, session.Snapshot);
    }

    [Fact]
    public void TransactionChildRevisionsAndIdsAreValidatedAgainstStartingRevision()
    {
        var session = Create();
        var before = session.Snapshot;
        EditCommand first = new(Id(401), 1, new SplitClip(SequenceId, ClipId, T(5), Id(8)), before.Revision);
        EditCommand second = new(Id(402), 1, new DeleteClip(SequenceId, Id(8)), before.Revision + 1);
        var stale = session.Execute(new EditTransaction(Id(400), 1, before.Revision, [first, second]));
        Assert.Equal(EditError.StaleRevision, stale.Error);
        Assert.Same(before, session.Snapshot);
        var duplicate = session.Execute(new EditTransaction(Id(400), 1, before.Revision, [first, second with { Id = first.Id, ExpectedRevision = null }]));
        Assert.Equal(EditError.InvalidCommand, duplicate.Error);
        Assert.Same(before, session.Snapshot);
        Assert.Equal(EditError.InvalidCommand, session.Execute(new EditTransaction(Id(400), 1, before.Revision, [])).Error);
    }
}
