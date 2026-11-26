using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Media;
using Aurelian.Inference.Whisper;

namespace Valencia.Analysis;

/// <summary>Aurelian Tiny.en provider for source-aligned local transcription.</summary>
public static class WhisperTranscriber
{
    public const string Algorithm = "aurelian-whisper/tiny-en-dtw-v2/" + WhisperModels.Revision;
    private const string Provider = "Aurelian Whisper / Vulkan";
    private const string Alignment = "Tiny.en selected-head DTW with PCM edge trimming/v1";
    public const string Preprocessing = "ffmpeg-swr/mono16k-float/no-peak-normalization/v1";

    public static AnalysisArtifact Run(MediaAsset asset, int stream, TranscriptionSettings settings, string key, string fingerprint,
        AnalysisCache cache, IProgress<AnalysisProgress>? progress, CancellationToken cancellation)
    {
        settings.Validate();
        var model = WhisperModels.Resolve(settings.Model);
        var imported = MediaAssetProbe.Import(asset.Id, asset.SourcePath);
        int capacity = settings.ChunkSeconds * 16000, overlap = settings.OverlapSeconds * 16000;
        float[] buffer = new float[capacity];
        int count = 0, epoch = -1, chunks = 0, reused = 0;
        long firstSample = 0, processed = 0;
        MediaTimestamp origin = default;
        var words = new List<TranscriptWord>();
        var diagnostics = new List<string>();
        List<TranscriptWord> pendingTail = [];
        VulkanWhisperTranscriber? transcriber = null;
        try
        {
            AnalysisStream.Transcription(asset, stream, block =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (block.Epoch != epoch)
                {
                    FinishEpoch();
                    epoch = block.Epoch;
                    origin = block.Origin;
                    firstSample = block.FirstSample;
                    if (epoch > 0) diagnostics.Add("Discontinuity retained as a separate transcript epoch.");
                }
                int offset = 0;
                while (offset < block.Samples.Length)
                {
                    int copy = Math.Min(capacity - count, block.Samples.Length - offset);
                    block.Samples.AsSpan(offset, copy).CopyTo(buffer.AsSpan(count));
                    offset += copy;
                    count += copy;
                    processed += copy;
                    if (count == capacity)
                    {
                        ProcessChunk(final: false);
                        buffer.AsSpan(capacity - overlap, overlap).CopyTo(buffer);
                        count = overlap;
                        firstSample += capacity - overlap;
                    }
                }
                progress?.Report(new(processed, Math.Clamp(processed / 16000.0 / imported.Duration.TotalSeconds, 0, 1)));
            }, cancellation);
            FinishEpoch();
            cancellation.ThrowIfCancellationRequested();
            var ordered = words.OrderBy(w => w.Range.Epoch).ThenBy(w => w.Range.Start, Comparer<SourcePosition>.Create((a, b) => a.CompareTo(b))).ToArray();
            var segments = ImmutableArray.CreateBuilder<TranscriptSegment>();
            for (int first = 0; first < ordered.Length; first += 12)
            {
                int length = Math.Min(12, ordered.Length - first);
                // Never create a segment across a discontinuity.
                for (int i = 1; i < length; i++) if (ordered[first + i].Range.Epoch != ordered[first].Range.Epoch) { length = i; break; }
                segments.Add(new(Id(key + "/segment/" + first), string.Join(" ", ordered.Skip(first).Take(length).Select(w => w.Text)),
                    new(ordered[first].Range.Start, ordered[first + length - 1].Range.End, ordered[first].Range.Epoch), first, length));
                first -= 12 - length;
            }
            var transcript = new TranscriptData(model, settings.Language, Provider, Alignment, Preprocessing,
                segments.ToImmutable(), [.. ordered], chunks, reused);
            return Wrap(key, transcript);
        }
        finally
        {
            transcriber?.Dispose();
        }

