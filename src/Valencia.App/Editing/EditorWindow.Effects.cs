using Machina.Core.Semantics;
using Machina.Standard.Widgets;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;

namespace Valencia.App.Editing;

internal sealed partial class EditorWindow
{
    private Func<EditOperation?>? contextTransition;
    private EditOperation? contextGain;

    private IEnumerable<WidgetOption> ContextEffects(double x, double y)
    {
        contextTransition = null;
        contextGain = null;
        var sequence = Editor.Sequence;
        var viewport = Editor.Viewport;
        if (viewport.TrackAt(sequence, x, y) is not { } row) yield break;
        var track = row.Track;
        var handle = TransitionHandle(track, row.Top, x, y);
        var visible = viewport.Visible(track).ToArray();
        var adjacent = visible.Zip(visible.Skip(1))
            .FirstOrDefault(pair => pair.First.End == pair.Second.Start && Math.Abs(viewport.X(pair.Second.Start) - x) <= 10);
        if (handle is { } transition)
        {
            contextTransition = () => new SetTrackTransition(sequence.Id, track.Id, transition.Window.Transition.Id, null);
            yield return new("effect.transition", "Remove transition", track.State.Locked);
        }
        else if (adjacent.First is { } outgoing && adjacent.Second is { } incoming
            && !track.Transitions.Any(t => t.OutgoingClipId == outgoing.Id && t.IncomingClipId == incoming.Id))
        {
            contextTransition = () => DefaultTransition(sequence, track, outgoing, incoming);
            yield return new("effect.transition", track.Kind == TrackKind.Video ? "Add dissolve" : "Add crossfade", track.State.Locked);
        }
        if (track.Kind != TrackKind.Audio) yield break;
        var points = EditorEffects.Audio(sequence, track.Id)?.Envelope ?? [];
        int index = GainHandle(track, row.Top, x, y);
        var time = GainTime(sequence, x);
        double at = CaptionEvaluation.GainAt(points, time);
        contextGain = new PatchTrackGain(sequence.Id, track.Id, null, EditorEffects.GainPoints(points, index, index < 0 ? new(time, at) : null));
        yield return new("effect.gain", index < 0 ? "Add gain point" : "Remove gain point", track.State.Locked);
    }

    private EditOperation? DefaultTransition(SequenceDocument sequence, TimelineTrack track, TimelineClip outgoing, TimelineClip incoming)
    {
        var id = Guid.NewGuid();
        long cut = incoming.Start.Ticks;
        var rate = sequence.Video.FrameRate;
        long index = TimelineTime.FrameIndex(incoming.Start, rate, TimeRounding.NearestAwayFromZero);
        for (int frames = 4; frames >= 1; frames--)
        {
            long left = Math.Max(0, cut - TimelineTime.FromFrames(Math.Max(0, index - frames), rate).Ticks);
            long right = Math.Max(0, TimelineTime.FromFrames(index + frames, rate).Ticks - cut);
            foreach (var (before, after) in new[] { (left, right), (left, 0L), (0L, right) })
            {
                var transition = new SetTrackTransition(sequence.Id, track.Id, id, new(id, outgoing.Id, incoming.Id, before, after));
                var check = new EffectDrag(Editor.Session.Snapshot.Revision, (_, _) => transition);
                check.Move(Editor.Session, 0, 0);
                if (check.Preview?.Success == true) return transition;
                Editor.Status = check.Preview?.Message ?? "The cut needs available source footage for a transition.";
            }
        }
        return null;
    }

    private bool RunContextEffect(string choice)
    {
        if (choice != "effect.transition" && choice != "effect.gain") return false;
        if ((choice == "effect.transition" ? contextTransition?.Invoke() : contextGain) is { } operation)
            CommitEffect(operation);
        return true;
    }

    private TransformControl? MonitorTarget(double x, double y)
    {
        if (view.HideVideo || Editor.Source is not null || Playback?.IsPlaying == true) return null;
        var picture = EditorEffects.Picture(Editor, view.Layout.Monitor);
        if (!Contains(picture, x, y)) return null;
        var at = EditorEffects.Frame(Editor, Editor.Playhead);
        // Topmost title first, as it draws over the others.
        var targets = EditorEffects.TitlesAt(Editor, at).Reverse().Select(t => new TransformControl(t, at));
        if (EditorEffects.SelectedTransform(Editor) is { Title: null } clip) targets = targets.Append(clip);
        return targets.FirstOrDefault(target =>
        {
            var anchor = EditorEffects.Anchor(target, picture);
            return Math.Abs(x - anchor.X) <= 11 && Math.Abs(y - anchor.Y) <= 11;
        });
    }

    private bool BeginMonitorEffect(double x, double y)
    {
        if (MonitorTarget(x, y) is not { } target) return false;
        if (target.Clip.IsTitle()) Editor.SelectedClip = target.Clip.Id;
        var picture = EditorEffects.Picture(Editor, view.Layout.Monitor);
        var pose = target.Pose;
        var sequence = Editor.SequenceId;
        BeginEffect((endX, endY) => Math.Abs(endX - x) + Math.Abs(endY - y) < 2 ? null
            : target.Write(sequence, pose with
            {
                X = Math.Round(Math.Clamp(pose.X + (endX - x) / picture.Width, -1, 2), 4),
                Y = Math.Round(Math.Clamp(pose.Y + (endY - y) / picture.Height, -1, 2), 4)
            }));
        return true;
    }

