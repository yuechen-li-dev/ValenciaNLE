namespace Valencia.Media;

/// <summary>
/// Per-generation preview pacing. The first frame can prebuffer before the device
/// exists; later frames publish only when due on the actual transport clock.
/// This is never used by offline export.
/// </summary>
internal sealed class PreviewFrameClock
{
    private readonly TaskCompletionSource<Func<long>> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Attach(Func<long> readTicks)
    {
        ArgumentNullException.ThrowIfNull(readTicks);
        if (!ready.TrySetResult(readTicks)) throw new InvalidOperationException("Preview clock is already attached.");
    }

    public async Task WaitUntilDueAsync(long frameTicks, CancellationToken cancellation)
    {
        var clock = await ready.Task.WaitAsync(cancellation).ConfigureAwait(false);
        while (frameTicks > clock())
            await Task.Delay(2, cancellation).ConfigureAwait(false);
    }

    // M0's existing seconds-based transport only. Sequence playback uses exact ticks.
    public static long TransportTicks(double seconds) => checked((long)Math.Round(
        seconds * Valencia.Core.SequenceTime.TicksPerSecond, MidpointRounding.AwayFromZero));
}
