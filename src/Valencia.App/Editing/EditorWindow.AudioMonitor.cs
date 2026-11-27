using System.Collections.Immutable;
using Valencia.Media;

namespace Valencia.App.Editing;

internal sealed partial class EditorWindow
{
    internal AudioOutputLevels MonitorLevels => Playback?.OutputLevels ?? default;
    internal ImmutableHashSet<Guid> SoloedAudioTracks => Editor.SoloedAudioTracks;
    private ImmutableHashSet<Guid> CurrentMonitorSoloTracks =>
        Editor.Source is null ? SoloedAudioTracks : ImmutableHashSet<Guid>.Empty;

    internal bool SetAudioTrackSolo(Guid trackId, bool soloed)
    {
        if (!Editor.SetAudioTrackSolo(trackId, soloed)) return false;
        lastUi = -1;
        if (Editor.Source is not null) return true;

        // Reopen at the device playhead to discard the buffered mix from the old selection.
        if (opening is not null || desired)
        {
            if (resume) Seek(Editor.Playhead, play: true, reverse: reverseRequested, keepPicture: true);
        }
        else if (Playback is { IsPlaying: true } playing)
            Seek(playing.Position, play: true, reverse: playing.IsReverse, keepPicture: true);
        return true;
    }
}
