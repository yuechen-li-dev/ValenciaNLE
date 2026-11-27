using System.Collections.Immutable;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Xunit;
using static Valencia.Testing.TestProject;

namespace Valencia.Core.Tests;

/// <summary>Sequence titles and markers follow the picture through ripple edits.</summary>
public sealed class AnnotationRippleTests
{
    private static TitleCue Title(int id, long start, long end, TransformCurve? curve = null, long entrance = 0, long exit = 0) =>
        new(Id(id), "Title " + id, T(start), T(end), "static", entrance, exit, Transform: curve);

    private static EditingSession Annotated()
    {
        var session = Create(); // V1 holds one 10 s clip at 0
        foreach (var op in new EditOperation[]
        {
            new SetSequenceMarker(SequenceId, new(Id(40), T(1), "before")),
            new SetSequenceMarker(SequenceId, new(Id(41), T(4), "inside")),
            new SetSequenceMarker(SequenceId, new(Id(42), T(7), "after")),
            new SetTitle(SequenceId, Title(50, 6, 8)),
            new SetTitle(SequenceId, Title(51, 2, 4, entrance: T(1).Ticks, exit: T(1).Ticks)),
            new SetTitle(SequenceId, Title(52, 3, 5)),
            new SetTitle(SequenceId, Title(53, 4, 9, new([new(0, X: .1), new(T(2).Ticks, X: .2), new(T(5).Ticks, X: .3)]))),
        }) Assert.True(Apply(session, op).Success);
        return session;
    }

    private static SequenceDocument Sequence(EditingSession session) => Query(session).Sequence(SequenceId);
    private static long Ticks(long seconds) => T(seconds).Ticks;

    [Fact]
    public void RippleDeleteClosesTheGapUnderTitlesAndMarkers()
    {
        var session = Annotated();
        Assert.True(Apply(session, new RippleDelete(SequenceId, [Video1], T(3), T(5), [new(ClipId, Id(60))])).Success);
        var sequence = Sequence(session);

        Assert.Equal(new[] { Ticks(1), Ticks(3), Ticks(5) }, sequence.Markers.Select(m => m.At.Ticks));
        var titles = sequence.Titles.ToDictionary(t => t.Id);
        Assert.False(titles.ContainsKey(Id(52)));                                   // entirely inside the removed range
        Assert.Equal((T(4), T(6)), (titles[Id(50)].Start, titles[Id(50)].End));    // after: moves left
        var tail = titles[Id(51)];                                                  // loses its last second
        Assert.Equal((T(2), T(3)), (tail.Start, tail.End));
        Assert.Equal((Ticks(1), 0L), (tail.EntranceTicks, tail.ExitTicks));         // clamped to the new length
        var head = titles[Id(53)];                                                  // loses its first second
        Assert.Equal((T(3), T(7)), (head.Start, head.End));
        Assert.Equal(new[] { (0L, .1), (Ticks(1), .2), (Ticks(4), .3) }, head.Transform!.Keys.Select(k => (k.At, k.X)));
        Assert.Equal(T(8), sequence.Duration);
    }

    [Fact]
    public void RippleDeleteIsOneUndoStepIncludingAnnotations()
    {
        var session = Annotated();
        var before = Sequence(session);
        Assert.True(Apply(session, new RippleDelete(SequenceId, [Video1], T(3), T(5), [new(ClipId, Id(60))])).Success);
        Assert.True(session.Undo(Request(session)).Success);
        var restored = Sequence(session);
        Assert.Equal(before.Markers, restored.Markers);
        Assert.Equal(before.Titles, restored.Titles);
    }

    [Fact]
    public void AudioOnlyRippleAndMagneticLiftsLeaveAnnotationsInPlace()
    {
        var session = Annotated();
        var audio = new TimelineClip(Id(70), AssetId, 1, T(0), new(new(0, 1, 48_000)), new(new(480_000, 1, 48_000)), new("A"));
        Assert.True(Apply(session, new InsertClip(SequenceId, Audio, audio)).Success);
        var before = Sequence(session);
        Assert.True(Apply(session, new RippleDelete(SequenceId, [Audio], T(3), T(5), [new(Id(70), Id(71))])).Success);
        Assert.Equal(before.Markers, Sequence(session).Markers);
        Assert.Equal(before.Titles, Sequence(session).Titles);
        Assert.True(Apply(session, new RippleDelete(SequenceId, [Video1], T(3), T(5), [new(ClipId, Id(72))], CarryAnnotations: false)).Success);
        Assert.Equal(before.Titles, Sequence(session).Titles);
    }

    [Fact]
    public void InsertShiftMovesLaterAnnotationsAndStretchesSpanningTitles()
    {
        var session = Annotated();
        Assert.True(Apply(session, new ShiftSequenceAnnotations(SequenceId, T(4), T(3))).Success);
        var sequence = Sequence(session);
        Assert.Equal(new[] { Ticks(1), Ticks(7), Ticks(10) }, sequence.Markers.Select(m => m.At.Ticks)); // marker at the point moves
        var titles = sequence.Titles.ToDictionary(t => t.Id);
        Assert.Equal((T(2), T(4)), (titles[Id(51)].Start, titles[Id(51)].End));   // ends exactly at the point: unchanged
        Assert.Equal((T(3), T(8)), (titles[Id(52)].Start, titles[Id(52)].End));   // spans it: stretched
        Assert.Equal((T(7), T(12)), (titles[Id(53)].Start, titles[Id(53)].End));  // starts at it: moved whole
        Assert.Equal(new[] { 0L, Ticks(2), Ticks(5) }, titles[Id(53)].Transform!.Keys.Select(k => k.At));
        Assert.False(Apply(session, new ShiftSequenceAnnotations(SequenceId, T(1), new(0))).Success);
    }
}
