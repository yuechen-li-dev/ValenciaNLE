using System.Windows.Forms;
using Valencia.Core;
using Valencia.Core.Editing;
using Valencia.Core.Timeline;

namespace Valencia.App.Editing;

/// <summary>Title clips from the editor: create at the playhead, edit the words, cycle the look.</summary>
internal sealed partial class EditorWindow
{
    private static readonly string[] TitleStyleOrder = [TitleStyles.Card, TitleStyles.LowerThird, TitleStyles.Outline];

    /// <summary>
    /// A five-second Valencia title card at the playhead, on the highest free video track (a new one when none is free),
    /// as Premiere's New Text Layer does. The new clip is selected so Edit text and Style act on it.
    /// </summary>
    internal EditResult NewTitle(string text = "TITLE", string subtitle = "")
    {
        var rate = Editor.Sequence.Video.FrameRate;
        var at = EditorEffects.Frame(Editor, Editor.TimelinePlayhead);
        var length = TimelineTime.FromFrames(Math.Max(1, TimelineTime.FrameIndex(new(5 * SequenceTime.TicksPerSecond), rate, TimeRounding.NearestAwayFromZero)), rate);
        var id = Guid.NewGuid();
        var result = Editor.Commit(new SetTitle(Editor.SequenceId, new TitleCue(id, text, at, new(checked(at.Ticks + length.Ticks)),
            Style: TitleStyles.Card, Subtitle: subtitle)));
        if (result.Success) { Editor.SelectedClip = id; Seek(Editor.Playhead); Editor.Status = "Title added. Edit text changes the words; Style cycles card, lower third and outline."; }
        else Editor.Status = "Cannot add title: " + (result.Message ?? result.Error.ToString());
        return result;
    }

    internal EditResult? SetTitleStyle(string? style = null)
    {
        if (Editor.Selection is not { Title: { } content } clip) { Editor.Status = "Select a title clip first."; return null; }
        style ??= TitleStyleOrder[(Array.IndexOf(TitleStyleOrder, content.Style) + 1) % TitleStyleOrder.Length];
        var result = Editor.Commit(new SetTitleContent(Editor.SequenceId, clip.Id, content with { Style = style }));
        if (result.Success) Seek(Editor.Playhead);
        Editor.Status = result.Success ? "Title style: " + EditorView.TitleStyleName(style) + "." : "Cannot change title: " + result.Message;
        return result;
    }

    internal EditResult? EditTitleText(string? text = null, string? subtitle = null)
    {
        if (Editor.Selection is not { Title: { } content } clip) { Editor.Status = "Select a title clip first."; return null; }
        if (text is null)
        {
            Playback?.Pause(); CancelGesture(); keys.Reset();
            using var form = new Form { Text = "Edit title", Width = 480, Height = 190, StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
            var head = new TextBox { Left = 96, Top = 14, Width = 352, Text = content.Text, MaxLength = 120 };
            var sub = new TextBox { Left = 96, Top = 48, Width = 352, Text = content.Subtitle, MaxLength = 120 };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 292, Top = 92, Width = 74 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 374, Top = 92, Width = 74 };
            form.Controls.AddRange([new Label { Text = "Title", Left = 12, Top = 17, Width = 80 }, head,
                new Label { Text = "Subtitle", Left = 12, Top = 51, Width = 80 }, sub, ok, cancel]);
            form.AcceptButton = ok; form.CancelButton = cancel;
            bool accepted = form.ShowDialog() == DialogResult.OK;
            keys.Reset();
            if (!accepted) return null;
            text = head.Text; subtitle = sub.Text;
        }
        text = text.Trim(); subtitle = (subtitle ?? content.Subtitle).Trim();
        if (text.Length == 0) { Editor.Status = "A title needs at least one character."; return null; }
        var result = Editor.Commit(new SetTitleContent(Editor.SequenceId, clip.Id, content with { Text = text, Subtitle = subtitle }));
        if (result.Success) Seek(Editor.Playhead);
        Editor.Status = result.Success ? "Title updated." : "Cannot change title: " + result.Message;
        return result;
    }
}
