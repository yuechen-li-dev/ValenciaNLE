using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Media;

/// <summary>
/// Worker-confined paused preview. Reuses video cursors for one immutable snapshot/profile and never
/// starts an audio decoder, builds an audio seek index, or opens an audio device. Calls must be serialized;
/// cancel/join the current call before disposing. Timeline evaluation is shared with playback/export.
/// </summary>
public sealed class SequencePreview : IDisposable
{
    private ProjectSnapshot? snapshot;
    private Guid sequenceId;
    private OutputProfile? profile;
    private SequenceEvaluator? evaluator;

    public SequenceFrame Video(ProjectSnapshot snapshot, Guid sequenceId, SequenceTime position,
        CancellationToken cancellation = default, OutputProfile? profile = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!ReferenceEquals(this.snapshot, snapshot) || this.sequenceId != sequenceId || this.profile != profile)
        {
            Dispose();
            evaluator = new(snapshot, sequenceId, profile, useProxies: true);
            this.snapshot = snapshot; this.sequenceId = sequenceId; this.profile = profile;
        }
        try
        {
            var frame = evaluator!.Video(position, cancellation);
            cancellation.ThrowIfCancellationRequested();
            return frame;
        }
        catch { Dispose(); throw; } // never reuse a partially cancelled/failed decoder cursor
    }

    public void Dispose() { evaluator?.Dispose(); evaluator = null; snapshot = null; }
}
