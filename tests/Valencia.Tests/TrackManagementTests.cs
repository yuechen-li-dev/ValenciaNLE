using Valencia.App.Editing;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Media;
using Xunit;

namespace Valencia.Tests;

public sealed class TrackManagementTests(TimelineFixture fixture) : IClassFixture<TimelineFixture>
{
    private static SequenceTime F(long frames) => TimelineFixture.F(frames);

    private EditorDocument Editor()
    {
        var editor = new EditorDocument();
        editor.Open(fixture.Snapshot, "fixture.valencia");
        return editor;
    }

    [Fact]
    public void ADropOnATrackPlacesLinkedStreamsFromThatTrackDown()
    {
        var editor = Editor();
        var imported = MediaAssetProbe.Import(TimelineFixture.Id(2), fixture.A);
        var onA2 = EditorEdits.Plan(editor, imported, F(120), TimelineFixture.Id(8)).OfType<InsertClip>().ToArray();
        Assert.Contains(onA2, i => i.TrackId == TimelineFixture.Id(8)); // audio lands where it was dropped
        Assert.Contains(onA2, i => i.TrackId == TimelineFixture.Id(5)); // its video goes to the first video track
        Assert.All(onA2, i => Assert.Equal(F(120), i.Clip.Start));
        var onV2 = EditorEdits.Plan(editor, imported, F(120), TimelineFixture.Id(6)).OfType<InsertClip>().ToArray();
        Assert.Contains(onV2, i => i.TrackId == TimelineFixture.Id(6));
        Assert.Same(fixture.Snapshot, editor.Session.Snapshot); // planning a drag preview never edits
    }

    [Fact]
    public void DroppingOverwritesAtTheDropPointWithoutMovingThePlayhead()
    {
        var editor = Editor();
        var imported = MediaAssetProbe.Import(TimelineFixture.Id(2), fixture.A);
        var result = EditorEdits.InsertAt(editor, imported, overwrite: true, at: F(150), target: TimelineFixture.Id(6));
        Assert.True(result.Success, result.Message);
        var v2 = editor.Sequence.Tracks.Single(t => t.Id == TimelineFixture.Id(6));
        Assert.Contains(v2.Clips, c => c.Start == F(150) && c.AssetId == imported.Asset.Id);
        Assert.True(editor.History(false).Success); // one undo step
        Assert.True(DocumentSemantics.Equal(fixture.Snapshot.Document, editor.Session.Snapshot.Document));
    }

    [Fact]
    public void TheScrollbarAppearsOnlyWhenTracksOverflowAndTracksFirstTrack()
    {
        var editor = Editor();
        var view = new EditorView();
        view.Prepare(editor, new(editor), false, false, 1280, 720);
        Assert.Null(editor.TrackScrollbar);
        for (int i = 0; i < 20; i++)
        {
            var kind = i % 2 == 0 ? TrackKind.Video : TrackKind.Audio;
            Assert.True(editor.Commit(new AddTrack(editor.SequenceId, Guid.NewGuid(), kind, TrackLayout.NewTrackOrder(editor.Sequence.Tracks, kind), "T")).Success);
        }
        // Video stays grouped above audio however tracks are added.
        var kinds = editor.Sequence.Tracks.Select(t => t.Kind).ToArray();
        Assert.Equal(kinds.OrderBy(k => k == TrackKind.Video ? 0 : 1), kinds);

        view.Prepare(editor, new(editor), false, false, 1280, 720);
        var (groove, top) = editor.TrackScrollbar!.Value;
        Assert.True(top.Height < groove.Height);
        Assert.Equal(groove.Y, top.Y, 3);
        Assert.InRange(groove.X + groove.Width, view.Layout.ClipRight - .5, view.Layout.ClipRight + .5);
        editor.Viewport.FirstTrack = editor.Sequence.Tracks.Length - editor.Viewport.VisibleTracks;
        view.Prepare(editor, new(editor), false, false, 1280, 720);
        var (_, bottom) = editor.TrackScrollbar!.Value;
        Assert.Equal(groove.Y + groove.Height, bottom.Y + bottom.Height, 3);
    }
}
