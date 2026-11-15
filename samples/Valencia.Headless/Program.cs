using System.Text.Json;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Persistence;

// No graphics, codec, window or audio-device references. This fixture demonstrates editing semantics, not decoding.
string directory = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/local/m1a-demo");
Directory.CreateDirectory(directory);
string media = Path.Combine(directory, "source.metadata-fixture");
if (!File.Exists(media)) await File.WriteAllTextAsync(media, "Generated metadata-only test fixture. Not playable video.");
var session = new EditingSession(ProjectDocument.Create(Id(1), "M1a demonstration"));
var asset = new MediaAsset(Id(2), "Sample", media, "metadata-fixture",
    [new(0, TrackKind.Video, "fixture", 1, 90000, new(0, 1, 90000), new(1800000, 1, 90000)),
     new(1, TrackKind.Audio, "fixture", 1, 48000, new(0, 1, 48000), new(960000, 1, 48000))]);
Check(session.Execute(new EditTransaction(Id(100), 1, 0,
    [new(Id(101), 1, new AddAsset(asset)),
     new(Id(102), 1, new CreateSequence(Id(3), "Main", new(1920, 1080, new(30000, 1001)), 48000)),
     new(Id(103), 1, new AddTrack(Id(3), Id(4), TrackKind.Video, 0, "V1")),
     new(Id(104), 1, new AddTrack(Id(3), Id(5), TrackKind.Audio, 1, "A1")),
     new(Id(105), 1, new InsertClip(Id(3), Id(4), new(Id(6), Id(2), 0, T(0), S(0), S(10), new("Sample clip"))))])));
Apply(new SplitClip(Id(3), Id(6), new(T(2).Ticks + 1), Id(7)), 106);
var right = new TimelineQueries(session.Snapshot).Clips(Id(3), Id(4))[1];
Apply(new TrimClip(Id(3), Id(7), T(3), right.SourceIn.Shift(T(1)), S(9)), 107);
Apply(new MoveClip(Id(3), Id(7), Id(4), T(4)), 108);
var edited = session.Snapshot.Document;
Check(session.Undo(new(Id(109), 1, session.Snapshot.Revision)));
Check(session.Redo(new(Id(110), 1, session.Snapshot.Revision)));
if (!DocumentSemantics.Equal(edited, session.Snapshot.Document)) throw new InvalidOperationException("Undo/redo did not restore semantic state.");
var files = new ProjectFile();
string project = Path.Combine(directory, "demonstration.valencia");
await files.SaveAsync(project, session);
var loaded = await files.LoadAsync(project);
if (!DocumentSemantics.Equal(session.Snapshot.Document, loaded.Snapshot.Document) || loaded.Snapshot.Revision != session.Snapshot.Revision
    || loaded.MissingMedia.Length != 0) throw new InvalidOperationException("Reopened project differs.");
var active = new TimelineQueries(loaded.Snapshot).Evaluate(Id(3), T(5));
if (active.Length != 1 || active[0].Clip.Id != Id(7)) throw new InvalidOperationException("Timeline evaluation differs.");
var expectedSource = new TimelineQueries(session.Snapshot).Evaluate(Id(3), T(5))[0].SourcePosition;
if (!active[0].SourcePosition.EquivalentTo(expectedSource)) throw new InvalidOperationException("Source mapping differs.");
await File.WriteAllBytesAsync(Path.Combine(directory, "semantic-debug.json"), new ProjectJsonSerializer().Serialize(loaded.Snapshot));
var result = new
{
    outcome = "passed", project, revision = loaded.Snapshot.Revision, semanticEquality = true,
    fixtureOnly = true, missingMedia = loaded.MissingMedia.Length, activeClipId = active[0].Clip.Id,
    source = active[0].SourcePosition, durationTicks = new TimelineQueries(loaded.Snapshot).Duration(Id(3)).Ticks,
    workflow = "create → register → sequence/tracks → insert → split → trim → move → undo/redo → save → reopen → evaluate"
};
string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), json);
Console.WriteLine(json);
return;

static Guid Id(int id) => new(id, 0, 0, new byte[8]);
static SequenceTime T(long seconds) => new(checked(seconds * SequenceTime.TicksPerSecond));
static SourcePosition S(long seconds) => new(new(seconds * 90000, 1, 90000));
static void Check(EditResult result) { if (!result.Success) throw new InvalidOperationException($"{result.Error}: {result.Message}"); }
void Apply(EditOperation operation, int id) => Check(session.Execute(new EditCommand(Id(id), 1, operation, session.Snapshot.Revision)));
