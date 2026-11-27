using System.Text.Json;
using Valencia.Core.Timeline;
using Valencia.Engine;
using Valencia.Media;
using Valencia.Persistence;
using Xunit;

namespace Valencia.Programming.Tests;

public sealed class ProductExportTests
{
    [Fact]
    public async Task DurableQueuePinsRevisionPersistsResultsAndFlagsLoudnessWithoutRejectingIntentionalSilence()
    {
        string root = Directory.CreateTempSubdirectory("Valencia-M3b-render-").FullName;
        try
        {
            string media = Path.Combine(root, "source.mkv");
            await AnalysisFixture.Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=160x90:r=30000/1001:d=2",
                "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "2", "-c:v", "mpeg4", "-c:a", "pcm_s16le", media);
            string path = Path.Combine(root, "project.valencia");
            await AnalysisFixture.Run("dotnet", typeof(Valencia.Cli.Program).Assembly.Location, "project", "create", path,
                "--media", media, "--width", "160", "--height", "90", "--fps-num", "30000", "--fps-den", "1001");
            var policy = new WorkspacePolicy(root);
            Guid job;
            long revision;
            await using (var engine = await EditingEngine.OpenAsync(Path.GetFileName(path), policy))
            {
                var snapshot = engine.Session.Snapshot;
                revision = snapshot.Revision;
                var sequence = snapshot.Document.Sequences[0];
                var spec = new RenderSpecification(sequence.Id, Path.Combine(root, "render.webm"), null, "vp9", null, new AudioFinish(), true, 34);
                var queued = await engine.Exports.EnqueueAsync(snapshot, spec);
                var plan = new EditPlan(1, snapshot.Document.Id, EngineJson.Exact(revision), sequence.Id, [new DeletePlan(sequence.Tracks[0].Clips[0].Id)]);
                Assert.True(engine.Plan(plan, true).Committed);
                job = queued.JobId;
                var completed = await engine.Exports.WaitAsync(job).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(completed.State == "completed", EngineJson.Write(completed));
                Assert.Equal(EngineJson.Exact(revision), completed.Revision);
                Assert.Equal("vp9", completed.Lint!.Measured!.VideoCodec);
                Assert.Equal("opus", completed.Lint.Measured.AudioCodec);
                Assert.Equal("30000/1001", completed.Lint.Measured.FrameRate);
                Assert.Contains(completed.Lint.Findings, finding => finding.Code == "LOUDNESS_TARGET");
                await Assert.ThrowsAsync<IOException>(() => engine.Exports.EnqueueAsync(snapshot, spec));
            }
            await using (var reopened = await EditingEngine.OpenAsync(Path.GetFileName(path), policy))
            {
                var completed = reopened.Exports.Status(job);
                Assert.Equal("completed", completed.State);
                Assert.Equal(EngineJson.Exact(revision), completed.Revision);
                Assert.Equal(job, Assert.Single(reopened.Exports.List()).JobId);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DurableCheckpointRestoresCapturedProjectAndUsesTheNormalHistory()
    {
        string root = Directory.CreateTempSubdirectory("Valencia-M3b-checkpoint-").FullName;
        try
        {
            var baseline = Valencia.Testing.TestProject.Create(path: Path.Combine(root, "missing.mkv")).Snapshot;
            await new ProjectFile().SaveAsync(Path.Combine(root, "project.valencia"), baseline);
            var policy = new WorkspacePolicy(root);
            await using (var first = await EditingEngine.OpenAsync("project.valencia", policy))
            {
                var checkpoint = await first.AgentAsync("agent_checkpoint", Request(first, "create"));
                Assert.True(checkpoint.Ok, checkpoint.Issue?.Message);
            }
            await using (var second = await EditingEngine.OpenAsync("project.valencia", policy))
            {
                var sequence = baseline.Document.Sequences[0];
                var deleted = second.Plan(new(1, baseline.Document.Id, EngineJson.Exact(baseline.Revision),
                    sequence.Id, [new DeletePlan(sequence.Tracks[0].Clips[0].Id)]), true);
                Assert.True(deleted.Committed);
                var restored = await second.AgentAsync("agent_checkpoint", Request(second, "restore"));
                Assert.True(restored.Ok, restored.Issue?.Message);
                Assert.True(DocumentSemantics.Equal(baseline.Document, second.Session.Snapshot.Document));
                Assert.True(second.Undo(second.Session.Snapshot.Revision).Success);
                Assert.Empty(second.Session.Snapshot.Document.Sequences[0].Tracks[0].Clips);
            }
        }
        finally { Directory.Delete(root, recursive: true); }

        static JsonElement Request(EditingEngine engine, string action)
        {
            var current = engine.Session.Snapshot;
            return JsonSerializer.SerializeToElement(new { projectId = current.Document.Id, revision = current.Revision,
                action, durable = true, name = "Original" }, EngineJson.Options);
        }
    }
}
