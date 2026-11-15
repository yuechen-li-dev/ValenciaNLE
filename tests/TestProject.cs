using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;

namespace Valencia.Testing;

internal static class TestProject
{
    public static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    public static readonly Guid ProjectId = Id(1), AssetId = Id(2), SequenceId = Id(3), Video1 = Id(4), Video2 = Id(5), Audio = Id(6), ClipId = Id(7);
    public static SequenceTime T(long seconds) => new(checked(seconds * SequenceTime.TicksPerSecond));
    public static SourcePosition S(long seconds) => new(new(checked(seconds * 90_000), 1, 90_000));
    public static MediaAsset Asset(string? path = null) => new(AssetId, "Source", path ?? Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Valencia-inert.mkv")), "matroska",
        [new(0, TrackKind.Video, "h264", 1, 90_000, new(0, 1, 90_000), new(1_800_000, 1, 90_000), 1920, 1080),
         new(1, TrackKind.Audio, "aac", 1, 48_000, new(0, 1, 48_000), new(960_000, 1, 48_000), SampleRate: 48000, Channels: 2)]);
    public static TimelineClip Clip(Guid? id = null, long start = 0, long sourceIn = 0, long sourceOut = 10) =>
        new(id ?? ClipId, AssetId, 0, T(start), S(sourceIn), S(sourceOut), new("Clip"));
    public static EditingSession Create(bool clip = true, int limit = 100, string? path = null)
    {
        var session = new EditingSession(ProjectDocument.Create(ProjectId, "Test project"), limit);
        var result = session.Execute(new EditTransaction(Id(100), 1, 0,
        [new(Id(101), 1, new AddAsset(Asset(path))), new(Id(102), 1, new CreateSequence(SequenceId, "Main", new(1920, 1080, new(30000, 1001)), 48000)),
         new(Id(103), 1, new AddTrack(SequenceId, Video1, TrackKind.Video, 0, "V1")),
         new(Id(104), 1, new AddTrack(SequenceId, Video2, TrackKind.Video, 1, "V2")),
         new(Id(105), 1, new AddTrack(SequenceId, Audio, TrackKind.Audio, 2, "A1"))]));
        if (!result.Success) throw new InvalidOperationException(result.Message);
        if (clip && !Apply(session, new InsertClip(SequenceId, Video1, Clip())).Success) throw new InvalidOperationException("Fixture insert failed.");
        return session;
    }
    public static EditResult Apply(EditingSession session, EditOperation operation) => session.Execute(new EditCommand(Guid.NewGuid(), 1, operation, session.Snapshot.Revision));
    public static SessionRequest Request(EditingSession session) => new(Guid.NewGuid(), 1, session.Snapshot.Revision);
    public static TimelineQueries Query(EditingSession session) => new(session.Snapshot);
}
