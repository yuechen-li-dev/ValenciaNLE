using System.Collections.Concurrent;

namespace Valencia.App.Editing;

// The existing native event loop owns UI state. Async I/O continuations return to that same thread.
internal sealed class EditorSynchronizationContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> callbacks = new();
    public override void Post(SendOrPostCallback d, object? state) => callbacks.Enqueue((d, state));
    public void Pump() { while (callbacks.TryDequeue(out var work)) work.Callback(work.State); }
}
