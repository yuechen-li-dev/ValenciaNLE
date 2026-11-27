using NAudio.Dsp;
using Valencia.Core.Timeline;

namespace Valencia.Media;

/// <summary>Worker-owned voice EQ. Each channel has independent filter history.</summary>
internal sealed class VoiceEqualizer(int rate, VoiceEq eq)
{
    private BiQuadFilter[] left = Create(rate, eq), right = Create(rate, eq);
    private long? nextSample;

    public void Process(Span<float> stereo, long firstSample)
    {
        if (nextSample is { } previous && firstSample != previous)
        {
            left = Create(rate, eq);
            right = Create(rate, eq);
        }
        ProcessChannel(stereo, left, 0);
        ProcessChannel(stereo, right, 1);
        nextSample = checked(firstSample + stereo.Length / 2);
    }

    private static void ProcessChannel(Span<float> stereo, BiQuadFilter[] filters, int channel)
    {
        foreach (var filter in filters)
            for (int index = channel; index < stereo.Length; index += 2)
                stereo[index] = filter.Transform(stereo[index]);
    }

    private static BiQuadFilter[] Create(int rate, VoiceEq eq)
    {
        var filters = new List<BiQuadFilter>(3);
        if (eq.LowDb != 0) filters.Add(BiQuadFilter.LowShelf(rate, 120, 1, (float)eq.LowDb));
        if (eq.MidDb != 0) filters.Add(BiQuadFilter.PeakingEQ(rate, 1500, .9f, (float)eq.MidDb));
        if (eq.HighDb != 0) filters.Add(BiQuadFilter.HighShelf(rate, Math.Min(4500, rate * .45f), 1, (float)eq.HighDb));
        return [.. filters];
    }
}
