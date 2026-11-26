using System.Collections.Immutable;
using Machina.Layout.Geometry;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;

namespace Valencia.App.Editing;

internal sealed class EffectDrag(long revision, Func<double, double, EditOperation?> propose)
{
    public EditOperation? Operation { get; private set; }
    public EditResult? Preview { get; private set; }

    public void Move(EditingSession session, double x, double y)
    {
        var operation = propose(x, y);
        if (Equals(Operation, operation)) return;
        Operation = operation;
        Preview = operation is null ? null : session.Preview(Transaction(operation));
    }

    public EditResult? Commit(EditingSession session) =>
        Operation is { } operation && Preview?.Success == true ? session.Execute(Transaction(operation)) : Preview;

    private EditTransaction Transaction(EditOperation operation) =>
        new(Guid.NewGuid(), 1, revision, [new(Guid.NewGuid(), 1, operation)]);
}

internal sealed record TransformControl(TimelineClip? Clip, TitleCue? Title, SequenceTime Time)
{
    public TransformCurve? Curve => Title?.Transform ?? Clip?.Properties.Video?.Transform?.Curve;
    private long Offset => Title is not null ? Time.Ticks - Title.Start.Ticks
        : Clip!.MapToSource(Time).Since(Clip.Properties.Video?.Transform?.Origin ?? Clip.SourceIn).Ticks;

    public TransformPose Pose => Curve?.At(Offset) ?? (Title is { } title
        ? new(title.X, title.Y, 1, 0, 1)
        : new(Clip!.Properties.Video?.X ?? .5, Clip.Properties.Video?.Y ?? .5, Clip.Properties.Video?.Scale ?? 1, 0, 1));

    public EditOperation Write(Guid sequence, TransformPose pose)
    {
        long rebase = Clip is { Properties.Video.Transform: { } existing } clip
            ? existing.Origin.Since(ClipTransform.KeyOrigin(clip)).Ticks : 0;
        var keys = Curve?.Keys ?? [];
        var key = new TransformKey(checked(Offset + rebase), pose.X, pose.Y, pose.Scale, pose.Rotation, pose.Opacity,
            keys.FirstOrDefault(k => k.At == Offset)?.Interpolation ?? "linear");
        var curve = new TransformCurve([.. keys.Where(k => k.At != Offset)
            .Select(k => rebase == 0 ? k : k with { At = checked(k.At + rebase) }).Append(key).OrderBy(k => k.At)]);
        return Title is { } title ? new SetTitleTransform(sequence, title.Id, curve)
            : new SetClipTransform(sequence, Clip!.Id, curve, PreserveOrigin: true);
    }

    public SequenceTime? Adjacent(bool next)
    {
        var origin = Clip?.Properties.Video?.Transform?.Origin;
        if (Clip?.Retime?.Hold is not null) return null;
        var keys = Curve?.Keys ?? [];
        var candidates = Title is { } title
            ? keys.Select(k => new SequenceTime(checked(title.Start.Ticks + k.At)))
            : keys.Select(k => origin!.Value.Shift(new(k.At)))
                .Where(p => p.CompareTo(Clip!.SourceIn) >= 0 && p.CompareTo(Clip.SourceOut) < 0)
                .Select(p => Clip!.MapToTimeline(p));
        long? ticks = next ? candidates.Where(t => t.Ticks > Time.Ticks).Min(t => (long?)t.Ticks)
            : candidates.Where(t => t.Ticks < Time.Ticks).Max(t => (long?)t.Ticks);
        return ticks is { } value ? new(value) : null;
    }
}

internal static class EditorEffects
{
    public static TransformControl? SelectedTransform(EditorDocument editor)
    {
        if (editor.Source is not null || editor.ScrubTarget is not null) return null;
        var at = Frame(editor, editor.Playhead);
        var sequence = editor.Sequence;
        if (editor.SelectedTitle is { } id && sequence.Titles.FirstOrDefault(t => t.Id == id) is { } title
            && TitleMotion.At(title, at) is not null)
            return new(null, title, at);
        if (editor.Selection is { } clip && clip.Start.Ticks <= at.Ticks && at.Ticks < clip.End.Ticks
            && sequence.Tracks.Any(track => track.Kind == TrackKind.Video && track.Clips.Any(c => c.Id == clip.Id)))
            return new(clip, null, at);
        return null;
    }

    public static SequenceTime Frame(EditorDocument editor, SequenceTime time) =>
        TimelineTime.SnapToFrame(time, editor.Sequence.Video.FrameRate, TimeRounding.NearestAwayFromZero);

    public static Rect Picture(EditorDocument editor, Rect monitor)
    {
        var sequence = editor.Sequence;
        var shape = sequence.Creator?.Profiles.FirstOrDefault(p => p.Id == editor.SelectedProfile);
        double width = shape?.Width ?? sequence.Video.Width, height = shape?.Height ?? sequence.Video.Height;
        double scale = Math.Min(monitor.Width / width, monitor.Height / height);
        return new(monitor.X + (monitor.Width - width * scale) / 2, monitor.Y + (monitor.Height - height * scale) / 2,
            width * scale, height * scale);
    }

    public static (double X, double Y) Anchor(TransformControl control, Rect picture)
    {
        var pose = control.Pose;
        if (control.Title is { } title && TitleMotion.At(title, control.Time) is { } motion)
            pose = pose with { X = pose.X + motion.OffsetX, Y = pose.Y + motion.OffsetY };
        if (control.Clip?.Properties.Video?.Motion is { } clipMotion)
        {
            var placement = clipMotion.Apply(new(X: pose.X, Y: pose.Y, Scale: pose.Scale),
                control.Time.Ticks - control.Clip.Start.Ticks, control.Clip.Duration.Ticks);
            pose = pose with { X = placement.X, Y = placement.Y };
        }
        return (picture.X + pose.X * picture.Width, picture.Y + pose.Y * picture.Height);
    }

    public static TrackAudio? Audio(SequenceDocument sequence, Guid track) =>
        sequence.Creator?.Audio.FirstOrDefault(audio => audio.TrackId == track);

    // Most mix adjustments are near unity. Reserve the bottom ten percent for very low levels.
    public static double GainY(double top, double height, double db) =>
        top + height * (db < -36 ? .9 + (-36 - db) / 840 : (30 - db) * .9 / 66);

    public static double GainAtY(double top, double height, double y)
    {
        double fraction = Math.Clamp((y - top) / height, 0, 1);
        return Math.Round(fraction > .9 ? -36 - (fraction - .9) * 840 : 30 - fraction * 66 / .9, 1);
    }

    public static ImmutableArray<GainPoint> GainPoints(ImmutableArray<GainPoint> existing, int replace, GainPoint? point)
    {
        if (replace >= 0) existing = existing.RemoveAt(replace);
        if (point is null) return existing;
        if (replace < 0 && existing.Any(p => p.Time == point.Time)) return existing;
        // The reducer rejects duplicate ticks. Moving a point onto a neighbor never erases the neighbor.
        return [.. existing.Append(point).OrderBy(p => p.Time.Ticks)];
    }

    public static SetTrackTransition Resize(Guid sequence, Guid track, TransitionWindow window, bool left, SequenceTime time) =>
        new(sequence, track, window.Transition.Id, left
            ? window.Transition with { BeforeTicks = Math.Max(0, window.Cut.Ticks - time.Ticks) }
            : window.Transition with { AfterTicks = Math.Max(0, time.Ticks - window.Cut.Ticks) });
}
