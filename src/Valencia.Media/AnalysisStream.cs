using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Media;

public sealed record SourceAudioFrame(MediaTimestamp Timestamp, int SampleRate, int Channels, float[] Samples);
public sealed record SourceLumaFrame(MediaTimestamp Timestamp, byte[] Pixels);
public sealed record TranscriptionPcmBlock(MediaTimestamp Origin, int Epoch, long FirstSample, float[] Samples);

/// <summary>Synchronous worker-confined pull decode; callbacks finish before the next owned frame is allocated.</summary>
public static class AnalysisStream
{
    public static void Transcription(MediaAsset asset, int streamIndex, Action<TranscriptionPcmBlock> consume, CancellationToken cancellation)
    {
        FfmpegRuntime.Initialize();
        RequireStream(asset, streamIndex, TrackKind.Audio);
        using var decoder = Open(asset, streamIndex);
        MediaTimestamp? origin = null;
        long inputSamples = 0, outputSamples = 0;
        int inputRate = 0, epoch = 0;
        void Deliver(float[] pcm)
        {
            if (pcm.Length == 0) return;
            consume(new(origin!.Value, epoch, outputSamples, pcm));
            outputSamples = checked(outputSamples + pcm.Length);
        }
        while (decoder.Read(cancellation))
        {
            var timestamp = decoder.Timestamp;
            int rate = decoder.SourceSampleRate;
            if (origin is { } first)
            {
                var expected = new SourcePosition(first).Shift(TimelineTime.FromSamples(inputSamples, inputRate)).ToTimestamp(TimeRounding.Floor);
                if (rate != inputRate || Math.Abs((decimal)timestamp.Value - expected.Value) > Math.Max(2m, (decimal)timestamp.Denominator / timestamp.Numerator / rate))
                {
                    Deliver(decoder.DrainAudio());
                    decoder.ResetAudioConversion();
                    epoch++;
                    origin = null;
                }
            }
            if (origin is null) { origin = timestamp; inputSamples = 0; outputSamples = 0; inputRate = rate; }
            inputSamples = checked(inputSamples + decoder.SourceSampleCount);
            Deliver(decoder.CopyTranscriptionAudio());
        }
        if (origin is not null) Deliver(decoder.DrainAudio());
    }
    public static void Audio(MediaAsset asset, int streamIndex, Action<SourceAudioFrame> consume, CancellationToken cancellation)
    {
        FfmpegRuntime.Initialize();
        RequireStream(asset, streamIndex, TrackKind.Audio);
        using var decoder = Open(asset, streamIndex);
        while (decoder.Read(cancellation))
        {
            var pcm = decoder.CopySourceAudio();
            if (pcm.Samples.Length != 0) consume(new(decoder.Timestamp, pcm.Rate, pcm.Channels, pcm.Samples));
        }
        // Same-rate sample-format conversion has no delayed samples. Never silently discard a delayed tail.
        if (decoder.DrainAudio().Length != 0) throw new NotSupportedException("Unexpected delayed same-rate audio conversion.");
    }

    public static void Video(MediaAsset asset, int streamIndex, int everyNthFrame, Action<SourceLumaFrame> consume, CancellationToken cancellation)
    {
        FfmpegRuntime.Initialize();
        RequireStream(asset, streamIndex, TrackKind.Video);
        using var decoder = Open(asset, streamIndex);
        long frame = 0;
        while (decoder.Read(cancellation))
            if (frame++ % everyNthFrame == 0) consume(new(decoder.Timestamp, decoder.CopyAnalysisLuma()));
    }

    private static FfmpegDecoder Open(MediaAsset asset, int index) => new(
        new MediaInfo(asset.SourcePath, asset.Container, 0, 0, 0, 0, 0, index, null, []), index, 0);

    private static void RequireStream(MediaAsset asset, int index, TrackKind kind)
    {
        if (!asset.Streams.Any(s => s.Index == index && s.Kind == kind)) throw new ArgumentException("Analysis stream identity/kind is invalid.");
    }
}
