using Machina.Layout.Geometry;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;
using Valencia.Rendering;

namespace Valencia.App.Editing;

internal sealed partial class EditorView
{
    private static void DrawTransformHandles(List<CanvasRectangle> quads, EditorDocument editor, Rect monitor)
    {
        if (editor.Source is not null || editor.ScrubTarget is not null) return;
        var picture = EditorEffects.Picture(editor, monitor);
        var time = EditorEffects.Frame(editor, editor.Playhead);
        foreach (var title in editor.Sequence.Titles.Where(t => TitleMotion.At(t, time) is { Opacity: > 0 }))
            Draw(new(null, title, time), title.Id == editor.SelectedTitle);
        if (EditorEffects.SelectedTransform(editor) is { Clip: not null } clip) Draw(clip, true);

        void Draw(TransformControl target, bool selected)
        {
            var (x, y) = EditorEffects.Anchor(target, picture);
            Cross(x, y, selected ? Ui.Cyan : Ui.Bone);
            var operation = editor.EffectDrag?.Operation;
            var curve = operation switch
            {
                SetTitleTransform title when title.TitleId == target.Title?.Id => title.Curve,
                SetClipTransform clipTransform when clipTransform.ClipId == target.Clip?.Id => clipTransform.Curve,
                _ => null
            };
            if (curve is null) return;
            var candidate = target.Title is { } titleCue ? target with { Title = titleCue with { Transform = curve } }
                : target with { Clip = target.Clip! with { Properties = target.Clip.Properties with
                    { Video = (target.Clip.Properties.Video ?? new()) with { Transform = new(ClipTransform.KeyOrigin(target.Clip), curve) } } } };
            (x, y) = EditorEffects.Anchor(candidate, picture);
            Cross(x, y, editor.EffectDrag?.Preview?.Success == true ? Ui.Orange : Ui.Alert);
        }

        void Cross(double x, double y, uint color)
        {
            if (x < picture.X + 8 || x > picture.X + picture.Width - 8 || y < picture.Y + 8 || y > picture.Y + picture.Height - 8) return;
            quads.Add(new((float)x - 8, (float)y - 1, 16, 2, color));
            quads.Add(new((float)x - 1, (float)y - 8, 2, 16, color));
        }
    }

    private static void DrawTimelineEffects(List<CanvasRectangle> quads, EditorDocument editor, SequenceDocument sequence,
        TimelineQueries queries, TimelineTrack track, float top)
    {
        var viewport = editor.Viewport;
        double left = viewport.ClipLeft, right = viewport.ClipRight;
        var preview = editor.EffectDrag;
        if (!track.Transitions.IsEmpty)
        {
            foreach (var window in queries.TrackTransitions(sequence.Id, track.Id))
            {
                var replacement = preview?.Operation as SetTrackTransition;
                bool active = replacement?.TransitionId == window.Transition.Id;
                var current = active && replacement!.Transition is { } transition ? window with { Transition = transition } : window;
                double x1 = viewport.X(current.Start), x2 = viewport.X(current.End);
                double first = Math.Max(left, x1), last = Math.Min(right, x2);
                if (last <= first) continue;
                uint color = active ? (preview!.Preview?.Success == true ? Ui.Cyan : Ui.Alert) : Ui.Orange;
                quads.Add(new((float)first, top + 23, (float)(last - first), 11, Ui.Backing));
                quads.Add(new((float)first, top + 28, (float)(last - first), 2, color));
                foreach (double edge in new[] { x1, x2 })
                    if (edge >= left + 2 && edge <= right - 2) quads.Add(new((float)edge - 2, top + 23, 4, 11, color));
            }
        }
        if (track.Kind != TrackKind.Audio) return;
        var patch = preview?.Operation as PatchTrackGain;
        bool editing = patch?.TrackId == track.Id;
        var points = (editing ? patch!.Envelope : EditorEffects.Audio(sequence, track.Id)?.Envelope) ?? [];
        if (points.IsEmpty) return;
        uint ink = editing ? (preview!.Preview?.Success == true ? Ui.Cyan : Ui.Alert) : Ui.Cyan;
        double graphTop = top + 36, height = viewport.Row - 43;
        double edgeRight = Math.Min(right, viewport.X(sequence.Duration));
        double previousY = EditorEffects.GainY(graphTop, height, CaptionEvaluation.GainAt(points, viewport.Time(left)));
        for (double x = left; x + 1 < edgeRight; x += 5)
        {
            double next = Math.Min(x + 5, edgeRight);
            double y = EditorEffects.GainY(graphTop, height, CaptionEvaluation.GainAt(points, viewport.Time(next)));
            quads.Add(new((float)x, (float)Math.Min(y, previousY), (float)(next - x), (float)Math.Max(1, Math.Abs(y - previousY)), ink));
            previousY = y;
        }
        double drawn = left - 6;
        for (int index = EditorWindow.LowerGainIndex(points, viewport.Origin); index < points.Length; index++)
        {
            var point = points[index];
            double x = viewport.X(point.Time);
            if (x > right - 3) break;
            if (x < left + 3 || x - drawn < 5) continue;
            double y = EditorEffects.GainY(graphTop, height, point.GainDb);
            quads.Add(new((float)x - 3, (float)y - 3, 6, 6, Ui.Ink));
            quads.Add(new((float)x - 2, (float)y - 2, 4, 4, ink));
            drawn = x;
        }
    }
}
