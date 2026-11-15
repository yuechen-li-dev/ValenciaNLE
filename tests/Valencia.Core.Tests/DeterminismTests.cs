using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Xunit;
using static Valencia.Testing.TestProject;

namespace Valencia.Core.Tests;

public sealed class DeterminismTests
{
    [Theory]
    [InlineData(17)][InlineData(103)][InlineData(2026)]
    public void SeededCommandSequencesActuallyReplayThroughDispatcher(int seed)
    {
        var initial = Create().Snapshot;
        var original = new EditingSession(initial, 256);
        var commands = new List<EditCommand>();
        var outcomes = new List<bool>();
        var documents = new List<ProjectDocument>();
        var random = new Random(seed);
        int nextId = 10000;
        for (int i = 0; i < 100; i++)
        {
            var clips = Query(original).Tracks(SequenceId).SelectMany(t => t.Clips).ToArray();
            var clip = clips.Length == 0 ? null : clips[random.Next(clips.Length)];
            EditOperation operation = random.Next(6) switch
            {
                0 when clip is not null => new MoveClip(SequenceId, clip.Id, random.Next(2) == 0 ? Video1 : Video2, T(random.Next(20))),
                1 when clip is not null => new SplitClip(SequenceId, clip.Id, new(clip.Start.Ticks + clip.Duration.Ticks / 2), Id(nextId++)),
                2 when clip is not null => new DeleteClip(SequenceId, clip.Id),
                3 when clip is not null => new TrimClip(SequenceId, clip.Id, clip.Start, clip.SourceIn, clip.SourceIn), // deliberately invalid
                _ => new InsertClip(SequenceId, random.Next(2) == 0 ? Video1 : Video2, Clip(Id(nextId++), random.Next(20), 0, random.Next(1, 5)))
            };
            var before = original.Snapshot;
            var command = new EditCommand(Id(30000 + i), 1, operation, before.Revision);
            var result = original.Execute(command);
            Assert.Equal(before.Revision + (result.Success ? 1 : 0), result.Revision);
            if (!result.Success) Assert.Same(before, original.Snapshot);
            else
            {
                var after = original.Snapshot;
                Assert.True(original.Undo(Request(original)).Success);
                Assert.True(DocumentSemantics.Equal(before.Document, original.Snapshot.Document));
                Assert.True(original.Redo(Request(original)).Success);
                Assert.True(DocumentSemantics.Equal(after.Document, original.Snapshot.Document));
            }
            commands.Add(command);
            outcomes.Add(result.Success);
            documents.Add(original.Snapshot.Document);
        }
        // Replay the same operations/identities with their recorded expected revisions, including undo/redo commits.
        var replay = new EditingSession(initial, 256);
        for (int i = 0; i < commands.Count; i++)
        {
            var result = replay.Execute(commands[i]);
            Assert.Equal(outcomes[i], result.Success);
            if (result.Success)
            {
                Assert.True(replay.Undo(Request(replay)).Success);
                Assert.True(replay.Redo(Request(replay)).Success);
            }
            Assert.True(DocumentSemantics.Equal(documents[i], replay.Snapshot.Document));
        }
        Assert.Equal(original.Snapshot.Revision, replay.Snapshot.Revision);
        Assert.True(outcomes.Any(x => x) && outcomes.Any(x => !x));
    }
}
