using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Testing;
using Xunit;
using static Valencia.Testing.TestProject;

namespace Valencia.Core.Tests;

public sealed class CreatorTests
{
    private static CreatorSettings Settings() => new(1,
        [new(Id(201), AssetId, 1, S(1), S(2), "Hello"), new(Id(202), AssetId, 1, S(2), S(3), "world")], new(),
        [new(Id(203), "vertical", 720, 1280, new(30000, 1001), "fill", Framing: [new(T(0), .2, .5), new(T(10), .8, .5)])],
        [new(Audio, -6, [new(T(0), 0), new(T(2), -12), new(T(4), 0)])]);

    [Fact]
    public void CaptionWordBoundariesAreHalfOpenAndFollowMovedAndSplitSource()
    {
        var session = Create();
        Assert.True(Apply(session, new SetCreatorSettings(SequenceId, Settings())).Success);
        CaptionLine? At(Valencia.Core.SequenceTime time)
        {
            var q = Query(session);
            return CaptionEvaluation.At(q.Sequence(SequenceId), q.Evaluate(SequenceId, time));
        }
        Assert.Null(At(T(0)));
        Assert.Equal(0, At(T(1))!.ActiveWord);
        Assert.Equal(0, At(new(T(2).Ticks - 1))!.ActiveWord);
        Assert.Equal(1, At(T(2))!.ActiveWord);
        Assert.Null(At(T(3)));
        Assert.True(Apply(session, new SplitClip(SequenceId, ClipId, T(2), Id(204))).Success);
        Assert.True(Apply(session, new MoveClip(SequenceId, Id(204), Video1, T(20))).Success);
        Assert.Null(At(T(2)));
        Assert.Equal("world", At(T(20))!.Words[0].Text);
        Assert.Equal(0, At(T(20))!.ActiveWord);
        Assert.Null(At(T(21)));
    }

    [Fact]
    public void CreatorAndEffectEditsUndoRedoAndRejectMalformedStateAtomically()
    {
        var session = Create(); var before = session.Snapshot;
        Assert.True(Apply(session, new SetCreatorSettings(SequenceId, Settings())).Success);
        Assert.True(Apply(session, new SetClipProperties(SequenceId, ClipId, new(Video: new(Exposure: 1), Audio: new(100, 100, 12)))).Success);
        var after = session.Snapshot.Document;
        Assert.True(session.Undo(Request(session)).Success);
        Assert.True(session.Undo(Request(session)).Success);
        Assert.True(DocumentSemantics.Equal(before.Document, session.Snapshot.Document));
        Assert.True(session.Redo(Request(session)).Success);
        Assert.True(session.Redo(Request(session)).Success);
        Assert.True(DocumentSemantics.Equal(after, session.Snapshot.Document));
        var stable = session.Snapshot;
        Assert.False(Apply(session, new SetCreatorSettings(SequenceId, Settings() with { Words = [Settings().Words[0], Settings().Words[0]] })).Success);
        Assert.False(Apply(session, new SetClipProperties(SequenceId, ClipId, new(Video: new(CropLeft: .5, CropRight: .5)))).Success);
        Assert.Same(stable, session.Snapshot);
    }

    [Fact]
    public void GainAutomationAndFadesHaveExactEndpointPolicies()
    {
        var envelope = Settings().Audio[0].Envelope;
        Assert.Equal(0, CaptionEvaluation.GainAt(envelope, T(0)));
        Assert.Equal(-6, CaptionEvaluation.GainAt(envelope, T(1)));
        Assert.Equal(-12, CaptionEvaluation.GainAt(envelope, T(2)));
        Assert.Equal(-6, CaptionEvaluation.GainAt(envelope, T(3)));
        Assert.Equal(0, CaptionEvaluation.GainAt(envelope, T(5)));
        Assert.Equal(0, CaptionEvaluation.Fade(0, 100, 10, 10));
        Assert.Equal(.5, CaptionEvaluation.Fade(5, 100, 10, 10));
        Assert.Equal(1, CaptionEvaluation.Fade(50, 100, 10, 10));
        Assert.Equal(.5, CaptionEvaluation.Fade(95, 100, 10, 10));
    }
}
