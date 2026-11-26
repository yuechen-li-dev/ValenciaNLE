using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Valencia.Analysis;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Media;
using Valencia.Persistence;
using Valencia.Scripting;
using Xunit;

namespace Valencia.Programming.Tests;

[CollectionDefinition("Whisper", DisableParallelization = true)]
public sealed class WhisperCollection { }

[Collection("Whisper")]
public sealed partial class TranscriptionTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Valencia-transcript", Guid.NewGuid().ToString("N"));
    private string source = "", project = "";
    private string? previousModels;
    private readonly List<(string Text, TimeSpan Start)> referenceWords = [];
    private readonly Xunit.Abstractions.ITestOutputHelper log;
    public TranscriptionTests(Xunit.Abstractions.ITestOutputHelper log) => this.log = log;
    private const string Speech = "Welcome to Valencia. The important announcement begins now. Keep this sentence for the final video. Thank you for listening.";
    public async Task InitializeAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Real local Windows speech fixture is required.");
        Directory.CreateDirectory(root);
        previousModels = Environment.GetEnvironmentVariable("VALENCIA_MODEL_DIR");
        Environment.SetEnvironmentVariable("VALENCIA_MODEL_DIR", Path.Combine(Repo(), "artifacts/local/m2c/models"));
        WhisperModels.Resolve("tiny.en"); // Explicit failure if the documented, authorized model is absent; no test downloads.
        string wave = Path.Combine(root, "speech.wav");
        CreateSpeech(wave);
        source = Path.Combine(root, "speech.mp4");
        await AnalysisFixture.Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30", "-i", wave,
            "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", source);
        project = await AnalysisFixture.Project(root, source);
    }
    private void CreateSpeech(string wave)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var speech = new System.Speech.Synthesis.SpeechSynthesizer();
        speech.SetOutputToWaveFile(wave, new System.Speech.AudioFormat.SpeechAudioFormatInfo(16000,
            System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
        speech.SpeakProgress += (_, e) =>
        {
            if (OperatingSystem.IsWindows()) referenceWords.Add((Speech.Substring(e.CharacterPosition, e.CharacterCount), e.AudioPosition));
        };
        speech.Speak(Speech);
    }
    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("VALENCIA_MODEL_DIR", previousModels);
        Directory.Delete(root, true);
        return Task.CompletedTask;
    }
    private async Task<EditingEngine> Engine()
    {
        var loaded = await new ProjectFile().LoadAsync(project);
        return new(new(loaded.Snapshot), projectPath: project, analysisDirectory: Path.Combine(root, "cache"));
    }
    private static async Task<AnalysisArtifact> Transcribe(EditingEngine engine, TranscriptionSettings? settings = null)
    {
        var asset = engine.Query().Assets[0];
        int stream = asset.Streams.First(s => s.Kind == TrackKind.Audio).Index;
        var state = await engine.Analysis.WaitAsync(engine.Analysis.Start(new(asset.Id, stream, "transcript", Transcription: settings)));
        Assert.True(state.State == "completed", state.Error);
        return await engine.Analysis.ResultAsync(state.Key);
    }
    [Fact]
    public async Task RealLocalTranscriptionIsSourceAlignedCachedAndTyped()
    {
        await using var engine = await Engine();
        var before = engine.Session.Snapshot;
        var artifact = await Transcribe(engine);
        Assert.Equal("Aurelian Whisper / Vulkan", artifact.Transcript!.Provider);
        Assert.Contains(artifact.Transcript.Words, w => WhisperTranscriber.Normalize(w.Text) == "announcement");
        Assert.All(artifact.Transcript.Words, w => Assert.True(w.Range.End.CompareTo(w.Range.Start) >= 0));
        Assert.All(artifact.Transcript.Words.Where(w => w.TimingUsable), w => Assert.True(w.Range.End.CompareTo(w.Range.Start) > 0));
        Assert.Equal(16000, artifact.SampleRate);
        Assert.Same(before, engine.Session.Snapshot);
        var job = engine.Analysis.Start(new(artifact.AssetId, artifact.StreamIndex, "transcript"));
        Assert.True((await engine.Analysis.WaitAsync(job)).CacheHit);
        Assert.Equal(AnalysisJson.Write(artifact), AnalysisJson.Write(await engine.Analysis.ResultAsync(artifact.Key)));
        using var json = JsonDocument.Parse(EngineJson.Write(artifact));
        Assert.Empty(Copeland.TS.Tson.TypeProfileCompiler.Validate(AnalysisContracts.Catalog, "AnalysisArtifact", json.RootElement));
        var matches = TranscriptQueries.Search(artifact, "IMPORTANT announcement", context: 1);
        Assert.Single(matches);
        Assert.True(matches[0].TimingUsable);
        Assert.Empty(TranscriptQueries.Search(artifact, "not actually spoken"));
        Assert.Throws<ArgumentException>(() => TranscriptQueries.UniqueMatch(artifact, "for"));
        Assert.Throws<ArgumentException>(() => TranscriptQueries.UniqueMatch(artifact, "not actually spoken"));
        Assert.Throws<ArgumentException>(() => TranscriptQueries.Search(artifact, "welcome", limit: 129));
        var expected = Speech.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(WhisperTranscriber.Normalize).ToArray();
        var actual = artifact.Transcript.Words.Select(w => WhisperTranscriber.Normalize(w.Text)).ToArray();
        int[,] distance = new int[expected.Length + 1, actual.Length + 1];
        for (int i = 0; i <= expected.Length; i++) distance[i, 0] = i;
        for (int j = 0; j <= actual.Length; j++) distance[0, j] = j;
        for (int i = 1; i <= expected.Length; i++)
            for (int j = 1; j <= actual.Length; j++) distance[i, j] = Math.Min(distance[i - 1, j] + 1, Math.Min(distance[i, j - 1] + 1, distance[i - 1, j - 1] + (expected[i - 1] == actual[j - 1] ? 0 : 1)));
        var errors = artifact.Transcript.Words.Where(w => w.TimingUsable).Select(w => new { Word = w, Reference = referenceWords.Where(r => WhisperTranscriber.Normalize(r.Text) == WhisperTranscriber.Normalize(w.Text)).ToArray() })
            .Where(p => p.Reference.Length == 1).Select(p => Math.Abs(AnalysisTime.Normalize(p.Word.Range.Start, artifact.ContainerStart).TotalSeconds - p.Reference[0].Start.TotalSeconds)).ToArray();
        double wer = distance[expected.Length, actual.Length] / (double)expected.Length;
        Assert.InRange(wer, 0, .25);
        Assert.NotEmpty(errors);
        log.WriteLine("M2C_WORDS=" + EngineJson.Write(new { referenceWords = referenceWords.Select(r => new { r.Text, seconds = r.Start.TotalSeconds }), predicted = artifact.Transcript.Words.Select(w => new { w.Text, seconds = AnalysisTime.Normalize(w.Range.Start, artifact.ContainerStart).TotalSeconds, w.TimingUsable }) }));
        log.WriteLine("M2C_ACCURACY=" + EngineJson.Write(new
        {
            wer,
            wordStartMeanAbsoluteErrorSeconds = errors.Average(),
            wordStartMaxErrorSeconds = errors.Max(),
            comparedUniqueWords = errors.Length,
            totalWords = actual.Length,
            untimedWords = artifact.Transcript.Words.Count(w => !w.TimingUsable),
            reference = "Windows SAPI SpeakProgress on one generated English voice; not a general accuracy benchmark"
        }));
    }
    [Fact]
    public async Task SearchToRetainPlanUndoSaveReopenAndIndependentExport()
    {
        await using var engine = await Engine();
        var transcript = await Transcribe(engine);
        var selected = Assert.Single(TranscriptQueries.Search(transcript, "important announcement"));
        var before = engine.Session.Snapshot;
        Guid sequence = engine.Query().Sequences[0].Id;
        var proposal = await engine.TextEditAsync(new(sequence, transcript.Key, "retain", selected.WordIds));
        Assert.True(proposal.Preview!.Valid);
        Assert.Same(before, engine.Session.Snapshot);
        var committed = engine.Plan(proposal.Plan!, true);
        Assert.True(committed.Valid);
        Assert.Equal(EngineJson.Write(proposal.Preview.Changes), EngineJson.Write(committed.Changes));
        Assert.True(engine.Undo(engine.Session.Snapshot.Revision).Success);
        Assert.True(DocumentSemantics.Equal(before.Document, engine.Session.Snapshot.Document));
        Assert.True(engine.Redo(engine.Session.Snapshot.Revision).Success);
        Assert.False(engine.Plan(proposal.Plan!, true).Valid);
        string saved = Path.Combine(root, "edited.valencia");
        await engine.SaveAsync(saved);
        Assert.True(DocumentSemantics.Equal(engine.Session.Snapshot.Document, (await new ProjectFile().LoadAsync(saved)).Snapshot.Document));
        string movie = Path.Combine(root, "edited.mp4");
        var result = await engine.Exports.WaitAsync(engine.Exports.Start(sequence, movie));
        Assert.Equal("completed", result.State);
        var clip = engine.Query().Sequences[0].Tracks.First(t => t.Kind == TrackKind.Video).Clips.Single();
        double sourceSeconds = clip.SourceIn.Timestamp.TotalSeconds + clip.SourceIn.Offset.TotalSeconds + .2;
        async Task<byte[]> Frame(string file, string time) => await AnalysisFixture.Run("ffmpeg", "-v", "error", "-ss", time, "-i", file,
            "-frames:v", "1", "-vf", "scale=32:18:flags=area", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1");
        var a = await Frame(movie, "0.2");
        var b = await Frame(source, sourceSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.InRange(a.Zip(b, (x, y) => Math.Abs(x - y)).Average(), 0, 8);
    }
    [Fact]
    public async Task RepeatedOccurrencesRequireExplicitIdentityAndUntimedWordsCannotEdit()
    {
        await using var engine = await Engine();
        var artifact = await Transcribe(engine);
        var matches = Assert.Single(TranscriptQueries.Search(artifact, "important announcement"));
        var sequence = engine.Query().Sequences[0];
        foreach (var track in sequence.Tracks)
        {
            var clip = track.Clips[0];
            Assert.True(engine.Execute(new EditCommand(Guid.NewGuid(), 1,
                new InsertClip(sequence.Id, track.Id, clip with { Id = Guid.NewGuid(), Start = new(20 * SequenceTime.TicksPerSecond) }), engine.Session.Snapshot.Revision)).Success);
        }
        var ambiguity = await engine.TextEditAsync(new(sequence.Id, artifact.Key, "remove", matches.WordIds));
        Assert.Null(ambiguity.Plan);
        Assert.Equal(2, ambiguity.Occurrences.Length);
        var chosen = await engine.TextEditAsync(new(sequence.Id, artifact.Key, "remove", matches.WordIds, [ambiguity.Occurrences[1].OccurrenceId]));
        Assert.True(chosen.Preview!.Valid);
        Assert.All(chosen.Removed, r => Assert.True(r.Start.TotalSeconds >= 20));
        var bad = artifact with { Transcript = artifact.Transcript! with { Words = artifact.Transcript!.Words.SetItem(0, artifact.Transcript.Words[0] with { TimingUsable = false }) } };
        Assert.Throws<ArgumentException>(() => TranscriptEditing.Create(engine.Session.Snapshot, bad, new(sequence.Id, artifact.Key, "remove", [bad.Transcript.Words[0].Id])));
    }
    [Fact]
    public async Task CompletedChunkCacheResumesAndCopelandUsesSamePlan()
    {
        await using var engine = await Engine();
        var settings = new TranscriptionSettings(ChunkSeconds: 8, OverlapSeconds: 2);
        var artifact = await Transcribe(engine, settings);
        File.Delete(Path.Combine(engine.Analysis.Cache.DirectoryPath, artifact.Key + ".vca"));
        var resumed = await Transcribe(engine, settings);
        Assert.True(resumed.Transcript!.ReusedChunks > 0);
        Assert.Equal(AnalysisJson.Write(artifact.Transcript!.Words), AnalysisJson.Write(resumed.Transcript.Words));
        var normal = await Transcribe(engine);
        var words = TranscriptQueries.Search(normal, "important announcement").Single().WordIds;
        var native = await engine.TextEditAsync(new(engine.Query().Sequences[0].Id, normal.Key, "retain", words));
        using var host = new CopelandScriptHost();
        var result = await host.RunAsync($$"""
            using Valencia.Scripting;
            function run(api: ScriptApi): string {
                const job = api.Transcribe("{{normal.AssetId}}", {{normal.StreamIndex}}, "tiny.en");
                const key = api.WaitAnalysis(job);
                const range = api.TranscriptRange(key, "0", "25401600000000");
                const sequence = api.FirstSequence();
                const clipWords = api.TranscriptForClip(key, sequence, api.FirstClipOnTrack(sequence, api.FirstTrack(sequence, "video")));
                return api.TextEditPlan(api.FirstSequence(), key, "important announcement", "retain");
            }
            """, Path.Combine(root, "edit.ts"), engine);
        Assert.True(result.Success, EngineJson.Write(result.Diagnostics));
        Assert.Equal(EngineJson.Write(native.Plan), result.ReturnValue);
    }
    private static string Repo()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "Valencia.slnx"))) return folder.FullName;
        throw new DirectoryNotFoundException();
    }
}
