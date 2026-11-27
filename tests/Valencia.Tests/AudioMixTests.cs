using Machina.Core.Actions;
using Valencia.App.Editing;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Media;
using Xunit;

namespace Valencia.Tests;

public sealed class AudioMixTests(TimelineFixture fixture) : IClassFixture<TimelineFixture>
{
    [Fact]
    public void FaderScalePutsUnityAtTheEngravedDetentAndRoundTrips()
    {
        Assert.Equal(.85, MeterScale.Fader(0), 9);
        Assert.Equal(0, MeterScale.Fader(MeterScale.Floor));
        Assert.Equal(1, MeterScale.Fader(MeterScale.FaderTop));
        foreach (double db in new[] { -54.0, -30, -12, -6, 0, 3, 12 })
        {
            Assert.Equal(db, MeterScale.FaderDb(MeterScale.Fader(db)), 6);
            if (db <= 0) Assert.Equal(db, MeterScale.FullDb(MeterScale.Full(db)), 6); // dBFS tops out at full scale
        }
        Assert.Equal(1, MeterScale.Full(3));
        Assert.Equal(MeterScale.Floor, MeterScale.Db(0));
        Assert.Equal(-6.0206, MeterScale.Db(.5), 3);
    }

    [Fact]
    public void LouderIsBrighterAndOnlyAnOverIsAlert()
    {
        Assert.Equal(EditorView.Ui.Mute, MeterScale.Zone(-30, 0));
        Assert.Equal(EditorView.Ui.Bone, MeterScale.Zone(-12, 0));
        Assert.Equal(EditorView.Ui.Card, MeterScale.Zone(-3, 0));
        Assert.Equal(EditorView.Ui.Alert, MeterScale.Zone(0, 0));
        Assert.Equal(EditorView.Ui.Alert, MeterScale.Zone(-1, -1));
    }

    [Fact]
    public void MeterAttacksInstantlyReleasesHoldsAndLatchesOvers()
    {
        var channel = new MeterChannel();
        channel.Observe(.5, .25, 0, now: 0, dt: .05);
        Assert.Equal(-6.02, channel.PeakDb, 2);
        Assert.Equal(channel.PeakDb, channel.HoldDb);
        channel.Observe(0, 0, 0, now: .5, dt: .5);
        Assert.Equal(-6.02 - MeterChannel.ReleaseDbPerSecond * .5, channel.PeakDb, 2);
        Assert.Equal(-6.02, channel.HoldDb, 2); // still inside the hold window
        channel.Observe(0, 0, 0, now: 2, dt: .5);
        Assert.True(channel.HoldDb < -6.03);
        Assert.False(channel.Over);
        channel.Observe(1, .7, 0, now: 2.1, dt: .1);
        Assert.True(channel.Over);
        for (int i = 0; i < 40; i++) channel.Observe(0, 0, 0, now: 2.2 + i * .25, dt: .25);
        Assert.True(channel.Over); // latched until the user clears it
        Assert.False(channel.Live);
    }

    [Fact]
    public void MasterOverIsTheClipperCeilingAndClearingResetsEveryLamp()
    {
        var meter = new AudioMeter();
        var id = Guid.NewGuid();
        meter.Observe(new AudioOutputLevels(512, .89f, .5f, .3f, .2f), ceilingDb: -1, new Dictionary<Guid, (double, double)> { [id] = (1.2, .5) }, now: 0);
        Assert.True(meter.Left.Over);  // -1.0 dBFS reached the -1 dB ceiling
        Assert.False(meter.Right.Over);
        Assert.True(meter.Track(id)!.Over); // channel estimate past 0 dBFS
        meter.ClearOver();
        Assert.False(meter.Over);
        meter.Pinned = true;
        meter.Observe(default, -1, new Dictionary<Guid, (double, double)>(), now: 1);
        Assert.True(meter.Left.PeakDb > -2);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(768, 1024)]
    public void MixTabExposesEveryStripAndRegistersFadersInsideThePanel(int width, int height)
    {
        var editor = new EditorDocument();
        editor.Open(fixture.Snapshot, "fixture.valencia");
        editor.MixMode = true;
        var view = new EditorView();
        var frame = view.Prepare(editor, new(editor), false, false, width, height);
        var audio = editor.Sequence.Tracks.Where(t => t.Kind == TrackKind.Audio).ToArray();
        Assert.NotEmpty(audio);
        var first = audio[0].Id;
        foreach (string action in new[] { "Solo:" + first, "MixMute:" + first, "MixGain+:" + first, "MixGain-:" + first, "MixUnity:" + first,
                     "MixUnity:master", "MixGain+:master", "ClearOver" })
        {
            Assert.True(frame.TryGetActionCenter(UiAction.Named(action), out var center), action);
            Assert.Equal(action, frame.HitTest.HitTest(center)?.Action.Name);
        }
        Assert.True(editor.MixFaders.ContainsKey("master"));
        Assert.True(editor.MixFaders.ContainsKey(first.ToString()));
        var panel = view.Layout.Panel;
        foreach (var rect in editor.MixFaders.Values)
        {
            Assert.InRange(rect.X, panel.X, panel.X + panel.Width);
            Assert.InRange(rect.X + rect.Width, panel.X, panel.X + panel.Width);
            Assert.InRange(rect.Y + rect.Height, panel.Y, panel.Y + panel.Height);
            Assert.True(rect.Width > 60);
        }
        editor.MixMode = false;
        view.Prepare(editor, new(editor), false, false, width, height);
        Assert.Empty(editor.MixFaders); // a hidden tab leaves no fader to grab
    }

    [Fact]
    public void DesktopDocksTheEdgeMeterAndPortraitDoesNot()
    {
        var desktop = EditorLayout.For(1280, 720);
        Assert.Equal(1280 - EditorLayout.Gap - EditorLayout.EdgeMeterWidth, desktop.ClipRight);
        Assert.True(desktop.HasEdgeMeter);
        var phone = EditorLayout.For(390, 844);
        Assert.Equal(390 - EditorLayout.Gap, phone.ClipRight);
        Assert.False(phone.HasEdgeMeter);
    }

    [Fact]
    public void AudioClipInspectorHasFadesCleanupAndTheUnwiredVoiceEq()
    {
        var editor = new EditorDocument();
        editor.Open(fixture.Snapshot, "fixture.valencia");
        var clip = editor.Sequence.Tracks.First(t => t.Kind == TrackKind.Audio && t.Clips.Length > 0).Clips[0];
        editor.SelectedClip = clip.Id;
        var frame = new EditorView().Prepare(editor, new(editor), false, false, 1920, 1080);
        foreach (string action in new[] { "fx:clipGain+", "fx:fadeIn+", "fx:fadeIn-", "fx:fadeOut+", "fx:cleanup+", "fx:cleanup-", "EqNotWired" })
        {
            Assert.True(frame.TryGetActionCenter(UiAction.Named(action), out var center), action);
            Assert.Equal(action, frame.HitTest.HitTest(center)?.Action.Name);
        }
    }
}
