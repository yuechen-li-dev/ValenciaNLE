using System.Collections.Immutable;
using Valencia.App.Editing;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Media;
using Xunit;

namespace Valencia.Tests;

/// <summary>Premiere-style edits (linked delete, Q/W, lift/extract, insert/overwrite) composed from Core operations.</summary>
public sealed class PremiereEditTests
{
    private static SequenceTime T(double seconds) => new((long)Math.Round(seconds * SequenceTime.TicksPerSecond));
    private static readonly MediaTimestamp Zero = new(0, 1, 90000);

    // V1/V2/A1/A2 as created by EditorDocument.New; V1 and A1 receive linked A/V clips of one asset.
    private static (EditorDocument Editor, MediaAsset Asset) Fixture(params (double Start, double Length)[] pairs)
    {
        var editor = new EditorDocument();
        var asset = new MediaAsset(Guid.NewGuid(), "Recording", Path.GetFullPath(Path.Combine(Path.GetTempPath(), "premiere-inert.mkv")), "fixture",
            [new AssetStream(0, TrackKind.Video, "h264", 1, 90000, Zero, new(90000 * 600L, 1, 90000)),
             new AssetStream(1, TrackKind.Audio, "aac", 1, 90000, Zero, new(90000 * 600L, 1, 90000))]);
        var sequence = editor.Sequence; var tracks = sequence.Tracks;
        double source = 0;
        var video = ImmutableArray.CreateBuilder<TimelineClip>(); var audio = ImmutableArray.CreateBuilder<TimelineClip>();
        foreach (var (start, length) in pairs)
        {
            var sourceIn = new SourcePosition(Zero).Shift(T(source)); var sourceOut = sourceIn.Shift(T(length));
            video.Add(new(Guid.NewGuid(), asset.Id, 0, T(start), sourceIn, sourceOut, new("v")));
            audio.Add(new(Guid.NewGuid(), asset.Id, 1, T(start), sourceIn, sourceOut, new("a")));
            source += length + 10; // distinct source ranges per pair
        }
        tracks = tracks.SetItem(0, tracks[0] with { Clips = video.ToImmutable() }).SetItem(2, tracks[2] with { Clips = audio.ToImmutable() });
        var document = editor.Session.Snapshot.Document with { Assets = [asset], Sequences = [sequence with { Tracks = tracks }] };
        editor.Open(new(document, 0), "premiere.valencia");
        return (editor, asset);
    }

    private static TimelineTrack Track(EditorDocument editor, string name) => editor.Sequence.Tracks.First(t => t.Name == name);
    private static long[] Starts(EditorDocument editor, string name) => [.. Track(editor, name).Clips.Select(c => c.Start.Ticks)];

    [Fact]
    public void RippleDeleteRemovesLinkedAudioAndKeepsTracksInSync()
    {
        var (editor, _) = Fixture((0, 4), (4, 4), (8, 4));
        editor.SelectedClip = Track(editor, "V1").Clips[1].Id;
        Assert.True(editor.Delete(ripple: true)!.Success);
        Assert.Equal(new long[] { T(0).Ticks, T(4).Ticks }, Starts(editor, "V1"));
        Assert.Equal(Starts(editor, "V1"), Starts(editor, "A1"));
        Assert.Equal(T(8), editor.Sequence.Duration);
    }

    [Fact]
    public void PlainDeleteRemovesLinkedPairButLeavesGap()
    {
        var (editor, _) = Fixture((0, 4), (4, 4));
        editor.SelectedClip = Track(editor, "A1").Clips[0].Id;
        Assert.True(editor.Delete(ripple: false)!.Success);
        Assert.Equal(new long[] { T(4).Ticks }, Starts(editor, "V1"));
        Assert.Equal(new long[] { T(4).Ticks }, Starts(editor, "A1"));
    }

    [Fact]
    public void LinkUsesSourceInterceptNotIdenticalEdges()
    {
        var (editor, _) = Fixture((0, 4));
        var video = Track(editor, "V1").Clips[0]; var audio = Track(editor, "A1").Clips[0];
        Assert.True(EditorEdits.LinkedTo(video, audio));
        // OBS-style audio that starts 21 ms earlier on the same source clock is still linked.
        var early = audio with { Start = T(0), SourceIn = audio.SourceIn, SourceOut = audio.SourceOut.Shift(T(0.021)) };
        var shiftedVideo = video with { Start = T(0.021), SourceIn = video.SourceIn.Shift(T(0.021)) };
        Assert.True(EditorEdits.LinkedTo(shiftedVideo, early));
        // Same asset placed out of sync is not linked.
        Assert.False(EditorEdits.LinkedTo(video with { Start = T(1) }, audio));
    }