        void FinishEpoch()
        {
            if (count == 0) return;
            if (count == overlap && pendingTail.Count > 0) Append(pendingTail);
            else ProcessChunk(final: true);
            count = 0;
            pendingTail.Clear();
        }
        void Append(IEnumerable<TranscriptWord> incoming)
        {
            foreach (var word in incoming)
            {
                if (words.Count >= 100000) throw new NotSupportedException("Transcript word budget exceeded; transcribe a narrower recording.");
                var last = words.LastOrDefault();
                if (last is not null && last.Range.Epoch == word.Range.Epoch && Normalize(last.Text) == Normalize(word.Text)
                    && word.Range.Start.CompareTo(last.Range.End) < 0) continue;
                words.Add(word);
            }
        }
        void ProcessChunk(bool final)
        {
            cancellation.ThrowIfCancellationRequested();
            string chunkKey = AnalysisCache.Key(asset.Id, fingerprint, stream, Algorithm + "/chunk", AnalysisJson.Write(settings) + $"|{epoch}|{origin.Value}|{origin.Numerator}|{origin.Denominator}|{firstSample}|{count}");
            var cached = cache.ReadAsync(chunkKey, cancellation).GetAwaiter().GetResult();
            List<TranscriptWord> decoded;
            if (cached?.Transcript is { } saved)
            {
                decoded = [.. saved.Words];
                reused++;
            }
            else
            {
                decoded = [];
                double energy = 0;
                for (int i = 0; i < count; i++) energy += (double)buffer[i] * buffer[i];
                if (energy / count > 1e-8)
                {
                    transcriber ??= new VulkanWhisperTranscriber(model.Path);
                    foreach (var word in transcriber.Transcribe(buffer, count, settings.Threads, cancellation))
                    {
                        var range = new SourceRange(
                            AnalysisTime.SampleOffset(origin, firstSample + word.FirstSample, 16000),
                            AnalysisTime.SampleOffset(origin, firstSample + word.EndSample, 16000), epoch);
                        var original = new SourceRange(
                            AnalysisTime.SampleOffset(origin, firstSample + word.UnrefinedFirstSample, 16000),
                            AnalysisTime.SampleOffset(origin, firstSample + word.UnrefinedEndSample, 16000), epoch);
                        long rawStart = word.UnrefinedFirstSample * TimeSpan.TicksPerSecond / 16000;
                        long rawEnd = word.UnrefinedEndSample * TimeSpan.TicksPerSecond / 16000;
                        long alignedStart = word.FirstSample * TimeSpan.TicksPerSecond / 16000;
                        decoded.Add(new(Id(chunkKey + "/" + decoded.Count + "/" + word.Text), word.Text, range,
                            rawStart, rawEnd, null, word.TimingUsable, AlignedStartTicks: alignedStart,
                            OriginalRange: original, DtwBoundaryTicks: rawStart));
                    }
                }
                var partial = new TranscriptData(model, settings.Language, Provider, Alignment, Preprocessing, [], [.. decoded], 1, 0);
                cache.PublishAsync(Wrap(chunkKey, partial) with { Kind = "transcriptChunk" }, cancellation).GetAwaiter().GetResult();
            }
            chunks++;
            var chunkStart = AnalysisTime.SampleOffset(origin, firstSample, 16000);
            long lower = firstSample == 0 ? 0 : overlap / 2;
            long upper = final ? count : count - overlap / 2;
            var accepted = new List<TranscriptWord>();
            pendingTail = [];
            foreach (var word in decoded)
            {
                long middle = word.Range.Start.Since(chunkStart).Ticks / 2 + word.Range.End.Since(chunkStart).Ticks / 2;
                long sample = TimelineTime.SampleIndex(new(middle), 16000, TimeRounding.Floor);
                if (sample >= lower && sample < upper) accepted.Add(word);
                else if (sample >= upper) pendingTail.Add(word);
            }
            Append(accepted);
        }
        AnalysisArtifact Wrap(string artifactKey, TranscriptData transcript) => new(1, artifactKey, asset.Id, fingerprint, stream, "transcript", Algorithm,
            AnalysisJson.Write(settings), MediaAnalyzer.DecoderPolicy + "/" + Preprocessing, imported.ContainerStart, "complete", 16000, 1, processed,
            [], [], [], [], [.. diagnostics.Take(128)], transcript);
    }
    public static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToLowerInvariant();
    private static Guid Id(string text) => new(SHA256.HashData(Encoding.UTF8.GetBytes(text))[..16]);
}