    private (TransitionWindow Window, bool Left)? TransitionHandle(TimelineTrack track, double rowTop, double x, double y)
    {
        if (y < rowTop + 22 || y > rowTop + 35 || track.Transitions.IsEmpty) return null;
        var viewport = Editor.Viewport;
        var windows = new TimelineQueries(Editor.Session.Snapshot).TrackTransitions(Editor.SequenceId, track.Id);
        foreach (var window in windows)
        {
            double left = Math.Abs(viewport.X(window.Start) - x), right = Math.Abs(viewport.X(window.End) - x);
            if (Math.Min(left, right) <= 6) return (window, left <= right);
        }
        return null;
    }

    private int GainHandle(TimelineTrack track, double rowTop, double x, double y)
    {
        var points = EditorEffects.Audio(Editor.Sequence, track.Id)?.Envelope ?? [];
        var viewport = Editor.Viewport;
        int best = -1;
        double distance = 36;
        for (int i = LowerGainIndex(points, viewport.Time(x - 6)); i < points.Length; i++)
        {
            double dx = viewport.X(points[i].Time) - x;
            if (dx > 6) break;
            double dy = EditorEffects.GainY(rowTop + 36, viewport.Row - 43, points[i].GainDb) - y;
            double candidate = dx * dx + dy * dy;
            if (candidate <= distance) { distance = candidate; best = i; }
        }
        return best;
    }

