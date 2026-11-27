using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Machina.Core.Actions;
using Valencia.Analysis;
using Valencia.App.Editing;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Media;
using Valencia.Persistence;
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
        meter.Observe(new(new(512, .89f, .5f, .3f, .2f), new Dictionary<Guid, AudioOutputLevels>
        { [id] = new(512, 1.2f, .5f, .5f, .25f) }, -18.3), ceilingDb: -1, now: 0);
        Assert.Equal(-18.3, meter.MomentaryLufs);
        Assert.True(meter.Left.Over);  // -1.0 dBFS reached the -1 dB ceiling
        Assert.False(meter.Right.Over);
        Assert.True(meter.Track(id)!.Over);
        meter.ClearOver();
        Assert.False(meter.Over);
        meter.Pinned = true;
        meter.Observe(AudioMeterReading.Empty, -1, now: 1);
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
                     "MixUnity:master", "MixGain+:master", "ClearOver", "NormalizeMix", "ListenMix" })
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
    public void AudioClipInspectorHasFadesCleanupAndWorkingEqActions()
    {
        var editor = new EditorDocument();
        editor.Open(fixture.Snapshot, "fixture.valencia");
        var clip = editor.Sequence.Tracks.First(t => t.Kind == TrackKind.Audio && t.Clips.Length > 0).Clips[0];
        editor.SelectedClip = clip.Id;
        var frame = new EditorView().Prepare(editor, new(editor), false, false, 1920, 1080);
        foreach (string action in new[] { "fx:clipGain+", "fx:fadeIn+", "fx:fadeIn-", "fx:fadeOut+", "fx:cleanup+", "fx:cleanup-",
                     "fx:eqLow-", "fx:eqLow0", "fx:eqMid+", "fx:eqMid0", "fx:eqHigh+", "fx:eqHigh0" })
        {
            Assert.True(frame.TryGetActionCenter(UiAction.Named(action), out var center), action);
            Assert.Equal(action, frame.HitTest.HitTest(center)?.Action.Name);
        }
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public async Task MomentaryLoudnessMatchesFfmpegEbuR128(int rate)
    {
        var pcm = new float[rate * 2];
        for (int frame = 0; frame < rate; frame++)
            pcm[2 * frame] = pcm[2 * frame + 1] = (float)(.1 * Math.Sin(2 * Math.PI * 997 * frame / rate));
        var meter = new MomentaryLoudness(rate);
        Assert.Null(meter.Observe(pcm.AsSpan(0, rate / 5 * 2)));
        double? actual = meter.Observe(pcm.AsSpan(rate / 5 * 2));
        Assert.NotNull(actual);
        byte[] metadata = await TimelineFixture.Run("-f", "lavfi", "-i",
            $"aevalsrc=0.1*sin(2*PI*997*t)|0.1*sin(2*PI*997*t):s={rate}:d=1",
            "-af", "ebur128=metadata=1,ametadata=print:key=lavfi.r128.M:file=-", "-f", "null", "NUL");
        string last = Encoding.UTF8.GetString(metadata).Split('\n').Last(line => line.StartsWith("lavfi.r128.M="));
        double expected = double.Parse(last.Split('=')[1], CultureInfo.InvariantCulture);
        Assert.InRange(Math.Abs(actual.Value - expected), 0, .05);
    }

    [Fact]
    public async Task TrackMetersUseActualSamplesAcrossQueueBoundariesAndBeforeMonitorGain()
    {
        var buffer = new AudioStreamBuffer { Gain = .5f };
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await buffer.WriteMonitoredAsync(new([.5f, -.25f, .25f, -.5f],
            new Dictionary<Guid, float[]> { [first] = [.5f, -.25f, .25f, -.5f] }), default);
        await buffer.WriteMonitoredAsync(new([.75f, .2f],
            new Dictionary<Guid, float[]> { [second] = [.75f, .2f] }), default);
        byte[] bytes = new byte[4 * 2 * sizeof(float)];
        Assert.Equal(bytes.Length, buffer.Read(bytes));
        Assert.Equal(new[] { .25f, -.125f, .125f, -.25f, .375f, .1f, 0, 0 }, MemoryMarshal.Cast<byte, float>(bytes).ToArray());
        var reading = buffer.MeterReading;
        Assert.Equal(.375f, reading.Output.LeftPeak);
        Assert.Equal(.5f, reading.Tracks[first].LeftPeak);
        Assert.Equal(.75f, reading.Tracks[second].LeftPeak);
        Assert.Equal(.375f, reading.Tracks[second].LeftRms);
        Assert.Equal(4, reading.Tracks[first].Frames);
    }

    [Fact]
    public async Task ClipEqSurvivesSaveAndBypassChangesOnlyTheMonitoredSignal()
    {
        var snapshot = fixture.Snapshot;
        var sequence = snapshot.Document.Sequences[0];
        var track = sequence.Tracks.First(item => item.Kind == TrackKind.Audio);
        var clip = track.Clips[0];
        var session = new EditingSession(snapshot);
        var eq = new VoiceEq(LowDb: -6, MidDb: 12, HighDb: 3);
        var operation = new SetClipProperties(sequence.Id, clip.Id, clip.Properties with { Audio = new(Eq: eq) });
        Assert.True(session.Execute(new EditCommand(Guid.NewGuid(), 1, operation, snapshot.Revision)).Success);
        var file = new ProjectFile();
        string path = Path.Combine(fixture.Root, "voice-eq.valencia");
        await file.SaveAsync(path, session);
        var reopened = (await file.LoadAsync(path)).Snapshot;
        Assert.Equal(eq, new TimelineQueries(reopened).Clips(sequence.Id, track.Id)[0].Properties.Audio?.Eq);
        using var original = new SequenceEvaluator(snapshot, sequence.Id);
        using var processed = new SequenceEvaluator(reopened, sequence.Id);
        using var audition = new SequenceEvaluator(reopened, sequence.Id);
        const int frames = 4096;
        var dry = original.Audio(default, 0, frames, default);
        var wet = processed.Audio(default, 0, frames, default);
        var before = audition.MonitorAudio(default, 0, frames, default, [], bypassClipProcessing: true, reverse: false);
        Assert.False(wet.SequenceEqual(dry));
        Assert.Equal(dry, before.Samples);
        Assert.Equal(dry, before.Tracks[track.Id]);
        Assert.Equal(wet, processed.Audio(default, 0, frames, default));

        string export = Path.Combine(fixture.Root, "voice-eq.mkv");
        await SequenceExport.RunAsync(reopened, sequence.Id, export,
            range: (default, TimelineTime.FromSamples(frames, sequence.AudioSampleRate)));
        byte[] decoded = await TimelineFixture.Run("-i", export, "-map", "0:a:0", "-f", "f32le", "-");
        float[] exported = MemoryMarshal.Cast<byte, float>(decoded).ToArray();
        Assert.True(exported.Length >= wet.Length);
        Assert.InRange(exported.Zip(wet, (sample, expected) => Math.Abs(sample - expected)).Max(), 0, .00004);
    }

    [Theory]
    [InlineData(35, 12, 0, 0)]
    [InlineData(1500, 0, -12, 0)]
    [InlineData(9500, 0, 0, 12)]
    public void VoiceBandsChangeTheirIntendedFrequencyAndKeepChannelHistoryIndependent(int frequency, double low, double mid, double high)
    {
        const int rate = 48000;
        var stereo = new float[rate * 2];
        for (int frame = 0; frame < rate; frame++)
            stereo[frame * 2] = (float)(.1 * Math.Sin(2 * Math.PI * frequency * frame / rate));
        double Power() => stereo.Skip(rate).Where((_, index) => index % 2 == 0).Sum(sample => (double)sample * sample);
        double before = Power();
        var equalizer = new VoiceEqualizer(rate, new(low, mid, high));
        equalizer.Process(stereo, 0);
        double gain = 10 * Math.Log10(Power() / before);
        Assert.InRange(Math.Abs(gain - (low + mid + high)), 0, 1);
        Assert.All(stereo.Where((_, index) => index % 2 != 0), sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public async Task NormalizeMeasuresTheMixBeforeTheMasterAndPreservesAStricterPeakTarget()
    {
        var snapshot = fixture.Snapshot;
        var sequence = snapshot.Document.Sequences[0];
        var altered = sequence with { Creator = new(1, [], new(), [], [], MasterGainDb: 24, PeakCeilingDb: -6) };
        var clipped = snapshot with { Document = snapshot.Document with { Sequences = [altered] } };
        var baseline = await LoudnessAnalyzer.MeasureForNormalizationAsync(snapshot, sequence.Id, default);
        var measured = await LoudnessAnalyzer.MeasureForNormalizationAsync(clipped, sequence.Id, default);
        Assert.NotNull(baseline.IntegratedLufs);
        Assert.Equal(baseline.IntegratedLufs, measured.IntegratedLufs);
        Assert.Equal(baseline.TruePeakDb, measured.TruePeakDb);
        Assert.Equal(-6, measured.PeakCeilingDb);
        Assert.True(measured.SafeGainDb <= measured.PeakCeilingDb - measured.TruePeakDb);
    }
}
