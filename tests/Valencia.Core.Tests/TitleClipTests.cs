using System.Collections.Immutable;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Xunit;
using static Valencia.Testing.TestProject;

namespace Valencia.Core.Tests;

/// <summary>Titles are clips on video tracks: placement, editing, splitting, effects and the old title layer.</summary>
public sealed class TitleClipTests
{
    private static SequenceDocument Sequence(EditingSession session) => Query(session).Sequence(SequenceId);
    private static (TimelineTrack Track, TimelineClip Clip) Find(EditingSession session, Guid id) =>
        Sequence(session).Tracks.SelectMany(t => t.Clips.Select(c => (t, c))).Single(p => p.c.Id == id);

    [Fact]
    public void NewTitlesTakeTheHighestFreeVideoTrackThenANewOne()
    {
        var session = Create(); // V1 has footage 0..10 s, V2 is empty
        Assert.True(Apply(session, new SetTitle(SequenceId, new(Id(50), "ONE", T(1), T(3), Style: TitleStyles.Card))).Success);
        Assert.Equal(Video2, Find(session, Id(50)).Track.Id);
        Assert.True(Apply(session, new SetTitle(SequenceId, new(Id(51), "TWO", T(2), T(4), Style: TitleStyles.Card))).Success);
        var (track, clip) = Find(session, Id(51));
        Assert.Equal(TrackKind.Video, track.Kind);
        Assert.Equal(2, track.Order);                        // a new video track above V2, below the audio
        Assert.Equal("V3", track.Name);
        Assert.Equal(TitleClips.AssetId, clip.AssetId);
        Assert.Equal(TitleStyles.Card, clip.Title!.Style);
        Assert.Equal(T(4), clip.End);
        // Deterministic: replaying the same command on a fresh session derives the same track ID.
        var again = Create();
        Apply(again, new SetTitle(SequenceId, new(Id(50), "ONE", T(1), T(3))));
        Apply(again, new SetTitle(SequenceId, new(Id(51), "TWO", T(2), T(4))));
        Assert.Equal(track.Id, Find(again, Id(51)).Track.Id);
    }

    [Fact]
    public void TitlesEditTrimSplitAndRefuseFootageEffects()
    {
        var session = Create();
        Assert.True(Apply(session, new SetTitle(SequenceId, new(Id(50), "HELLO", T(2), T(8), "fade", T(1).Ticks, T(2).Ticks,
            Style: TitleStyles.LowerThird, Subtitle: "Guest"))).Success);
        var content = Find(session, Id(50)).Clip.Title!;
        Assert.True(Apply(session, new SetTitleContent(SequenceId, Id(50), content with { Text = "HELLO THERE" })).Success);
        Assert.Equal("HELLO THERE", Find(session, Id(50)).Clip.Title!.Text);
        Assert.Equal(T(2), Find(session, Id(50)).Clip.Start);

        Assert.True(Apply(session, new SplitClip(SequenceId, Id(50), T(5), Id(52))).Success);
        var left = Find(session, Id(50)).Clip; var right = Find(session, Id(52)).Clip;
        Assert.Equal((T(1).Ticks, 0L), (left.Title!.EntranceTicks, left.Title.ExitTicks));   // continuous: entrance stays left
        Assert.Equal((0L, T(2).Ticks), (right.Title!.EntranceTicks, right.Title.ExitTicks)); // exit stays right
        Assert.Equal(T(3).Ticks, TitleClips.LocalTicks(right, T(5)));                        // one clock across the split

        var before = session.Snapshot;
        Assert.False(Apply(session, new SetClipSpeed(SequenceId, Id(50), 2, 1)).Success);
        Assert.False(Apply(session, new PatchClipColor(SequenceId, Id(50), new(Contrast: 1.2))).Success);
        Assert.Same(before, session.Snapshot);
        Assert.True(Apply(session, new SetClipProperties(SequenceId, Id(50), left.Properties with { Opacity = .5m })).Success);
        Assert.True(Apply(session, new RemoveTitle(SequenceId, Id(52))).Success);
        Assert.Single(Sequence(session).Titles);
    }

    [Fact]
    public void ValidatorRejectsMalformedTitleClips()
    {
        var session = Create();
        var bad = TitleClips.Create(Id(50), T(1), T(2), new TitleContent("OK", "neon"));
        Assert.False(Apply(session, new InsertClip(SequenceId, Video2, bad)).Success);
        var noContent = TitleClips.Create(Id(51), T(1), T(2), new("OK")) with { Title = null };
        Assert.False(Apply(session, new InsertClip(SequenceId, Video2, noContent)).Success);
        var onAudio = TitleClips.Create(Id(52), T(1), T(2), new("OK"));
        Assert.False(Apply(session, new InsertClip(SequenceId, Audio, onAudio)).Success);
        Assert.False(Apply(session, new AddAsset(Asset() with { Id = TitleClips.AssetId })).Success);
        Assert.True(Apply(session, new InsertClip(SequenceId, Video2, TitleClips.Create(Id(53), T(1), T(2), new("OK")))).Success);
    }

    [Fact]
    public void OldTitleLayerBecomesClipsOnNewTracksDeterministically()
    {
        var document = Create().Snapshot.Document;
        var cues = ImmutableArray.Create(
            new TitleCue(Id(60), "A", T(1), T(3), "pop", T(1).Ticks, 0, X: .3, Transform: new([new(0, X: .2)])),
            new TitleCue(Id(61), "B", T(2), T(4)),     // overlaps A: second lane
            new TitleCue(Id(62), "C", T(3), T(5)));    // fits after A: first lane
        var legacy = document with { Sequences = [document.Sequences[0] with { LegacyTitles = cues }] };
        var upgraded = TitleMigration.Upgrade(legacy);
        DocumentValidator.Validate(upgraded);
        var sequence = upgraded.Sequences[0];
        Assert.True(sequence.LegacyTitles.IsEmpty);
        Assert.Equal(new[] { "V1", "V2", "V3", "V4", "A1" }, sequence.Tracks.Select(t => t.Name));
        Assert.Equal(new[] { Id(60), Id(62) }, sequence.Tracks[2].Clips.Select(c => c.Id));
        Assert.Equal(new[] { Id(61) }, sequence.Tracks[3].Clips.Select(c => c.Id));
        var a = sequence.Tracks[2].Clips[0];
        Assert.Equal((TitleStyles.Outline, "pop", .3), (a.Title!.Style, a.Title.Motion, a.Title.X));   // old look kept
        Assert.Equal(cues[0] with { TrackId = sequence.Tracks[2].Id }, sequence.Titles.Single(t => t.Id == Id(60)));
        Assert.True(DocumentSemantics.Equal(upgraded, TitleMigration.Upgrade(legacy)));
        Assert.Throws<ArgumentException>(() => DocumentValidator.Validate(legacy));
    }
}
