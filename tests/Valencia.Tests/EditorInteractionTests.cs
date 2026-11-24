using System.Diagnostics;
using System.Collections.Immutable;
using Silk.NET.Input;
using Valencia.App.Editing;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Xunit;
using Valencia.Rendering;
using Machina.Core.Styling;
using Machina.Presentation;
using Machina.Runtime.Input;

namespace Valencia.Tests;

public sealed class EditorInteractionTests
{
    [Fact]
    public void TimelineCursorTracksTrimMoveRazorLockAndRulerWithoutMutatingTheDocument()
    {
        var editor = Document();
        var layout = EditorLayout.For(1280, 720, 32);
        editor.Viewport.Configure(layout, editor.Sequence.Tracks.Length);
        var before = editor.Session.Snapshot;
        var clip = editor.Sequence.Tracks[0].Clips[0];
        double left = editor.Viewport.X(clip.Start), y = editor.Viewport.TrackTop + 20;
        Assert.Equal(Machina.Core.Semantics.UiCursor.ResizeHorizontal,
            editor.Viewport.Cursor(editor.Sequence, left + 1, y, layout.RulerY, false));
        Assert.Equal(Machina.Core.Semantics.UiCursor.Move,
            editor.Viewport.Cursor(editor.Sequence, left + 20, y, layout.RulerY, false));
        Assert.Equal(Machina.Core.Semantics.UiCursor.Crosshair,
            editor.Viewport.Cursor(editor.Sequence, left + 20, y, layout.RulerY, true));
        var locked = editor.Sequence with { Tracks = editor.Sequence.Tracks.SetItem(0,
            editor.Sequence.Tracks[0] with { State = new(Locked: true) }) };
        Assert.Equal(Machina.Core.Semantics.UiCursor.NotAllowed,
            editor.Viewport.Cursor(locked, left + 20, y, layout.RulerY, false));
        Assert.Equal(Machina.Core.Semantics.UiCursor.Hand,
            editor.Viewport.Cursor(editor.Sequence, left + 20, layout.RulerY + 2, layout.RulerY, false));
        Assert.Null(editor.Viewport.Cursor(editor.Sequence, 0, 0, layout.RulerY, false));
        Assert.Same(before, editor.Session.Snapshot);
    }

    [Theory]
    [InlineData(390, 844)]
    [InlineData(768, 1024)]
    [InlineData(1280, 720)]
    public void CustomCaptionReservesClientSpaceWithoutOverlappingEditingPanels(int width, int height)
    {
        var layout = EditorLayout.For(width, height, 32);
        Assert.Equal(32, layout.Header.Y);
        Assert.Equal(height, layout.Height);
        Assert.True(layout.Panel.Y >= layout.Header.Y + layout.Header.Height);
        Assert.True(layout.Monitor.Y >= layout.Header.Y + layout.Header.Height);
        Assert.True(layout.RulerY >= layout.Toolbar.Y + layout.Toolbar.Height);
        Assert.True(layout.TrackBottom <= height - EditorLayout.StatusHeight);
    }

