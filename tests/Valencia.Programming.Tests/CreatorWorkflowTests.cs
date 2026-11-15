using System.Collections.Immutable;
using System.Text.Json;
using Valencia.Analysis;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Persistence;
using Valencia.Scripting;
using Xunit;

namespace Valencia.Programming.Tests;

public sealed partial class TranscriptionTests
{
    [Fact]
    public async Task ThreeAudioStreamsRequireExplicitMusicRoleAndLeaveCommunicationsUnducked()
    {
        string three = Path.Combine(root, "three-streams.mkv");
        await AnalysisFixture.Run("ffmpeg", "-v", "error", "-y", "-i", source, "-map", "0:v", "-map", "0:a", "-map", "0:a", "-map", "0:a", "-c", "copy", three);
        string path = await AnalysisFixture.Project(root, three);
        var loaded = await new ProjectFile().LoadAsync(path);
        await using var engine = new EditingEngine(new(loaded.Snapshot), projectPath: path, analysisDirectory: Path.Combine(root, "three-cache"));
        var transcript = await Transcribe(engine, new(Alignment: "dtw"));
        var audio = await engine.AnalyzeAsync(new(transcript.AssetId, transcript.StreamIndex, "audio"));
        var request = ThreeParts(engine, transcript, audio);
        var before = engine.Session.Snapshot;
        await Assert.ThrowsAsync<NotSupportedException>(() => engine.CreateCreatorAsync(request));
        Assert.Same(before, engine.Session.Snapshot);
        var proposal = await engine.CreateCreatorAsync(request with { MusicStreamIndices = [2] });
        Assert.True(proposal.Preview.Valid);
        var tracks = engine.Query().Tracks(request.SequenceId);
        var settings = proposal.Plan.Operations.OfType<CreatorPlan>().Single().Settings;
        var music = settings.Audio.Single(a => tracks.First(t => t.Id == a.TrackId).Clips[0].StreamIndex == 2);
        var communications = settings.Audio.Single(a => tracks.First(t => t.Id == a.TrackId).Clips[0].StreamIndex == 3);
        Assert.Equal(request.MusicGainDb, music.GainDb); Assert.NotEmpty(music.Envelope);
        Assert.Equal(0, communications.GainDb); Assert.Empty(communications.Envelope);
        Assert.Same(before, engine.Session.Snapshot);
    }
    private static CreatorRequest ThreeParts(EditingEngine engine, AnalysisArtifact transcript, AnalysisArtifact audio, bool normalize = false) => new(
        engine.Query().Sequences[0].Id, transcript.Key, audio.Key, TargetTicks: EngineJson.Exact(9 * SequenceTime.TicksPerSecond),
        MinimumTicks: EngineJson.Exact(SequenceTime.TicksPerSecond), MaximumTicks: EngineJson.Exact(3 * SequenceTime.TicksPerSecond),
        ShortTicks: EngineJson.Exact(SequenceTime.TicksPerSecond), Width: 320, Height: 180, Normalize: normalize,
        Markers: [new("0", EngineJson.Exact(3 * SequenceTime.TicksPerSecond), "A"),
            new(EngineJson.Exact(3 * SequenceTime.TicksPerSecond), EngineJson.Exact(6 * SequenceTime.TicksPerSecond), "B"),
            new(EngineJson.Exact(6 * SequenceTime.TicksPerSecond), EngineJson.Exact(9 * SequenceTime.TicksPerSecond), "C")]);

