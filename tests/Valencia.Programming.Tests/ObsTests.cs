using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Obs;
using Xunit;

namespace Valencia.Programming.Tests;

public sealed class ObsTests : IAsyncLifetime
{
    [Fact]
    public async Task StoppedEventHandoffWaitsForLaggingFrontendStatusBeforeImport()
    {
        string path = Path.Combine(root, "drained.mkv");
        await AnalysisFixture.Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=red:size=64x64:duration=1", "-c:v", "libx264", path);
        await using var server = new ObsProtocolFixture { Output = path, StopStatusLagResponses = 3 };
        await using var controller = EditingEngine.Create(Guid.NewGuid(), "Controller");
        var obs = await controller.ConnectObsAsync(new(server.Endpoint), server.Password, Path.Combine(root, "take.json"), root, true);
        await obs.StartAsync(); await obs.StopAsync();
        await using var destination = EditingEngine.Create(Guid.NewGuid(), "New current project");
        var result = await obs.ImportIntoAsync(destination, 0);
        Assert.Single(destination.Query().Assets); Assert.Empty(controller.Query().Assets);
        Assert.Single(result.AssetIds);
        Assert.Equal(result.AssetIds[0], ObsManifestFile.Load(Path.Combine(root, "take.json")).Files[0].AssetId);
        Assert.True(server.Requests.Count(r => r == "GetRecordStatus") >= 5);
    }
    private readonly string root = Path.Combine(Path.GetTempPath(), "Valencia-obs", Guid.NewGuid().ToString("N"));
    public Task InitializeAsync() { Directory.CreateDirectory(root); return Task.CompletedTask; }
    public Task DisposeAsync() { Directory.Delete(root, true); return Task.CompletedTask; }

    [Fact]
    public async Task ExternalSecondTakeGetsNewIdentityWithoutErasingTheArchivedFirstTake()
    {
        await using var server = new ObsProtocolFixture { Output = Path.Combine(root, "first.mkv") };
        string path = Path.Combine(root, "takes.json");
        await using var session = new ObsRecordingSession(new(new(server.Endpoint), server.Password), path);
        await session.ConnectAsync(); await session.StartAsync(); await session.StopAsync();
        Guid first = session.Snapshot.SessionId;
        await session.Client.RequestAsync("StartRecord"); // OBS or another client, not session.StartAsync
        for (int i = 0; i < 100 && session.Snapshot.SessionId == first; i++) await Task.Delay(10);
        Assert.NotEqual(first, session.Snapshot.SessionId);
        Assert.Null(session.Snapshot.StoppedAt); Assert.True(session.Snapshot.ContinuousObservation);
        await session.StopAsync();
        string archive = Path.Combine(root, "takes." + first.ToString("N") + ".json");
        Assert.Equal(first, ObsManifestFile.Load(archive).SessionId);
        Assert.Equal("stopped", ObsManifestFile.Load(path).State);
        var replay = session.Snapshot with { Files = [new(server.Output, true, "replay", first, DurationMs: 1000, Kind: RecordingFileKind.Replay)] };
        Assert.Throws<NotSupportedException>(() => ObsWorkflow.PlannerMarkers(replay, first, true));
        await ObsManifestFile.SaveAsync(Path.Combine(root, "replay.json"), replay);
        Assert.Equal(RecordingFileKind.Replay, ObsManifestFile.Load(Path.Combine(root, "replay.json")).Files[0].Kind);
    }

    [Fact]
    public async Task RealWebSocketAuthenticationDiscoveryControlsMarkersSplitsAndNoStopOnDispose()
    {
        await using var server = new ObsProtocolFixture { Output = Path.Combine(root, "one.mkv"), SplitPath = Path.Combine(root, "two.mkv") };
        var client = new ObsClient(new(server.Endpoint), server.Password);
        await using (var session = new ObsRecordingSession(client, Path.Combine(root, "manifest.json")))
        {
            await session.ConnectAsync();
            var discovery = await ObsDiscoveryService.ReadAsync(client);
            Assert.True(discovery.Version.SupportsFileChanges);
            Assert.Single(discovery.Inputs);
            Assert.Equal(new[] { 1 }, discovery.Inputs[0].AudioTracks.ToArray());
            Assert.Contains(ObsDiscoveryService.Preflight(discovery), check => check.Code == "VERIFY_AUDIO");
            await session.StartAsync();
            var marker = await session.MarkAsync("Explanation");
            Assert.Equal(2500, marker.OutputDurationMs);
            Assert.True(marker.ContinuousObservation);
            Assert.Contains("unbounded", marker.Precision);
            await session.PauseAsync(true);
            Assert.True(server.Paused);
            await session.PauseAsync(false);
            await session.StopAsync();
            await Task.Delay(50);
            Assert.Equal(2, session.Snapshot.Files.Length);
            Assert.All(session.Snapshot.Files, file => Assert.True(file.Completed));
            Assert.Equal("stopped", ObsManifestFile.Load(Path.Combine(root, "manifest.json")).State);
            await session.StartAsync();
        }
        Assert.True(server.Active);
        Assert.Single(server.Requests, name => name == "StopRecord");
    }