    [Fact]
    public void RippleDeleteRefusesToDesyncUnrelatedMediaUntilTrackIsLocked()
    {
        var (editor, asset) = Fixture((0, 4), (4, 4));
        var music = new TimelineClip(Guid.NewGuid(), asset.Id, 1, T(0), new SourcePosition(Zero).Shift(T(100)), new SourcePosition(Zero).Shift(T(108)), new("music"));
        var a2 = Track(editor, "A2");
        Assert.True(editor.Commit(new Valencia.Core.Editing.InsertClip(editor.SequenceId, a2.Id, music)).Success);
        editor.SelectedClip = Track(editor, "V1").Clips[0].Id;
        long revision = editor.Session.Snapshot.Revision;
        Assert.Null(editor.Delete(ripple: true));
        Assert.Contains("A2", editor.Status);
        Assert.Equal(revision, editor.Session.Snapshot.Revision);
        Assert.True(editor.Commit(new Valencia.Core.Editing.SetTrackState(editor.SequenceId, a2.Id, new(Locked: true))).Success);
        Assert.True(editor.Delete(ripple: true)!.Success);
        Assert.Equal(new long[] { T(0).Ticks }, Starts(editor, "V1"));
        Assert.Equal(new long[] { T(0).Ticks }, Starts(editor, "A2"));
    }

    [Fact]
    public void QAndWRippleTrimAllUnlockedTracksToThePlayhead()
    {
        var (editor, _) = Fixture((0, 4), (4, 4));
        editor.Playhead = T(5);
        Assert.True(EditorEdits.RippleTrim(editor, previous: true)!.Success); // removes [4, 5)
        Assert.Equal(T(4), editor.Playhead);
        Assert.Equal(new long[] { T(0).Ticks, T(4).Ticks }, Starts(editor, "V1"));
        Assert.Equal(T(7), editor.Sequence.Duration);
        editor.Playhead = T(6);
        Assert.True(EditorEdits.RippleTrim(editor, previous: false)!.Success); // removes [6, 7)
        Assert.Equal(T(6), editor.Sequence.Duration);
        Assert.Equal(Starts(editor, "V1"), Starts(editor, "A1"));
        Assert.All(new[] { "V1", "A1" }, name => Assert.Equal(T(2).Ticks, Track(editor, name).Clips[1].Duration.Ticks));
    }

    [Fact]
    public void ExtractRipplesAndLiftLeavesGapBetweenMarks()
    {
        var (editor, _) = Fixture((0, 10));
        editor.MarkIn = T(2); editor.MarkOut = T(5);
        Assert.True(EditorEdits.RemoveMarked(editor, extract: false)!.Success);
        Assert.Equal(new long[] { T(0).Ticks, T(5).Ticks }, Starts(editor, "V1"));
        Assert.Equal(T(10), editor.Sequence.Duration);
        Assert.Null(editor.MarkIn);
        editor.MarkIn = T(5); editor.MarkOut = T(7);
        Assert.True(EditorEdits.RemoveMarked(editor, extract: true)!.Success);
        Assert.Equal(T(8), editor.Sequence.Duration);
        Assert.Equal(Starts(editor, "V1"), Starts(editor, "A1"));
    }

    [Fact]
    public void InsertSplitsAndShiftsWhileOverwriteReplaces()
    {
        var (editor, asset) = Fixture((0, 10));
        var imported = new ImportedAsset(asset with { Streams = [.. asset.Streams.Select(s => s with { End = new(90000 * 3L, 1, 90000) })] }, T(3), Zero);
        editor.Playhead = T(4);
        Assert.True(EditorEdits.InsertAt(editor, imported, overwrite: false).Success);
        Assert.Equal(T(13), editor.Sequence.Duration);
        Assert.Equal(new long[] { T(0).Ticks, T(4).Ticks, T(7).Ticks }, Starts(editor, "V1"));
        Assert.Equal(Starts(editor, "V1"), Starts(editor, "A1"));
        editor.Playhead = T(1);
        Assert.True(EditorEdits.InsertAt(editor, imported, overwrite: true).Success);
        Assert.Equal(T(13), editor.Sequence.Duration);
        Assert.Equal(new long[] { T(0).Ticks, T(1).Ticks, T(4).Ticks, T(7).Ticks }, Starts(editor, "V1"));
    }

    [Fact]
    public void EditPointNavigationIgnoresLockedTracks()
    {
        var (editor, asset) = Fixture((0, 4), (4, 4));
        var a2 = Track(editor, "A2");
        editor.Commit(new Valencia.Core.Editing.InsertClip(editor.SequenceId, a2.Id,
            new TimelineClip(Guid.NewGuid(), asset.Id, 1, T(2), new SourcePosition(Zero).Shift(T(200)), new SourcePosition(Zero).Shift(T(201)), new("blip"))));
        Assert.Equal(T(2), EditorEdits.NextEdit(editor.Sequence, T(0)));
        editor.Commit(new Valencia.Core.Editing.SetTrackState(editor.SequenceId, a2.Id, new(Locked: true)));
        Assert.Equal(T(4), EditorEdits.NextEdit(editor.Sequence, T(0)));
        Assert.Equal(T(4), EditorEdits.PreviousEdit(editor.Sequence, T(8)));
        Assert.Null(EditorEdits.NextEdit(editor.Sequence, T(8)));
    }
}