    internal static int LowerGainIndex(System.Collections.Immutable.ImmutableArray<GainPoint> points, SequenceTime time)
    {
        int low = 0, high = points.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (points[middle].Time.Ticks < time.Ticks) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private bool BeginTimelineEffect(double x, double y)
    {
        if (razor || Editor.Viewport.TrackAt(Editor.Sequence, x, y) is not { } row) return false;
        var track = row.Track;
        var viewport = Editor.Viewport;
        var sequence = Editor.Sequence;
        if (TransitionHandle(track, row.Top, x, y) is { } handle)
        {
            double edge = viewport.X(handle.Left ? handle.Window.Start : handle.Window.End);
            BeginEffect((endX, _) => Math.Abs(endX - x) < 2 ? null : EditorEffects.Resize(sequence.Id, track.Id,
                handle.Window, handle.Left, EditorEffects.Frame(Editor, viewport.Time(edge + endX - x))));
            return true;
        }
        if (track.Kind != TrackKind.Audio) return false;
        int index = GainHandle(track, row.Top, x, y);
        if (index < 0) return false;
        var points = EditorEffects.Audio(sequence, track.Id)!.Envelope;
        var point = points[index];
        double pointX = viewport.X(point.Time), pointY = EditorEffects.GainY(row.Top + 36, viewport.Row - 43, point.GainDb);
        BeginEffect((endX, endY) => Math.Abs(endX - x) + Math.Abs(endY - y) < 2 ? null
            : new PatchTrackGain(sequence.Id, track.Id, null, EditorEffects.GainPoints(points, index,
                new(Math.Abs(endX - x) < 2 ? point.Time : GainTime(sequence, pointX + endX - x),
                    Math.Abs(endY - y) < 2 ? point.GainDb : EditorEffects.GainAtY(row.Top + 36, viewport.Row - 43, pointY + endY - y)))));
        return true;
    }

    private SequenceTime GainTime(SequenceDocument sequence, double x) =>
        new(Math.Min(EditorEffects.Frame(Editor, Editor.Viewport.Time(x)).Ticks, sequence.Duration.Ticks));

    private void BeginEffect(Func<double, double, EditOperation?> proposal)
    {
        PauseTransport();
        ExitSource();
        Editor.EffectDrag = new(Editor.Session.Snapshot.Revision, proposal);
        CapturePointer();
        lastUi = -1;
    }

    private void ReleaseEffect(double x, double y)
    {
        var drag = Editor.EffectDrag!;
        drag.Move(Editor.Session, x, y);
        Editor.EffectDrag = null;
        ReleasePointer();
        if (drag.Commit(Editor.Session) is not { } result) return;
        Editor.Status = result.Success ? "Effect updated." : result.Message ?? result.Error.ToString();
        if (result.Success) Seek(Editor.Playhead);
    }

    private UiCursor? EffectCursor(double x, double y)
    {
        if (MonitorTarget(x, y) is not null) return UiCursor.Move;
        if (razor || Editor.Viewport.TrackAt(Editor.Sequence, x, y) is not { } row) return null;
        if (TransitionHandle(row.Track, row.Top, x, y) is not null)
            return row.Track.State.Locked ? UiCursor.NotAllowed : UiCursor.ResizeHorizontal;
        if (row.Track.Kind == TrackKind.Audio && GainHandle(row.Track, row.Top, x, y) >= 0)
            return row.Track.State.Locked ? UiCursor.NotAllowed : UiCursor.Move;
        return null;
    }

    private bool EffectAction(string name)
    {
        if (!name.StartsWith("fx:", StringComparison.Ordinal)) return false;
        string action = name[3..];
        if (action is "half" or "normal" or "double" or "freeze")
        {
            if (Editor.Selection is not { } video) return true;
            if (action == "freeze")
            {
                if (EditorEffects.SelectedTransform(Editor) is not { Clip: not null } frame)
                    Editor.Status = "Place the playhead inside the video clip to freeze that frame.";
                else CommitEffect(new FreezeClip(Editor.SequenceId, video.Id, frame.Time, video.Duration));
            }
            else CommitEffect(new SetClipSpeed(Editor.SequenceId, video.Id, action == "double" ? 2 : 1, action == "half" ? 2 : 1));
            return true;
        }
        if (action == "clipGain+" || action == "clipGain-")
        {
            if (Editor.Selection is { } clip)
            {
                double db = clip.Properties.Gain == 0 ? -120 : 20 * Math.Log10((double)clip.Properties.Gain);
                double next = Math.Clamp(db + (action.EndsWith('+') ? 1 : -1), -120, 30);
                CommitAudioEffect(new SetClipGain(Editor.SequenceId, clip.Id, next));
            }
            return true;
        }
        if (action is "fadeIn+" or "fadeIn-" or "fadeOut+" or "fadeOut-" or "cleanup+" or "cleanup-")
        {
            if (Editor.Selection is { } clip)
            {
                var audio = clip.Properties.Audio ?? new();
                int step = action.EndsWith('+') ? 1 : -1;
                long quarter = SequenceTime.TicksPerSecond / 4, longest = clip.Duration.Ticks;
                audio = action[..^1] switch
                {
                    "fadeIn" => audio with { FadeInTicks = Math.Clamp(audio.FadeInTicks + step * quarter, 0, Math.Max(0, longest - audio.FadeOutTicks)) },
                    "fadeOut" => audio with { FadeOutTicks = Math.Clamp(audio.FadeOutTicks + step * quarter, 0, Math.Max(0, longest - audio.FadeInTicks)) },
                    _ => audio with { NoiseReductionDb = Math.Clamp(audio.NoiseReductionDb + step * 3, 0, 24) },
                };
                if (audio != (clip.Properties.Audio ?? new())) CommitAudioEffect(new SetClipProperties(Editor.SequenceId, clip.Id, clip.Properties with { Audio = audio }));
            }
            return true;
        }
        if (action is "eqLow+" or "eqLow-" or "eqLow0" or "eqMid+" or "eqMid-" or "eqMid0" or "eqHigh+" or "eqHigh-" or "eqHigh0")
        {
            if (Editor.Selection is { } clip && Editor.Sequence.Tracks.Any(track => track.Kind == TrackKind.Audio
                && track.Clips.Any(item => item.Id == clip.Id)))
            {
                var audio = clip.Properties.Audio ?? new();
                var eq = audio.Eq ?? new();
                double Adjust(double value) => action[^1] == '0' ? 0
                    : Math.Clamp(value + (action[^1] == '+' ? 1 : -1), -VoiceEq.MaximumGainDb, VoiceEq.MaximumGainDb);
                var next = action[..^1] switch
                {
                    "eqLow" => eq with { LowDb = Adjust(eq.LowDb) },
                    "eqMid" => eq with { MidDb = Adjust(eq.MidDb) },
                    _ => eq with { HighDb = Adjust(eq.HighDb) }
                };
                if (next != eq)
                    CommitAudioEffect(new SetClipProperties(Editor.SequenceId, clip.Id, clip.Properties with
                    {
                        Audio = audio with { Eq = next == new VoiceEq() ? null : next }
                    }));
            }
            return true;
        }
        if (EditorEffects.SelectedTransform(Editor) is not { } target) return true;
        if (action == "prev" || action == "next")
        {
            if (target.Adjacent(action == "next") is { } time) Seek(time);
            return true;
        }
        var pose = target.Pose;
        double direction = action.EndsWith('-') ? -1 : 1;
        pose = action[..^1] switch
        {
            "x" => pose with { X = Math.Clamp(pose.X + direction * .01, -1, 2) },
            "y" => pose with { Y = Math.Clamp(pose.Y + direction * .01, -1, 2) },
            "scale" => pose with { Scale = Math.Clamp(pose.Scale + direction * .05, .1, 10) },
            "rotation" => pose with { Rotation = Math.Clamp(pose.Rotation + direction * 5, -3600, 3600) },
            "opacity" => pose with { Opacity = Math.Clamp(pose.Opacity + direction * .05, 0, 1) },
            _ => pose
        };
        CommitEffect(target.Write(Editor.SequenceId, pose));
        return true;
    }

    private void CommitEffect(EditOperation operation)
    {
        PauseTransport();
        ExitSource();
        if (Editor.Commit(operation).Success) Seek(Editor.Playhead);
    }

    private void CommitAudioEffect(EditOperation operation)
    {
        if (Editor.Commit(operation).Success) RefreshMonitorMix();
    }
}