    [Theory]
    [InlineData(390, 844, EditorLayoutProfile.VerticalPhone)]
    [InlineData(599, 1000, EditorLayoutProfile.VerticalPhone)]
    [InlineData(600, 1024, EditorLayoutProfile.VerticalTablet)]
    [InlineData(1023, 1280, EditorLayoutProfile.VerticalTablet)]
    [InlineData(1024, 768, EditorLayoutProfile.Desktop)]
    [InlineData(960, 600, EditorLayoutProfile.Desktop)]
    [InlineData(768, 720, EditorLayoutProfile.Desktop)]
    [InlineData(1920, 1080, EditorLayoutProfile.Desktop)]
    internal void ExplicitProfileLawDrivesViewportAndNativeHitTesting(int width, int height, EditorLayoutProfile expected)
    {
        var editor = Document(); var view = new EditorView();
        var prepared = view.Prepare(editor, new(editor), false, false, width, height);
        var layout = view.Layout;
        Assert.Equal(expected, layout.Profile);
        Assert.Equal(width, prepared.PresentationFrame.Viewport.Width);
        Assert.Equal(layout.ClipRight, editor.Viewport.ClipRight);
        Assert.Equal(layout.TrackTop, editor.Viewport.TrackTop);
        Assert.NotNull(editor.Viewport.Hit(editor.Sequence, layout.ClipLeft + 20, layout.TrackTop + 20));
        Assert.Null(editor.Viewport.Hit(editor.Sequence, layout.ClipLeft - 1, layout.TrackTop + 20));
        Assert.Null(editor.Viewport.Hit(editor.Sequence, layout.ClipLeft + 20, layout.TrackBottom));
        // Save is the third header command: a wrapped row under the wordmark on narrow roots, beside it otherwise.
        double gap = EditorLayout.HeaderCommandGap;
        double cell = layout.HeaderRows == 2 ? (width - 2 * EditorLayout.Gap - 5 * gap) / 6.0 : EditorLayout.HeaderCommandWidth;
        double saveX = (layout.HeaderRows == 2 ? EditorLayout.Gap : EditorLayout.HeaderCommandsLeft) + 2 * (cell + gap) + cell / 2;
        double saveY = layout.HeaderRows == 2 ? EditorLayout.HeaderRowHeight + 18 : 20;
        Assert.Equal("Save", prepared.HitTest.HitTest(new PointerPoint(saveX, saveY))?.Action.Name);
        Assert.True(prepared.TryGetActionCenter(Machina.Core.Actions.UiAction.Named("Fullscreen"), out _));

        var texts = prepared.PresentationFrame.Operations.OfType<PositionedTextOperation>().ToArray();
        foreach (var text in texts)
        {
            var measured = NativeUiText.Instance.MeasureText(text.Text, text.Style);
            Assert.True(text.Rect.Height >= measured.Height, $"{text.Text}: {text.Rect.Height} < {measured.Height}");
            Assert.True(text.Rect.Y + text.Rect.Height <= height, $"{text.Text} extends below the window");
        }
        Assert.Contains(texts, t => t.Text == "VALENCIA");
        if (!layout.Portrait) Assert.Contains(texts, t => t.Text.Contains("I/O mark", StringComparison.Ordinal));
        Assert.True(layout.Monitor.Y + layout.Monitor.Height <= layout.Transport.Y && layout.Transport.Y + layout.Transport.Height <= layout.RulerY,
            "Monitor, transport and timeline must not overlap.");
    }

    [Fact]
    public void NativeTextMetricsAndWrappingPreventBitmapSizedLabelClipping()
    {
        var small = NativeUiText.Instance.MeasureText("Ctrl+I import", new TextStyle(Size: TextSize.Sm));
        Assert.True(small.Height > 7); // old bitmap measurer reserved only seven pixels
        var hints = new[] { "Ctrl+I import", "Ctrl+K split", "Del / Shift+Del", "Ctrl+Z undo", "Ctrl+Shift+Z redo",
            "Wheel scroll", "Ctrl+wheel zoom", "Shift+wheel tracks" };
        foreach (double width in new[] { 354d, 732d, 1244d })
        {
            var lines = NativeUiText.WrapHints(hints, width);
            Assert.All(lines, line => Assert.True(NativeUiText.Width(line) <= width));
            Assert.Equal(string.Join(" | ", hints), string.Join(" | ", lines));
        }
        string shortened = NativeUiText.Ellipsize("A lengthy project status that should fit without a partial glyph", 180);
        Assert.EndsWith("...", shortened); Assert.True(NativeUiText.Width(shortened) <= 180);
        Assert.Equal("VALENCIA", NativeUiText.Ellipsize("VALENCIA", 200, TextSize.Xl)); // the header wordmark role
    }

