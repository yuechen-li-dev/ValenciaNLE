namespace Valencia.Core;

public sealed record MediaStreamInfo(int Index, string Kind, string Codec, string Details);

public sealed record MediaInfo(
    string Path,
    string Container,
    double DurationSeconds,
    double StartSeconds,
    int Width,
    int Height,
    double NominalFrameRate,
    int VideoStreamIndex,
    int? AudioStreamIndex,
    IReadOnlyList<MediaStreamInfo> Streams);

// Transport state is distinct from the future undoable project-edit command model.
public enum TransportAction
{
    Open,
    TogglePlayback,
    SeekBackward,
    SeekForward,
    SeekToStart
}
