using Valencia.Core;
using Valencia.Core.Timeline;
using Machina.Core.Semantics;

namespace Valencia.App.Editing;

internal sealed class TimelineViewport
{
    public const double Left = 166, Right = 1256, Top = 398, RowHeight = 49;
    public double ClipLeft { get; private set; } = Left;
    public double ClipRight { get; private set; } = Right;
    public double TrackTop { get; private set; } = Top;
    public double TrackBottom { get; private set; } = 645;
    /// <summary>Current track row height: rows grow to fill the timeline when there are few tracks (up to MaxRowHeight).</summary>
    public double Row { get; private set; } = RowHeight;
    public const double MaxRowHeight = 120;
    public int VisibleTracks => Math.Max(0, (int)((TrackBottom - TrackTop) / Row));
    public void Configure(EditorLayout layout, int trackCount = 0)
    {
        ClipLeft = layout.ClipLeft; ClipRight = layout.ClipRight;
        TrackTop = layout.TrackTop; TrackBottom = layout.TrackBottom;
        Row = trackCount <= 0 ? RowHeight : Math.Clamp(Math.Floor((TrackBottom - TrackTop) / trackCount), RowHeight, MaxRowHeight);
    }
    public SequenceTime Origin { get; private set; }
    public double PixelsPerSecond { get; private set; } = 90;
    public int FirstTrack { get; set; }
    public void Reset() { Origin = default; PixelsPerSecond = 90; FirstTrack = 0; }
    public double X(SequenceTime time) => ClipLeft + (time.Ticks - Origin.Ticks) / (double)SequenceTime.TicksPerSecond * PixelsPerSecond;
    public SequenceTime Time(double x) => new((long)Int128.Clamp((Int128)Origin.Ticks + (Int128)Math.Round((x - ClipLeft) / PixelsPerSecond * SequenceTime.TicksPerSecond), 0, long.MaxValue));
    public void Scroll(double pixels) => SetOrigin((Int128)Origin.Ticks + (Int128)Math.Round(pixels / PixelsPerSecond * SequenceTime.TicksPerSecond));
    public void Zoom(double factor, double anchor)
    {
        var time = Time(anchor);
        PixelsPerSecond = Math.Clamp(PixelsPerSecond * factor, 4, 1600);
        SetOrigin((Int128)time.Ticks - (Int128)Math.Round((anchor - ClipLeft) / PixelsPerSecond * SequenceTime.TicksPerSecond));
    }
    private void SetOrigin(Int128 ticks) => Origin = new((long)Int128.Clamp(ticks, 0, long.MaxValue));
    public IEnumerable<TimelineClip> Visible(TimelineTrack track)
    {
        long start = Origin.Ticks, end = Time(ClipRight).Ticks;
        int lo = 0, hi = track.Clips.Length;
        while (lo < hi) { int mid = (lo + hi) / 2; if (track.Clips[mid].End.Ticks <= start) lo = mid + 1; else hi = mid; }
        for (int i = lo; i < track.Clips.Length && track.Clips[i].Start.Ticks < end; i++) yield return track.Clips[i];
    }
    public (TimelineTrack Track, TimelineClip Clip, int Edge)? Hit(SequenceDocument sequence, double x, double y)
    {
        int visibleRow = (int)Math.Floor((y - TrackTop) / Row);
        int row = visibleRow + FirstTrack;
        if (x < ClipLeft || x > ClipRight || y < TrackTop || visibleRow >= VisibleTracks || row < 0 || row >= sequence.Tracks.Length) return null;
        var track = sequence.Tracks[row];
        foreach (var clip in Visible(track))
        {
            double left = Math.Max(ClipLeft, X(clip.Start)), right = Math.Min(ClipRight, X(clip.End));
            if (x >= left && x <= right) return (track, clip, Math.Abs(x - X(clip.Start)) <= 7 ? -1 : Math.Abs(x - X(clip.End)) <= 7 ? 1 : 0);
        }
        return null;
    }

    public UiCursor? Cursor(SequenceDocument sequence, double x, double y, double rulerY, bool razor)
    {
        if (Hit(sequence, x, y) is { } hit)
        {
            if (hit.Track.State.Locked) return UiCursor.NotAllowed;
            if (razor) return UiCursor.Crosshair;
            return hit.Edge == 0 ? UiCursor.Move : UiCursor.ResizeHorizontal;
        }
        if (x >= ClipLeft && x < ClipRight && y >= rulerY && y < TrackTop) return UiCursor.Hand;
        return null;
    }
}