    [Fact]
    public async Task CreatorPlanIsAtomicReproducibleUndoablePersistedAndExportsFourRealOutputs()
    {
        await using var engine = await Engine();
        var transcript = await Transcribe(engine, new(Alignment: "dtw"));
        var audio = await engine.AnalyzeAsync(new(transcript.AssetId, transcript.StreamIndex, "audio"));
        var request = ThreeParts(engine, transcript, audio, normalize: true);
        var before = engine.Session.Snapshot;
        var proposal = await engine.CreateCreatorAsync(request);
        Assert.True(proposal.Preview.Valid, EngineJson.Write(proposal.Preview));
        Assert.Same(before, engine.Session.Snapshot);
        Assert.Equal(4, proposal.Outputs.Length);
        Assert.Equal(9 * SequenceTime.TicksPerSecond, proposal.EstimatedDuration.Ticks);
        Assert.NotNull(proposal.Loudness?.IntegratedLufs);
        var repeated = await engine.CreateCreatorAsync(request);
        Assert.Equal(EngineJson.Write(proposal.Plan), EngineJson.Write(repeated.Plan));
        Assert.True(engine.ApplyCreator(proposal.Key).Committed);
        var after = engine.Session.Snapshot.Document;
        Assert.NotEmpty(after.Sequences[0].Creator!.Words);
        Assert.True(engine.Undo(engine.Session.Snapshot.Revision).Success);
        Assert.True(DocumentSemantics.Equal(before.Document, engine.Session.Snapshot.Document));
        Assert.True(engine.Redo(engine.Session.Snapshot.Revision).Success);
        Assert.True(DocumentSemantics.Equal(after, engine.Session.Snapshot.Document));
        Assert.False(engine.ApplyCreator(proposal.Key).Valid);
        string saved = Path.Combine(root, "creator.valencia");
        await engine.SaveAsync(saved);
        var reopened = await new ProjectFile().LoadAsync(saved);
        Assert.True(DocumentSemantics.Equal(after, reopened.Snapshot.Document));
        var outputs = await engine.ExportProfilesAsync(request.SequenceId, Path.Combine(root, "outputs"));
        Assert.Equal(4, outputs.Length);
        foreach (var output in outputs)
        {
            Assert.Equal("completed", output.State);
            var profile = proposal.Outputs.Single(p => Path.GetFileNameWithoutExtension(output.Output) == p.Name);
            using var probe = JsonDocument.Parse(await AnalysisFixture.Run("ffprobe", "-v", "error", "-show_entries", "format=duration:stream=codec_name,width,height", "-of", "json", output.Output));
            var video = probe.RootElement.GetProperty("streams").EnumerateArray().Single(s => s.GetProperty("codec_name").GetString() == "h264");
            Assert.Equal(profile.Width, video.GetProperty("width").GetInt32());
            Assert.Equal(profile.Height, video.GetProperty("height").GetInt32());
            Assert.InRange(double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture), profile.Name == "long-form" ? 9 : 1, profile.Name == "long-form" ? 9.05 : 1.05);
        }
        var loudness = await LoudnessAnalyzer.SequenceAsync(engine.Session.Snapshot, request.SequenceId, default);
        double expectedLoudness = proposal.Loudness!.IntegratedLufs!.Value + proposal.Loudness.SafeGainDb!.Value;
        Assert.InRange(loudness.IntegratedLufs!.Value, expectedLoudness - .3, expectedLoudness + .3);
        Assert.True(loudness.TruePeakDb <= -.8);
    }

    [Fact]
    public async Task ActualCopelandCreatorJobsAndCorrectionsUseTheSameEngine()
    {
        await using var engine = await Engine();
        var transcript = await Transcribe(engine, new(Alignment: "dtw"));
        var audio = await engine.AnalyzeAsync(new(transcript.AssetId, transcript.StreamIndex, "audio"));
        var request = ThreeParts(engine, transcript, audio);
        var native = await engine.CreateCreatorAsync(request);
        var corrected = await engine.CorrectTranscriptAsync(transcript.Key, new Dictionary<Guid, string> { [transcript.Transcript!.Words[0].Id] = "Greetings" });
        Assert.Equal("Welcome", corrected.Transcript!.Words[0].OriginalText);
        Assert.Equal(transcript.Transcript.Words[0].Range, corrected.Transcript.Words[0].Range);
        Assert.Equal(transcript.Transcript.Words[0].NativeStartTicks, corrected.Transcript.Words[0].NativeStartTicks);
        Assert.NotEqual(transcript.Key, corrected.Key);
        Assert.NotEmpty(TranscriptQueries.Search(corrected, "Greetings"));
        string requestFile = Path.Combine(root, "creator.obj.ts");
        await File.WriteAllTextAsync(requestFile, "const $value: CreatorRequest = " + EngineJson.Write(request) + ";");
        using var host = new CopelandScriptHost();
        var script = await host.RunAsync($$"""
            using Valencia.Scripting;
            function run(api: ScriptApi): string {
                const job = api.StartCreatorFromFile("{{requestFile.Replace('\\', '/')}}");
                const key = api.WaitCreator(job);
                const plan = api.CreatorPlan(key);
                api.ApplyCreator(key);
                api.Undo();
                api.Redo();
                const destination: string = api.ProjectFile() + ".creator.valencia";
                api.Save(destination);
                api.OpenProject(destination);
                api.ExportOutputs(api.FirstSequence(), destination + ".outputs");
                return plan;
            }
            """, Path.Combine(root, "creator.ts"), engine);
        Assert.True(script.Success, EngineJson.Write(script.Diagnostics));
        Assert.Equal(EngineJson.Write(native.Plan), script.ReturnValue);
        Assert.Equal(3, engine.Query().Sequences[0].Tracks[0].Clips.Length);
        Assert.Equal(4, Directory.GetFiles(project + ".creator.valencia.outputs", "*.mp4").Length);
    }
    [Fact]
    public async Task CreatorJobsRejectStaleRevisionsAndCancelWithoutCommitting()
    {
        await using var engine = await Engine();
        var transcript = await Transcribe(engine);
        var audio = await engine.AnalyzeAsync(new(transcript.AssetId, transcript.StreamIndex, "audio"));
        var request = ThreeParts(engine, transcript, audio, normalize: true);
        var before = engine.Session.Snapshot;
        Assert.Throws<ArgumentException>(() => engine.CreatorJobs.Start(request, before.Revision + 1));
        var job = engine.CreatorJobs.Start(request, before.Revision);
        engine.CreatorJobs.Cancel(job);
        Assert.Equal("cancelled", (await engine.CreatorJobs.WaitAsync(job)).State);
        Assert.Same(before, engine.Session.Snapshot);
        var bad = request with { TargetTicks = "0" };
        await Assert.ThrowsAsync<ArgumentException>(() => engine.CreateCreatorAsync(bad));
    }

    [Fact]
    public async Task TextRetentionPaddingAddsContextWithoutMutatingTheProject()
    {
        await using var engine = await Engine();
        var transcript = await Transcribe(engine, new(Alignment: "dtw"));
        var words = TranscriptQueries.UniqueMatch(transcript, "important announcement").WordIds;
        var request = new TextEditRequest(engine.Query().Sequences[0].Id, transcript.Key, "retain", words);
        var before = engine.Session.Snapshot;
        var exact = await engine.TextEditAsync(request);
        var padded = await engine.TextEditAsync(request with { PaddingMs = 200 });
        Assert.True(exact.Preview!.Valid);
        Assert.True(padded.Preview!.Valid);
        Assert.True(padded.Preview.ResultingDuration!.Value.Ticks > exact.Preview.ResultingDuration!.Value.Ticks);
        Assert.Same(before, engine.Session.Snapshot);
        await Assert.ThrowsAsync<ArgumentException>(() => engine.TextEditAsync(request with { Mode = "remove", PaddingMs = 200 }));
    }
}
