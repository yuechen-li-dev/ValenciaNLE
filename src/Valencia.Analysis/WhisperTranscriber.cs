using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Media;
using Whisper.net;

namespace Valencia.Analysis;

/// <summary>Reference CPU whisper.cpp provider. Its native mel/inference is not Aurelian execution.</summary>
public static class WhisperTranscriber
{
    public const string Algorithm = "whisper.net-1.9.1/token-times-v3-dtw/" + WhisperModels.Revision;
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
        WhisperFactory? factory = null;
        WhisperProcessor? processor = null;
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
            var transcript = new TranscriptData(model, settings.Language, "Whisper.net 1.9.1 / whisper.cpp CPU reference",
                settings.Alignment == "dtw" ? "whisper.cpp selected-head cross-attention DTW + PCM trailing-silence refinement/v1; not calibrated confidence" : "experimental native token-timestamp word splitting; not forced alignment or calibrated confidence", Preprocessing,
                segments.ToImmutable(), [.. ordered], chunks, reused);
            return Wrap(key, transcript);
        }
        finally
        {
            // Cancellation may return the async enumerator before the native worker exits.
            // Join it before releasing the factory/context; synchronous Dispose rejects this state.
            if (processor is not null) processor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            factory?.Dispose();
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
                    factory ??= WhisperFactory.FromPath(model.Path, new WhisperFactoryOptions { UseGpu = false,
                        UseDtwTimeStamps = settings.Alignment == "dtw", UseFlashAttention = settings.Alignment != "dtw",
                        HeadsPreset = settings.Alignment == "dtw" ? (settings.Model == "tiny.en" ? WhisperAlignmentHeadsPreset.TinyEn : WhisperAlignmentHeadsPreset.LargeV3) : WhisperAlignmentHeadsPreset.None });
                    processor ??= factory.CreateBuilder().WithLanguage(settings.Language).WithThreads(settings.Threads).WithNoContext()
                        .WithTemperature(0).WithTemperatureInc(0).WithTokenTimestamps().SplitOnWord().WithMaxSegmentLength(1).WithProbabilities().Build();
                    Decode().GetAwaiter().GetResult();
                    if (settings.Alignment == "dtw") RefineEnds();
                }
                var partial = new TranscriptData(model, settings.Language, "whisper.cpp CPU reference", "native token timestamps", Preprocessing, [], [.. decoded], 1, 0);
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

            async Task Decode()
            {
                await foreach (var segment in processor!.ProcessAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false))
                {
                    string text = segment.Text.Trim();
                    if (text.Length == 0) continue;
                    if (text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 1)
                        throw new NotSupportedException("Reference backend returned a multi-word segment without word boundaries; text editing is not advertised as word aligned.");
                    long start = Math.Clamp((long)((Int128)segment.Start.Ticks * 16000 / TimeSpan.TicksPerSecond), 0, count);
                    long end = Math.Clamp((long)(((Int128)segment.End.Ticks * 16000 + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond), 0, count);
                    bool usable = end > start && segment.End.Ticks <= (long)((Int128)count * TimeSpan.TicksPerSecond / 16000);
                    if (!usable) diagnostics.Add("Native boundary unsupported or outside chunk; retained as untimed text: " + text);
                    var range = new SourceRange(AnalysisTime.SampleOffset(origin, firstSample + start, 16000), AnalysisTime.SampleOffset(origin, firstSample + end, 16000), epoch);
                    SourceRange? originalRange = null;
                    long? aligned = null;
                    if (settings.Alignment == "dtw")
                    {
                        originalRange = range;
                        var tokens = segment.Tokens.Where(t => t.Id < 50256 && t.DtwTimestamp >= 0 && t.Text is { } tokenText && Normalize(tokenText).Length > 0).ToArray();
                        if (tokens.Length > 0)
                        {
                            aligned = checked(tokens.Max(t => t.DtwTimestamp) * TimeSpan.TicksPerSecond / 100);
                            start = Math.Clamp(tokens.Min(t => t.DtwTimestamp) * 160, 0, count);
                            end = Math.Max(start, Math.Clamp(tokens.Max(t => t.DtwTimestamp) * 160 + 160, 0, count));
                            range = new(AnalysisTime.SampleOffset(origin, firstSample + start, 16000), AnalysisTime.SampleOffset(origin, firstSample + end, 16000), epoch);
                            usable = end > start;
                        }
                        else usable = false;
                    }
                    decoded.Add(new(Id(chunkKey + "/" + decoded.Count + "/" + text), text, range, segment.Start.Ticks, segment.End.Ticks,
                        float.IsFinite(segment.Probability) ? segment.Probability : null, usable, aligned, originalRange, DtwBoundaryTicks: aligned));
                }
            }
            void RefineEnds()
            {
                var anchor = AnalysisTime.SampleOffset(origin, firstSample, 16000);
                long previousBoundary = 0;
                for (int i = 0; i < decoded.Count; i++)
                {
                    var word = decoded[i];
                    if (word.DtwBoundaryTicks is not { } boundary) continue;
                    long begin = previousBoundary;
                    long end = Math.Clamp((long)((Int128)boundary * 16000 / TimeSpan.TicksPerSecond), 0, count);
                    previousBoundary = Math.Max(previousBoundary, end);
                    // DTW token path boundaries delimit successive token spans. Exclude measured silence at their edges.
                    long firstActive = end, lastActive = begin;
                    for (long sample = begin; sample + 160 <= end; sample += 160)
                    {
                        double power = 0;
                        for (int j = 0; j < 160; j++) power += (double)buffer[sample + j] * buffer[sample + j];
                        if (power / 160 > 1e-5)
                        {
                            if (lastActive > begin && sample - lastActive >= 2400) firstActive = sample;
                            else firstActive = Math.Min(firstActive, sample);
                            lastActive = sample + 160;
                        }
                    }
                    if (firstActive - begin >= 640) begin = firstActive;
                    if (end - lastActive >= 960) end = lastActive;
                    end = Math.Clamp(Math.Max(end, begin), 0, count);
                    decoded[i] = word with { Range = new(AnalysisTime.SampleOffset(origin, firstSample + begin, 16000), AnalysisTime.SampleOffset(origin, firstSample + end, 16000), epoch),
                        AlignedStartTicks = (long)((Int128)begin * TimeSpan.TicksPerSecond / 16000), TimingUsable = end > begin };
                }
            }
        }
        AnalysisArtifact Wrap(string artifactKey, TranscriptData transcript) => new(1, artifactKey, asset.Id, fingerprint, stream, "transcript", Algorithm,
            AnalysisJson.Write(settings), MediaAnalyzer.DecoderPolicy + "/" + Preprocessing, imported.ContainerStart, "complete", 16000, 1, processed,
            [], [], [], [], [.. diagnostics.Take(128)], transcript);
    }
    public static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToLowerInvariant();
    private static Guid Id(string text) => new(SHA256.HashData(Encoding.UTF8.GetBytes(text))[..16]);
}
