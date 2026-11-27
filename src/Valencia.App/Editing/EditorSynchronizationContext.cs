using System.Collections.Concurrent;
using Valencia.Core;

namespace Valencia.App.Editing;

// The existing native event loop owns UI state. Async I/O continuations return to that same thread.
internal sealed class EditorSynchronizationContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> callbacks = new();
    public override void Post(SendOrPostCallback d, object? state) => callbacks.Enqueue((d, state));
    public void Pump(Action<EditorFailure>? report = null)
    {
        while (callbacks.TryDequeue(out var work))
        {
            var result = EditorOperation.Try("UI continuation", () =>
            {
                work.Callback(work.State);
                return new Completed();
            });
            if (result.Case is Err<EditorFailure> failed)
            {
                if (report is not null) report(failed.Error);
                else Console.Error.WriteLine(failed.Error.Cause);
            }
        }
    }
}
