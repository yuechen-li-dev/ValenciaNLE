namespace Valencia.Media;

/// <summary>BS.1770 K-weighted stereo energy over the last 400 ms (EBU momentary loudness).</summary>
internal sealed class MomentaryLoudness
{
    private readonly double[] energy;
    private readonly Stages left, right;
    private int cursor, filled;
    private double sum;

    public MomentaryLoudness(int rate)
    {
        energy = new double[Math.Max(1, (int)Math.Round(rate * .4))];
        left = new(rate);
        right = new(rate);
    }

    public double? Observe(ReadOnlySpan<float> stereo)
    {
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            double l = left.Process(float.IsFinite(stereo[i]) ? stereo[i] : 0);
            double r = right.Process(float.IsFinite(stereo[i + 1]) ? stereo[i + 1] : 0);
            double power = l * l + r * r;
            sum += power - energy[cursor];
            energy[cursor] = power;
            if (++cursor == energy.Length)
            {
                cursor = 0;
                // Rebase the rolling sum to prevent residual energy in extended silence.
                sum = 0;
                foreach (var entry in energy) sum += entry;
            }
            if (filled < energy.Length) filled++;
        }
        return filled == energy.Length && sum / energy.Length > 1e-20
            ? -.691 + 10 * Math.Log10(sum / energy.Length) : null;
    }

    private sealed class Stages(int rate)
    {
        private readonly Biquad shelf = Shelf(rate);
        private readonly Biquad highPass = HighPass(rate);

        public double Process(double sample) => highPass.Process(shelf.Process(sample));

        // The bilinear parameters reproduce the BS.1770 48 kHz tables and adapt to the device rate.
        private static Biquad Shelf(int rate)
        {
            double t = Math.Tan(Math.PI * 1681.974450955533 / rate);
            double knee = t / .7071752369554196, square = t * t, norm = 1 + knee + square;
            double high = Math.Pow(10, 3.999843853973347 / 20), middle = Math.Pow(high, .4996667741545416);
            return new((high + middle * knee + square) / norm, 2 * (square - high) / norm,
                (high - middle * knee + square) / norm, 2 * (square - 1) / norm, (1 - knee + square) / norm);
        }

        private static Biquad HighPass(int rate)
        {
            double t = Math.Tan(Math.PI * 38.13547087602444 / rate);
            double knee = t / .5003270373238773, square = t * t, norm = 1 + knee + square;
            return new(1, -2, 1, 2 * (square - 1) / norm, (1 - knee + square) / norm);
        }
    }

    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double first, second;
        public double Process(double sample)
        {
            double output = b0 * sample + first;
            first = b1 * sample - a1 * output + second;
            second = b2 * sample - a2 * output;
            return output;
        }
    }
}
