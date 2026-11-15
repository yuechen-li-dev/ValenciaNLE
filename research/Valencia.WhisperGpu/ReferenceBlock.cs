using System.Numerics;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using Valencia.Media;

namespace Valencia.WhisperGpu;

internal static class ReferenceBlock
{
    public static float[] EncoderInput(WhisperWeights model, string audio, int rows)
    {
        var asset = MediaAssetProbe.Read(Guid.NewGuid(), audio);
        float[] pcm = new float[480000];
        int count = 0;
        AnalysisStream.Transcription(asset, asset.Streams.First(s => s.Kind == Valencia.Core.Timeline.TrackKind.Audio).Index, block =>
        {
            int copy = Math.Min(block.Samples.Length, pcm.Length - count);
            block.Samples.AsSpan(0, copy).CopyTo(pcm.AsSpan(count));
            count += copy;
        }, default);
        float[] mel = new float[3000 * model.MelBands];
        Complex[] fft = new Complex[400];
        float maximum = -100;
        for (int frame = 0; frame < 3000; frame++)
        {
            for (int i = 0; i < 400; i++)
            {
                int position = frame * 160 + i - 200;
                if (position < 0) position = -position;
                float sample = position < pcm.Length ? pcm[position] : 0;
                fft[i] = sample * (.5 - .5 * Math.Cos(2 * Math.PI * i / 400));
            }
            Fourier.Forward(fft, FourierOptions.NoScaling);
            for (int band = 0; band < model.MelBands; band++)
            {
                double sum = 0;
                for (int k = 0; k < 201; k++) sum += model.MelFilters[band * 201 + k] * fft[k].Magnitude * fft[k].Magnitude;
                float value = (float)Math.Log10(Math.Max(1e-10, sum));
                mel[frame * model.MelBands + band] = value;
                maximum = Math.Max(maximum, value);
            }
        }
        for (int i = 0; i < mel.Length; i++) mel[i] = (Math.Max(mel[i], maximum - 8) + 4) / 4;
        int width = model.Width, convRows = rows * 2 + 2;
        float[] first = Conv(mel, 3000, model.MelBands, model.Read("encoder.conv1.weight"), model.Read("encoder.conv1.bias"), width, convRows, 1);
        float[] result = Conv(first, convRows, width, model.Read("encoder.conv2.weight"), model.Read("encoder.conv2.bias"), width, rows, 2);
        float[] positions = model.Read("encoder.positional_embedding");
        for (int i = 0; i < result.Length; i++) result[i] += positions[i];
        return result;
    }
    private static float[] Conv(float[] input, int inputRows, int channels, float[] weight, float[] bias, int outputs, int rows, int stride)
    {
        float[] result = new float[rows * outputs];
        Parallel.For(0, rows, row =>
        {
            for (int output = 0; output < outputs; output++)
            {
                float sum = bias[output];
                for (int channel = 0; channel < channels; channel++)
                    for (int tap = 0; tap < 3; tap++)
                    {
                        int frame = row * stride + tap - 1;
                        if (frame >= 0 && frame < inputRows) sum += input[frame * channels + channel] * weight[(output * channels + channel) * 3 + tap];
                    }
                result[row * outputs + output] = Gelu(sum);
            }
        });
        return result;
    }
    public static float Gelu(float value) => .5f * value * (1 + (float)SpecialFunctions.Erf(value / Math.Sqrt(2)));
    public static float[] Norm(float[] input, int rows, int width, float[] gamma, float[] beta)
    {
        float[] result = new float[input.Length];
        for (int row = 0; row < rows; row++)
        {
            double mean = 0, variance = 0;
            for (int c = 0; c < width; c++) mean += input[row * width + c];
            mean /= width;
            for (int c = 0; c < width; c++) variance += Math.Pow(input[row * width + c] - mean, 2);
            double scale = 1 / Math.Sqrt(variance / width + 1e-5);
            for (int c = 0; c < width; c++) result[row * width + c] = (float)((input[row * width + c] - mean) * scale * gamma[c] + beta[c]);
        }
        return result;
    }
    public static float[] Linear(float[] input, int rows, int inner, float[] weights, float[] bias)
    {
        int columns = bias.Length;
        var output = new float[rows * columns];
        Parallel.For(0, rows, row =>
        {
            for (int column = 0; column < columns; column++)
            {
                float sum = 0;
                for (int k = 0; k < inner; k++) sum += input[row * inner + k] * weights[column * inner + k];
                output[row * columns + column] = sum + bias[column];
            }
        });
        return output;
    }
    public static float[] Run(WhisperWeights model, float[] input, int rows, string prefix, bool causal)
    {
        int width = model.Width;
        var normalized = Norm(input, rows, width, model.Read(prefix + ".attn_ln.weight"), model.Read(prefix + ".attn_ln.bias"));
        float[] Project(string name) => Linear(normalized, rows, width, model.Read(prefix + ".attn." + name + ".weight"),
            model.Tensors.ContainsKey(prefix + ".attn." + name + ".bias") ? model.Read(prefix + ".attn." + name + ".bias") : new float[width]);
        var query = Project("query"); var key = Project("key"); var value = Project("value");
        var attention = new float[rows * width];
        for (int head = 0; head < model.Heads; head++)
            for (int row = 0; row < rows; row++)
            {
                var scores = new double[rows];
                int headWidth = width / model.Heads;
                double max = double.NegativeInfinity;
                for (int k = 0; k < rows; k++)
                {
                    double sum = 0;
                    for (int c = 0; c < headWidth; c++) sum += query[row * width + head * headWidth + c] * key[k * width + head * headWidth + c];
                    scores[k] = causal && k > row ? double.NegativeInfinity : sum / Math.Sqrt(headWidth);
                    max = Math.Max(max, scores[k]);
                }
                double denominator = scores.Sum(s => Math.Exp(s - max));
                for (int c = 0; c < headWidth; c++)
                {
                    double sum = 0;
                    for (int k = 0; k < rows; k++) sum += Math.Exp(scores[k] - max) / denominator * value[k * width + head * headWidth + c];
                    attention[row * width + head * headWidth + c] = (float)sum;
                }
            }
        var projected = Linear(attention, rows, width, model.Read(prefix + ".attn.out.weight"), model.Read(prefix + ".attn.out.bias"));
        var residual = input.Zip(projected, (a, b) => a + b).ToArray();
        var norm = Norm(residual, rows, width, model.Read(prefix + ".mlp_ln.weight"), model.Read(prefix + ".mlp_ln.bias"));
        var hidden = Linear(norm, rows, width, model.Read(prefix + ".mlp.0.weight"), model.Read(prefix + ".mlp.0.bias")).Select(Gelu).ToArray();
        var result = Linear(hidden, rows, width * 4, model.Read(prefix + ".mlp.2.weight"), model.Read(prefix + ".mlp.2.bias"));
        for (int i = 0; i < result.Length; i++) result[i] += residual[i];
        return result;
    }
}