    [Theory]
    [InlineData(390, 844)]
    [InlineData(768, 1024)]
    [InlineData(1920, 1080)]
    public void ProfileAwareDragUsesOneCommitAndKeepsExactLargeOrigin(int width, int height)
    {
        var editor = Document(5000);
        var viewport = editor.Viewport; viewport.Configure(EditorLayout.For(width, height));
        viewport.Scroll(8000 * 90);
        var clip = viewport.Visible(editor.Sequence.Tracks[0]).Skip(1).First();
        var gesture = new TimelineGesture(editor); var before = editor.Session.Snapshot;
        double x = viewport.X(clip.Start) + 15;
        var anchor = viewport.Time(x); viewport.Zoom(1.1, x);
        Assert.InRange(Math.Abs(viewport.Time(x).Ticks - anchor.Ticks), 0, 1);
        x = viewport.X(clip.Start) + 15;
        gesture.Begin(clip, 0, x); gesture.Move(x + 20, viewport.TrackTop + 20);
        Assert.True(gesture.Valid); Assert.Same(before, editor.Session.Snapshot);
        Assert.True(gesture.Release()); Assert.Equal(before.Revision + 1, editor.Session.Snapshot.Revision);
        Assert.True(editor.History(false).Success);
        Assert.True(DocumentSemantics.Equal(before.Document, editor.Session.Snapshot.Document));
        gesture.Begin(clip, 0, x); gesture.Move(x + 10, viewport.TrackBottom + 1);
        Assert.False(gesture.Valid); gesture.Cancel();
    }
    [Fact]
    public void FreshProjectCannotUndoItsRequiredSequenceSetup()
    {
        var editor = new EditorDocument();
        Assert.False(editor.Session.CanUndo);
        Assert.Equal(EditError.NothingToUndo, editor.History(false).Error);
        Assert.Equal(4, editor.Sequence.Tracks.Length);
    }
    private static SequenceTime T(long second) => new(second * SequenceTime.TicksPerSecond);
    private static EditorDocument Document(int count = 1)
    {
        var editor = new EditorDocument(); var stream = new AssetStream(0, TrackKind.Video, "fixture", 1, 90000, new(0, 1, 90000), new(900000, 1, 90000));
        var asset = new MediaAsset(Guid.NewGuid(), "Fixture", Path.GetFullPath(Path.Combine(Path.GetTempPath(), "editor-inert.mp4")), "fixture", [stream]);
        var track = editor.Sequence.Tracks[0];
        var clips = Enumerable.Range(0, count).Select(i => new TimelineClip(Guid.NewGuid(), asset.Id, 0, T(i * 2), new(new(0, 1, 90000)), new(new(90000, 1, 90000)), new())).ToImmutableArray();
        var snapshot = editor.Session.Snapshot;
        var doc = snapshot.Document with { Assets = [asset], Sequences = [editor.Sequence with { Tracks = editor.Sequence.Tracks.SetItem(0, track with { Clips = clips }) }] };
        editor.Open(new(doc, 0), "fixture.valencia"); return editor;
    }
    [Fact]
    public void ViewportKeepsPrecisionAtSixHourOriginAndZoomAnchor()
    {
        var viewport = new TimelineViewport(); viewport.Scroll(21600 * 90);
        var time = new SequenceTime(T(21601).Ticks + 1);
        Assert.InRange(Math.Abs(viewport.Time(viewport.X(time)).Ticks - time.Ticks), 0, 1);
        var anchor = viewport.Time(700); viewport.Zoom(1.5, 700);
        Assert.InRange(Math.Abs(viewport.Time(700).Ticks - anchor.Ticks), 0, 1);
        Assert.Equal(T(0), new TimelineViewport().Time(-100));
        viewport.Reset(); Assert.Equal(T(0), viewport.Origin); Assert.Equal(90, viewport.PixelsPerSecond);
    }
    [Fact]
    public void FiveThousandClipsAreCulledAndHitTestedWithoutPerClipWidgets()
    {
        var editor = Document(5000); editor.Viewport.Scroll(800 * 90);
        var watch = Stopwatch.StartNew(); var visible = editor.Viewport.Visible(editor.Sequence.Tracks[0]).ToArray();
        Assert.InRange(visible.Length, 5, 8);
        Assert.NotNull(editor.Viewport.Hit(editor.Sequence, 180, 420));
        var view = new EditorView(); view.Prepare(editor, new(editor), false, false);
        Assert.Equal(visible.Length, view.VisibleClips);
        Assert.InRange(view.Canvas.Count, 10, 80); // clips, rows, ruler ticks and panel fills; never one quad set per off-screen clip
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2)); // generous failure guard; detailed measurements are recorded by native smoke
    }
    [Fact]
    public void DragPreviewIsTransientAndReleaseCreatesOneUndoEntry()
    {
        var editor = Document(); var clip = editor.Sequence.Tracks[0].Clips[0]; var before = editor.Session.Snapshot;
        var gesture = new TimelineGesture(editor); gesture.Begin(clip, 0, 200); gesture.Move(290, 420);
        Assert.True(gesture.Valid); Assert.Same(before, editor.Session.Snapshot); Assert.True(gesture.Release());
        Assert.Equal(T(1), editor.Sequence.Tracks[0].Clips[0].Start);
        Assert.Equal(before.Revision + 1, editor.Session.Snapshot.Revision);
        Assert.True(editor.History(false).Success); Assert.True(DocumentSemantics.Equal(before.Document, editor.Session.Snapshot.Document));
        Assert.True(editor.History(true).Success);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void CancelAndInvalidLockedDestinationDoNotCommit(bool locked)
    {
        var editor = Document();
        if (locked) editor.Commit(new SetTrackState(editor.SequenceId, editor.Sequence.Tracks[0].Id, new(Locked: true)));
        var before = editor.Session.Snapshot; var gesture = new TimelineGesture(editor);
        gesture.Begin(editor.Sequence.Tracks[0].Clips[0], 0, 200); gesture.Move(290, 420);
        if (locked) { Assert.False(gesture.Valid); Assert.False(gesture.Release()); } else gesture.Cancel();
        Assert.Same(before, editor.Session.Snapshot); Assert.False(gesture.Active);
    }
    [Fact]
    public void TrimAndSelectionAreFrameAlignedAndSourceBounded()
    {
        var editor = Document(); var clip = editor.Sequence.Tracks[0].Clips[0];
        var gesture = new TimelineGesture(editor); gesture.Begin(clip, 1, editor.Viewport.X(clip.End));
        Assert.Equal(clip.Id, editor.SelectedClip);
        gesture.Move(editor.Viewport.X(clip.End) - 17, 420); Assert.True(gesture.Release());
        var trimmed = editor.Selection!;
        Assert.Equal(trimmed.End, TimelineTime.SnapToFrame(trimmed.End, editor.Sequence.Video.FrameRate, TimeRounding.Exact));
        gesture.Begin(trimmed, -1, editor.Viewport.X(trimmed.Start)); gesture.Move(150, 420); Assert.False(gesture.Valid);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void InputManRoutesUndoRedoWithoutDuplicateUnmodifiedCommands(bool shift)
    {
        var keys = new EditorKeymap(); keys.Record(Key.ControlLeft, true); if (shift) keys.Record(Key.ShiftLeft, true);
        keys.Record(Key.Z, true); keys.Record(Key.Z, false);
        Assert.Equal([shift ? EditorAction.Redo : EditorAction.Undo], keys.Read(.016));
        keys.Reset(); Assert.Empty(keys.Read(.016));
    }
    [Fact]
    public void RelinkUsesDispatcherAndValidatesExistingSourceBounds()
    {
        var editor = Document(); var original = editor.Session.Snapshot.Document.Assets[0];
        var replacement = original with { SourcePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "replacement.mp4")) };
        Assert.True(editor.Commit(new RelinkAsset(replacement)).Success);
        Assert.True(editor.History(false).Success); Assert.Equal(original, editor.Session.Snapshot.Document.Assets[0]);
        var before = editor.Session.Snapshot;
        Assert.False(editor.Commit(new RelinkAsset(replacement with { Streams = [original.Streams[0] with { End = new(1, 1, 90000) }] })).Success);
        Assert.Same(before, editor.Session.Snapshot);
    }
}
