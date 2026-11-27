using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Xunit;
using static Valencia.Testing.TestProject;

namespace Valencia.Core.Tests;

/// <summary>Markers follow the picture through ripple edits; title clips ripple as clips and keep their own clock.</summary>
public sealed class AnnotationRippleTests
{
    private static TitleCue Title(int id, long start, long end, TransformCurve? curve = null, long entrance = 0, long exit = 0) =>
        new(Id(id), "Title " + id, T(start), T(end), "static", entrance, exit, Transform: curve, TrackId: Video2);

    private static EditingSession Annotated()
    {
        var session = Create(); // V1 holds one 10 s clip at 0; V2 is empty
        foreach (var op in new EditOperation[]
        {
            new SetSequenceMarker(SequenceId, new(Id(40), T(1), "before")),
            new SetSequenceMarker(SequenceId, new(Id(41), T(4), "inside")),
            new SetSequenceMarker(SequenceId, new(Id(42), T(7), "after")),
            new SetTitle(SequenceId, Title(51, 2, 4, entrance: T(1).Ticks, exit: T(1).Ticks)),
            new SetTitle(SequenceId, Title(53, 4, 9, new([new(0, X: .1), new(T(2).Ticks, X: .2), new(T(5).Ticks, X: .3)]))),
        }) Assert.True(Apply(session, op).Success);
        return session;
    }

    private static SequenceDocument Sequence(EditingSession session) => Query(session).Sequence(SequenceId);
    private static long Ticks(long seconds) => T(seconds).Ticks;
    private static TimelineClip Clip(EditingSession session, int id) => Sequence(session).Tracks.SelectMany(t => t.Clips).Single(c => c.Id == Id(id));

    [Fact]
    public void RippleDeleteMovesMarkersAndTitleClipsKeepTheirClock()
    {
        var session = Annotated();
        Assert.True(Apply(session, new RippleDelete(SequenceId, [Video1, Video2], T(3), T(5), [new(ClipId, Id(60))])).Success);
        var sequence = Sequence(session);
        Assert.Equal(new[] { Ticks(1), Ticks(3), Ticks(5) }, sequence.Markers.Select(m => m.At.Ticks));

        var tail = Clip(session, 51);                                   // loses its last second
        Assert.Equal((T(2), T(3)), (tail.Start, tail.End));
        Assert.Equal((Ticks(1), 0L), (tail.Title!.EntranceTicks, tail.Title.ExitTicks)); // clamped to the new length
        var head = Clip(session, 53);                                   // loses its first second
        Assert.Equal((T(3), T(7)), (head.Start, head.End));
        // Keys stay on the title's clock: the frame now at 3 s is 1 s into the title, as before the cut.
        Assert.Equal(Ticks(1), TitleClips.LocalTicks(head, T(3)));
        Assert.Equal(new[] { 0L, Ticks(2), Ticks(5) }, head.Title!.Transform!.Keys.Select(k => k.At));
        Assert.Equal(T(8), sequence.Duration);
    }

    [Fact]
    public void RippleDeleteIsOneUndoStepIncludingMarkers()
    {
        var session = Annotated();
        var before = Sequence(session);
        Assert.True(Apply(session, new RippleDelete(SequenceId, [Video1, Video2], T(3), T(5), [new(ClipId, Id(60))])).Success);
        Assert.True(session.Undo(Request(session)).Success);
        Assert.Equal(before.Markers, Sequence(session).Markers);
        Assert.True(before.Titles.SequenceEqual(Sequence(session).Titles));
        Assert.True(DocumentSemantics.Equal(Annotated().Snapshot.Document, session.Snapshot.Document));
    }

    [Fact]
    public void AudioOnlyRippleAndMagneticLiftsLeaveMarkersInPlace()
    {
        var session = Annotated();
        var audio = new TimelineClip(Id(70), AssetId, 1, T(0), new(new(0, 1, 48_000)), new(new(480_000, 1, 48_000)), new("A"));
        Assert.True(Apply(session, new InsertClip(SequenceId, Audio, audio)).Success);
        var before = Sequence(session);
        Assert.True(Apply(session, new RippleDelete(SequenceId, [Audio], T(3), T(5), [new(Id(70), Id(71))])).Success);
        Assert.Equal(before.Markers, Sequence(session).Markers);
        Assert.True(Apply(session, new RippleDelete(SequenceId, [Video1], T(3), T(5), [new(ClipId, Id(72))], CarryAnnotations: false)).Success);
        Assert.Equal(before.Markers, Sequence(session).Markers);
    }

    [Fact]
    public void InsertShiftMovesMarkersAtOrAfterThePoint()
    {
        var session = Annotated();
        Assert.True(Apply(session, new ShiftSequenceAnnotations(SequenceId, T(4), T(3))).Success);
        Assert.Equal(new[] { Ticks(1), Ticks(7), Ticks(10) }, Sequence(session).Markers.Select(m => m.At.Ticks));
        Assert.False(Apply(session, new ShiftSequenceAnnotations(SequenceId, T(1), new(0))).Success);
    }
}
