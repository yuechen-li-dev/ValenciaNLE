using System.Collections.Immutable;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.Analysis;

/// <summary>One PCM pass, disjoint RMS windows, max-channel energy (never phase-cancelling channel averaging).</summary>
public sealed class AudioAnalyzer(AudioSettings settings)
{
    public const int MaximumBins = 65_536;
    private readonly List<WaveBin> bins = [];
    private readonly List<EnergyInterval> activity = [];
    private readonly double[] square = new double[2];
    private readonly float[] minimum = [float.PositiveInfinity, float.PositiveInfinity];
    private readonly float[] maximum = [float.NegativeInfinity, float.NegativeInfinity];
    private MediaTimestamp anchor;
    private long epochSamples, windowStart, count, units;
    private int rate, channels, epoch, windowSamples, windowsPerBin = 5, pendingWindows;
    private WaveBin? pending;

    public long ProcessedSamples => units;
    public void Push(SourceAudioFrame frame)
    {
        settings.Validate();
        if (rate == 0)
        {
            rate = frame.SampleRate;
            channels = frame.Channels;
            if (rate is < 8000 or > 192000 || channels is < 1 or > 2) throw new NotSupportedException("Unsupported analysis PCM format.");
            windowSamples = checked((int)AnalysisTime.MinimumSamples(settings.WindowMs, rate));
            anchor = frame.Timestamp;
        }
        if (rate != frame.SampleRate || channels != frame.Channels || frame.Samples.Length % channels != 0)
            throw new NotSupportedException("PCM format changed or block is incomplete.");
        var expected = AnalysisTime.SampleOffset(anchor, epochSamples, rate).ToTimestamp(TimeRounding.Floor);
        if (Math.Abs((decimal)frame.Timestamp.Value - expected.Value) > Math.Max(2m, (decimal)frame.Timestamp.Denominator / frame.Timestamp.Numerator / rate))
        {
            FlushWindow();
            FlushBin();
            epoch++;
            anchor = frame.Timestamp;
            epochSamples = 0;
        }
        for (int offset = 0; offset < frame.Samples.Length; offset += channels)
        {
            if (count == 0) windowStart = epochSamples;
            for (int channel = 0; channel < channels; channel++)
            {
                float value = frame.Samples[offset + channel];
                if (!float.IsFinite(value)) throw new InvalidDataException("Nonfinite decoded sample.");
                square[channel] += (double)value * value;
                minimum[channel] = Math.Min(minimum[channel], value);
                maximum[channel] = Math.Max(maximum[channel], value);
            }
            count++;
            epochSamples++;
            units++;
            if (count == windowSamples) FlushWindow();
        }
    }

    private void FlushWindow()
    {
        if (count == 0) return;
        var peaks = Enumerable.Range(0, channels).Select(c => new ChannelPeak(minimum[c], maximum[c], Math.Sqrt(square[c] / count))).ToImmutableArray();
        var window = new WaveBin(AnalysisTime.SampleOffset(anchor, windowStart, rate), AnalysisTime.SampleOffset(anchor, windowStart + count, rate), count, epoch, peaks);
        double rms = peaks.Max(p => p.Rms);
        double db = rms == 0 ? -200 : 20 * Math.Log10(rms);
        string category = db <= settings.ThresholdDb ? "silence" : db < settings.ActiveDb ? "lowActivity" : "active";
        var range = new SourceRange(window.Start, window.End, epoch);
        if (activity.LastOrDefault() is { } previous && previous.Category == category && previous.Range.Epoch == epoch)
        {
            double oldWeight = previous.Range.End.Since(previous.Range.Start).Ticks;
            double newWeight = window.End.Since(window.Start).Ticks;
            activity[^1] = new(previous.Range with { End = window.End }, category,
                Math.Sqrt((previous.Rms * previous.Rms * oldWeight + rms * rms * newWeight) / (oldWeight + newWeight)));
        }
        else
        {
            if (activity.Count >= MaximumBins) throw new NotSupportedException("Activity interval budget exceeded.");
            activity.Add(new(range, category, rms));
        }
        pending = pending is null ? window : Combine(pending, window);
        if (++pendingWindows >= windowsPerBin) FlushBin();
        count = 0;
        Array.Clear(square);
        Array.Fill(minimum, float.PositiveInfinity);
        Array.Fill(maximum, float.NegativeInfinity);
    }

    private void FlushBin()
    {
        if (pending is null) return;
        bins.Add(pending);
        pending = null;
        pendingWindows = 0;
        if (bins.Count >= MaximumBins)
        {
            var reduced = Reduce(bins);
            if (reduced.Count >= bins.Count) throw new NotSupportedException("Discontinuity/waveform budget exceeded.");
            bins.Clear();
            bins.AddRange(reduced);
            windowsPerBin = checked(windowsPerBin * 2);
        }
    }

    public (ImmutableArray<WaveLevel> Waveform, ImmutableArray<SourceRange> Silence, ImmutableArray<EnergyInterval> Activity) Finish()
    {
        FlushWindow();
        FlushBin();
        var pyramid = ImmutableArray.CreateBuilder<WaveLevel>();
        var level = bins.ToList();
        while (level.Count > 0)
        {
            pyramid.Add(new([.. level]));
            var reduced = Reduce(level);
            if (reduced.Count == level.Count) break;
            level = reduced;
        }
        var silence = new List<SourceRange>();
        long mergeTicks = AnalysisTime.Milliseconds(Math.Max(settings.MergeSilenceMs, settings.MinimumActivityMs)).Ticks;
        foreach (var range in activity.Where(a => a.Category == "silence").Select(a => a.Range))
        {
            if (silence.LastOrDefault() is { } last && last.Epoch == range.Epoch && range.Start.Since(last.End).Ticks <= mergeTicks)
                silence[^1] = last with { End = range.End };
            else silence.Add(range);
        }
        silence = silence.Select(r => r with { Start = r.Start.Shift(AnalysisTime.Milliseconds(settings.LeadingPaddingMs)),
            End = r.End.Shift(new(-AnalysisTime.Milliseconds(settings.TrailingPaddingMs).Ticks)) }).ToList();
        silence.RemoveAll(r => r.End.CompareTo(r.Start) <= 0 || r.End.Since(r.Start).Ticks < AnalysisTime.Milliseconds(settings.MinimumSilenceMs).Ticks);
        return (pyramid.ToImmutable(), [.. silence], [.. activity]);
    }

    internal static WaveBin Combine(WaveBin a, WaveBin b) => new(a.Start, b.End, checked(a.Samples + b.Samples), a.Epoch,
        [.. a.Channels.Zip(b.Channels, (x, y) => new ChannelPeak(Math.Min(x.Minimum, y.Minimum), Math.Max(x.Maximum, y.Maximum),
            Math.Sqrt((x.Rms * x.Rms * a.Samples + y.Rms * y.Rms * b.Samples) / (a.Samples + b.Samples))))]);

    private static List<WaveBin> Reduce(IReadOnlyList<WaveBin> source)
    {
        var result = new List<WaveBin>();
        for (int i = 0; i < source.Count; i++)
        {
            var bin = source[i];
            if (i + 1 < source.Count && bin.Epoch == source[i + 1].Epoch) bin = Combine(bin, source[++i]);
            result.Add(bin);
        }
        return result;
    }
}
