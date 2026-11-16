using Machina.Presentation;
using Valencia.Core;
using Valencia.App.Editing;
using Xunit;

namespace Valencia.Tests;

public sealed class EditorRecoveryTests
{
    [Fact]
    public void FailedPreparationPreservesLastValidFrameAndDocument()
    {
        var editor = new EditorDocument();
        var view = new EditorView();
        var prepared = view.Prepare(editor, new(editor), false, false);
        var snapshot = editor.Session.Snapshot;

        var result = view.TryPrepare(editor, new(editor), false, false, 0, 0);

        Assert.True(result is Err<EditorFailure>);
        Assert.Same(prepared, view.Prepared);
        Assert.Same(snapshot, editor.Session.Snapshot);
        Assert.True(view.TryPrepare(editor, new(editor), false, false, 1280, 720)
            is Ok<Machina.Pipeline.MachinaPreparedPresentation>);
    }

    [Fact]
    public async Task LegacyFailuresAndCancellationBecomeTypedResults()
    {
        var failure = await EditorOperation.TryAsync("open", () => throw new IOException("Invalid media"));
        var cancelled = await EditorOperation.TryAsync("save", () => Task.FromCanceled(new CancellationToken(true)));
        Assert.True(failure is Err<EditorFailure> { Error: { Operation: "open", Cancelled: false } });
        Assert.True(cancelled is Err<EditorFailure> { Error.Cancelled: true });
        Assert.True(await EditorOperation.TryAsync("retry", () => Task.CompletedTask) is Ok<Completed>);
    }

    [Fact]
    public void UiContinuationFailureDoesNotDiscardSubsequentWork()
    {
        var context = new EditorSynchronizationContext();
        var failures = new List<EditorFailure>();
        bool reached = false;
        context.Post(_ => throw new Machina.Layout.Diagnostics.LayoutError("probe", "Invalid frame"), null);
        context.Post(_ => reached = true, null);
        context.Pump(failures.Add);
        Assert.True(reached);
        Assert.Single(failures);
        Assert.IsType<Machina.Layout.Diagnostics.LayoutError>(failures[0].Cause);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(20, 720)]
    [InlineData(320, 200)]
    [InlineData(2559, 1)]
    [InlineData(390, 844)]
    [InlineData(2560, 1369)]
    public void ResizeNeverBuildsNegativeLayoutFrames(int width, int height)
    {
        var editor = new EditorDocument();
        var view = new EditorView();
        var snapshot = editor.Session.Snapshot;
        var prepared = view.Prepare(editor, new(editor), false, false, width, height);

        Assert.NotEmpty(prepared.PresentationFrame.Operations);
        Assert.Equal(width < 320 || height < 480, view.IsCompact);
        Assert.Same(snapshot, editor.Session.Snapshot);
        Assert.All(prepared.Resolved.Nodes.Values, node =>
        {
            Assert.True(node.Rect.Width >= 0);
            Assert.True(node.Rect.Height >= 0);
        });
    }
}
