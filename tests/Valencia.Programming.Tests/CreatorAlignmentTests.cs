using Valencia.Analysis;
using Valencia.Engine;
using Xunit;

namespace Valencia.Programming.Tests;

public sealed partial class TranscriptionTests
{
    [Fact]
    public async Task DtwRefinementUsesActualAttentionAndPreservesOriginalTimings()
    {
        await using var engine = await Engine();
        var native = await Transcribe(engine);
        var refined = await Transcribe(engine, new(Alignment: "dtw"));
        var errors = refined.Transcript!.Words.Where(w => w.TimingUsable).Select(w => new { Word = w, Reference = referenceWords.Where(r => WhisperTranscriber.Normalize(r.Text) == WhisperTranscriber.Normalize(w.Text)).ToArray() })
            .Where(p => p.Reference.Length == 1).Select(p => Math.Abs(AnalysisTime.Normalize(p.Word.Range.Start, refined.ContainerStart).TotalSeconds - p.Reference[0].Start.TotalSeconds)).ToArray();
        Assert.NotEmpty(errors);
        Assert.InRange(errors.Average(), 0, .15);
        Assert.All(refined.Transcript.Words, word => Assert.NotNull(word.OriginalRange));
        Assert.NotEqual(native.Key, refined.Key);
        log.WriteLine("M2D_ALIGNMENT=" + EngineJson.Write(new { meanStartErrorSeconds = errors.Average(), maximumStartErrorSeconds = errors.Max(), usable = errors.Length,
            words = refined.Transcript.Words.Select(w => new { w.Text, seconds = AnalysisTime.Normalize(w.Range.Start, refined.ContainerStart).TotalSeconds, w.TimingUsable }) }));
    }
}