    [Fact]
    public async Task WrongPasswordIsExplicitAndNotRetried()
    {
        await using var server = new ObsProtocolFixture();
        await using var client = new ObsClient(new(server.Endpoint), "incorrect fixture credential");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ConnectAsync());
        Assert.Equal(1, server.Connections);
        Assert.DoesNotContain("incorrect fixture credential", client.Status.Error ?? "");
    }

    [Fact]
    public async Task StopAcknowledgementDoesNotFinalizeWhileEncoderDrains()
    {
        await using var server = new ObsProtocolFixture { Output = Path.Combine(root, "draining.mkv"), StopDrainMs = 300 };
        await using var session = new ObsRecordingSession(new(new(server.Endpoint), server.Password), Path.Combine(root, "drain.json"));
        await session.ConnectAsync();
        await session.StartAsync();
        var stop = session.StopAsync();
        await Task.Delay(50);
        Assert.False(stop.IsCompleted);
        Assert.All(session.Snapshot.Files, file => Assert.False(file.Completed));
        await stop;
        Assert.All(session.Snapshot.Files, file => Assert.True(file.Completed));
        Assert.False(server.Active);
    }

    [Fact]
    public async Task UnsupportedRequestsRemoteEndpointsTimeoutAndCancellationAreExplicit()
    {
        Assert.Throws<ArgumentException>(() => new ObsClient(new(new Uri("ws://example.com:4455")), "secret"));
        Assert.Throws<ArgumentException>(() => new ObsClient(new(new Uri("wss://example.com:4455")), "secret"));
        await using var server = new ObsProtocolFixture();
        await using var client = new ObsClient(new(server.Endpoint, RequestTimeoutSeconds: 1), server.Password);
        await client.ConnectAsync();
        await Assert.ThrowsAsync<NotSupportedException>(() => client.RequestAsync("StartStream"));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.RequestAsync("SaveReplayBuffer"));
        server.IgnoreStatus = true;
        await Assert.ThrowsAsync<TimeoutException>(() => client.RequestAsync("GetRecordStatus"));
        using var cancelled = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RequestAsync("GetRecordStatus", cancellation: cancelled.Token));
        Assert.DoesNotContain("StartStream", server.Requests);
    }

    [Fact]
    public async Task ReconnectDoesNotReplayARecordingMutationAndLosesMarkerContinuity()
    {
        await using var server = new ObsProtocolFixture { Output = Path.Combine(root, "recording.mkv") };
        await using var session = new ObsRecordingSession(new(new(server.Endpoint), server.Password), Path.Combine(root, "reconnect.json"));
        await session.ConnectAsync();
        await session.StartAsync();
        server.DropNextStatus = true;
        await Assert.ThrowsAnyAsync<Exception>(() => ObsDiscoveryService.RecordStatusAsync(session.Client));
        for (int i = 0; i < 200 && (server.Connections < 2 || session.Client.Status.State != ObsConnectionState.Connected); i++) await Task.Delay(20);
        Assert.True(server.Connections >= 2);
        Assert.Single(server.Requests, request => request == "StartRecord");
        Assert.False((await session.MarkAsync("After reconnect")).ContinuousObservation);
        Assert.True(server.Active);
    }

    [Fact]
    public async Task RecordingIngestionIsAtomicStableIdempotentAndRejectsUnsafeFiles()
    {
        string media = Path.Combine(root, "completed.mkv");
        await AnalysisFixture.Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10:duration=1", "-f", "lavfi", "-i", "sine=sample_rate=48000:duration=1", "-c:v", "libx264", "-c:a", "aac", media);
        await using var engine = EditingEngine.Create(Guid.NewGuid(), "OBS import");
        var manifest = RecordingManifest.Empty() with { State = "stopped", StoppedAt = DateTimeOffset.UtcNow, Files = [new(media, true, "Simulated OBS STOPPED event")] };
        var first = await engine.ImportRecordingAsync(manifest, root, 0);
        Assert.Single(first.AssetIds);
        Assert.Equal(2, engine.Query().Assets[0].Streams.Length);
        var second = await engine.ImportRecordingAsync(first.Manifest, root, engine.Session.Snapshot.Revision);
        Assert.True(second.AlreadyImported);
        Assert.Equal(first.Revision, second.Revision);
        var before = engine.Session.Snapshot;
        await Assert.ThrowsAsync<ArgumentException>(() => engine.ImportRecordingAsync(manifest with { Files = [new(media, false, "Only a started event")] }, root, before.Revision));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => engine.ImportRecordingAsync(manifest with { Files = [new(Path.Combine(Path.GetTempPath(), "outside.mkv"), true, "untrusted path")] }, root, before.Revision));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.ImportRecordingAsync(manifest, root, 0));
        using var cancelled = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.ImportRecordingAsync(manifest with { Files = [new(Path.Combine(root, "not-created.mkv"), true, "Delayed file")] }, root, before.Revision, cancelled.Token));
        Assert.Same(before, engine.Session.Snapshot);
        Assert.True(engine.Undo(before.Revision).Success);
        Assert.Empty(engine.Query().Assets);
        Assert.True(engine.Redo(engine.Session.Snapshot.Revision).Success);
        Assert.Equal(first.AssetIds[0], engine.Query().Assets[0].Id);
    }

    [Fact]
    public async Task FailedMultiFileProbeDoesNotCommitEarlierAssetsAndMarkersRequireApproximatePolicy()
    {
        string media = Path.Combine(root, "good.mp4"), bad = Path.Combine(root, "bad.mp4");
        await AnalysisFixture.Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=red:size=64x64:duration=1", "-c:v", "libx264", media);
        await File.WriteAllTextAsync(bad, "corrupted recording");
        await using var engine = EditingEngine.Create(Guid.NewGuid(), "Atomic import");
        var manifest = RecordingManifest.Empty() with { Files = [new(media, true, "stopped"), new(bad, true, "stopped")] };
        await Assert.ThrowsAnyAsync<Exception>(() => engine.ImportRecordingAsync(manifest, root, 0));
        Assert.Empty(engine.Query().Assets);
        Guid asset = Guid.NewGuid();
        var marked = manifest with { Files = [new(media, true, "stopped", asset, DurationMs: 10000)],
            Markers = [new(Guid.NewGuid(), "highlight", "Important", 3000, 5, DateTimeOffset.UtcNow, media, true, false)] };
        Assert.Throws<ArgumentException>(() => ObsWorkflow.PlannerMarkers(marked, asset, false));
        var range = Assert.Single(ObsWorkflow.PlannerMarkers(marked, asset, true));
        Assert.Equal(EngineJson.Exact(Valencia.Core.SequenceTime.TicksPerSecond), range.Start);
        Assert.Equal(EngineJson.Exact(7 * Valencia.Core.SequenceTime.TicksPerSecond), range.End);
    }

    [Fact]
    public async Task RoughCutsAcceptRealAacVideoOffsetsWithoutLosingAlignment()
    {
        string media = Path.Combine(root, "obs-offset.mkv");
        await AnalysisFixture.Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=30:duration=4",
            "-f", "lavfi", "-i", "aevalsrc=if(between(t\\,1\\,3)\\,0\\,0.1*sin(2*PI*440*t)):s=48000:d=4", "-c:v", "libx264", "-c:a", "aac", media);
        var imported = Valencia.Media.MediaAssetProbe.Import(Guid.NewGuid(), media);
        await using var engine = EditingEngine.Create(Guid.NewGuid(), "OBS offsets");
        Guid sequence = Guid.NewGuid();
        Assert.True(engine.Execute(new EditCommand(Guid.NewGuid(), 1, new CreateSequence(sequence, "Main", new(64, 64, new(30, 1)), 48000), 0)).Success);
        Assert.True(engine.Execute(new EditCommand(Guid.NewGuid(), 1, new AddAsset(imported.Asset), engine.Session.Snapshot.Revision)).Success);
        Assert.True(engine.Execute(new EditTransaction(Guid.NewGuid(), 1, engine.Session.Snapshot.Revision,
            [.. InsertionCommands.Build(engine.Session.Snapshot, sequence, imported, default).Select(o => new EditCommand(Guid.NewGuid(), 1, o))])).Success);
        var tracks = engine.Query().Tracks(sequence);
        Assert.NotEqual(tracks[0].Clips[0].Start, tracks[1].Clips[0].Start);
        var audio = await engine.AnalyzeAsync(new(imported.Asset.Id, 1, "audio"));
        var proposal = await engine.RoughCutAsync(sequence, audio.Key, new());
        Assert.True(proposal.Valid);
        Assert.NotNull(proposal.Plan);
        var before = engine.Session.Snapshot.Document;
        Assert.True(engine.Plan(proposal.Plan, true).Committed);
        var active = engine.Query().Evaluate(sequence, new(Valencia.Core.SequenceTime.TicksPerSecond / 2));
        Assert.Equal(2, active.Length);
        Assert.True(active[0].SourcePosition.EquivalentTo(active[1].SourcePosition));
        Assert.True(engine.Undo(engine.Session.Snapshot.Revision).Success);
        Assert.True(DocumentSemantics.Equal(before, engine.Session.Snapshot.Document));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task DpapiCredentialsAndCancelledAtomicMetadataPreserveExistingFiles()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI qualification requires Windows.");
        string path = Path.Combine(root, "connection.json");
        string password = Guid.NewGuid().ToString("N");
        await ObsCredentials.SaveAsync(new Uri("ws://127.0.0.1:4455"), password, path);
        Assert.DoesNotContain(password, await File.ReadAllTextAsync(path));
        Assert.Equal(password, ObsCredentials.Load(path)!.Value.Password);
        byte[] before = await File.ReadAllBytesAsync(path);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ObsCredentials.SaveAsync(new Uri("ws://127.0.0.1:4456"), "another test credential", path, cancelled.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        string manifestPath = Path.Combine(root, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, "{\"schemaVersion\":99}");
        Assert.ThrowsAny<Exception>(() => ObsManifestFile.Load(manifestPath));
    }
}
