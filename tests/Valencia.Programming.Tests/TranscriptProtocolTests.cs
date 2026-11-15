using System.Diagnostics;
using System.Collections.Immutable;
using System.Text.Json;
using Valencia.Analysis;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Media;
using Valencia.Persistence;
using Xunit;

namespace Valencia.Programming.Tests;

public sealed partial class TranscriptionTests
{
    [Fact]
    public async Task ActualStdioTranscriptionSearchTextPlanAndExport()
    {
        var snapshot = (await new ProjectFile().LoadAsync(project)).Snapshot;
        Guid projectId = snapshot.Document.Id, asset = snapshot.Document.Assets[0].Id, sequence = snapshot.Document.Sequences[0].Id;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { typeof(Valencia.Cli.Program).Assembly.Location, "mcp", "--root", root, "--project", "input.valencia" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        int id = 0;
        async Task<JsonElement> Rpc(string method, object parameters)
        {
            await process.StandardInput.WriteLineAsync(EngineJson.Write(new { jsonrpc = "2.0", id = ++id, method, @params = parameters }));
            await process.StandardInput.FlushAsync();
            string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(line);
            return JsonDocument.Parse(line).RootElement.Clone();
        }
        Task<JsonElement> Tool(string name, object arguments) => Rpc("tools/call", new { name, arguments });
        JsonElement Data(JsonElement message) => message.GetProperty("result").GetProperty("structuredContent").GetProperty("result");
        async Task<JsonElement> Wait(Guid jobId, string method)
        {
            for (int trial = 0; trial < 3000; trial++)
            {
                var state = Data(await Tool(method, new { projectId, jobId }));
                if (state.GetProperty("state").GetString() != "running") return state;
                await Task.Delay(10);
            }
            throw new TimeoutException();
        }
        try
        {
            await Rpc("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "transcript test", version = "1" } });
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var discovery = await Rpc("tools/list", new { });
            Assert.Contains("transcript_query", discovery.GetRawText());
            var submitted = Data(await Tool("start_analysis", new { projectId, request = new AnalysisRequest(asset, 1, "transcript") }));
            var state = await Wait(submitted.GetProperty("jobId").GetGuid(), "analysis_status");
            Assert.Equal("completed", state.GetProperty("state").GetString());
            string key = state.GetProperty("key").GetString()!;
            var matches = Data(await Tool("transcript_query", new { projectId, key, query = "important announcement" }));
            var rangeWords = Data(await Tool("transcript_query", new { projectId, key, rangeStart = "0", rangeEnd = "25401600000000", limit = 128 }));
            Assert.Contains(rangeWords.EnumerateArray(), word => word.GetProperty("text").GetString() == "announcement");
            var match = Assert.Single(matches.EnumerateArray());
            var ids = match.GetProperty("wordIds").EnumerateArray().Select(w => w.GetGuid()).ToImmutableArray();
            var requested = new TextEditRequest(sequence, key, "retain", ids);
            string revision = EngineJson.Exact(snapshot.Revision);
            var proposal = Data(await Tool("create_textedit", new { projectId, revision, request = requested }));
            Assert.True(proposal.GetProperty("preview").GetProperty("valid").GetBoolean());
            var plan = proposal.GetProperty("plan");
            var applied = Data(await Tool("apply_edit_plan", new { projectId, revision, plan }));
            Assert.True(applied.GetProperty("committed").GetBoolean());
            var stale = await Tool("create_textedit", new { projectId, revision, request = requested });
            Assert.True(stale.GetProperty("result").GetProperty("isError").GetBoolean());
            var exported = Data(await Tool("run_export", new { projectId, revision = applied.GetProperty("resultingRevision").GetString(), sequenceId = sequence, output = "text-edit.mp4" }));
            Assert.Equal("completed", (await Wait(exported.GetProperty("jobId").GetGuid(), "export_status")).GetProperty("state").GetString());
            using var probe = JsonDocument.Parse(await AnalysisFixture.Run("ffprobe", "-v", "error", "-show_entries", "format=duration:stream=codec_name", "-of", "json", Path.Combine(root, "text-edit.mp4")));
            Assert.InRange(double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture), .3, 3);
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("REQUEST_FAILED", await stderr);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    [Fact]
    public async Task SplitAndTrimmedMappingsRemainSourceAlignedAndCancellationDoesNotPublish()
    {
        await using var engine = await Engine();
        var artifact = await Transcribe(engine);
        var words = TranscriptQueries.Search(artifact, "important announcement").Single().WordIds;
        var sequence = engine.Query().Sequences[0];
        foreach (var track in sequence.Tracks)
        {
            var clip = track.Clips.Single();
            var trimmed = new TrimClip(sequence.Id, clip.Id, new(10 * SequenceTime.TicksPerSecond),
                clip.SourceIn.Shift(new(SequenceTime.TicksPerSecond)), clip.SourceOut);
            Assert.True(engine.Execute(new EditCommand(Guid.NewGuid(), 1, trimmed, engine.Session.Snapshot.Revision)).Success);
            Assert.True(engine.Execute(new EditCommand(Guid.NewGuid(), 1,
                new SplitClip(sequence.Id, clip.Id, new(12 * SequenceTime.TicksPerSecond), Guid.NewGuid()), engine.Session.Snapshot.Revision)).Success);
        }
        var proposal = await engine.TextEditAsync(new(sequence.Id, artifact.Key, "remove", words));
        Assert.NotNull(proposal.Plan);
        Assert.All(proposal.Removed, r => Assert.True(r.Start.TotalSeconds > 10));
        Assert.True(proposal.Preview!.Valid);
        var request = new AnalysisRequest(artifact.AssetId, artifact.StreamIndex, "transcript", Transcription: new(ChunkSeconds: 9));
        var job = engine.Analysis.Start(request);
        engine.Analysis.Cancel(job);
        var cancelled = await engine.Analysis.WaitAsync(job);
        Assert.Equal("cancelled", cancelled.State);
        Assert.Null(await engine.Analysis.Cache.ReadAsync(cancelled.Key));
    }
    [Fact]
    public async Task NonzeroStreamOriginAndTwoHourSparseSpeechUseBoundedChunks()
    {
        string offset = Path.Combine(root, "offset.mkv");
        await AnalysisFixture.Run("ffmpeg", "-v", "error", "-y", "-itsoffset", "1.25", "-i", Path.Combine(root, "speech.wav"), "-c:a", "pcm_s16le", offset);
        var shifted = MediaAssetProbe.Read(Guid.NewGuid(), offset);
        var cache = new AnalysisCache(Path.Combine(root, "offset-cache"));
        var settings = new TranscriptionSettings();
        var transcript = WhisperTranscriber.Run(shifted, 0, settings, "offset", AnalysisCache.Fingerprint(offset), cache, null, default);
        Assert.InRange(transcript.Transcript!.Words.First().Range.Start.Timestamp.TotalSeconds, 1.24, 1.26);
        Assert.Contains(transcript.Transcript.Words, w => WhisperTranscriber.Normalize(w.Text) == "announcement");
        string longFile = Path.Combine(root, "two-hour.mka");
        await AnalysisFixture.Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-i", Path.Combine(root, "speech.wav"),
            "-filter_complex", "[1:a]aresample=16000,asplit=2[a][b];[a]adelay=1000[a1];[b]adelay=7190000[b1];[0:a][a1][b1]amix=inputs=3:duration=first:normalize=0[out]",
            "-map", "[out]", "-t", "7200", "-c:a", "flac", longFile);
        var asset = MediaAssetProbe.Read(Guid.NewGuid(), longFile);
        var timer = Stopwatch.StartNew();
        var result = WhisperTranscriber.Run(asset, 0, settings, "long", AnalysisCache.Fingerprint(longFile), new(Path.Combine(root, "long-cache")), null, default);
        Assert.True(result.Transcript!.CompletedChunks > 200);
        Assert.Contains(result.Transcript.Words, w => AnalysisTime.Normalize(w.Range.Start, result.ContainerStart).TotalSeconds > 7190);
        log.WriteLine("M2C_LONG=" + EngineJson.Write(new
        {
            audioSeconds = 7200,
            wallSeconds = timer.Elapsed.TotalSeconds,
            chunks = result.Transcript.CompletedChunks,
            words = result.Transcript.Words.Length,
            fixture = "Two hours generated FLAC, speech near start/end and otherwise silence; not two hours continuous speech"
        }));
    }
}
