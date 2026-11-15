using System.Diagnostics;
using Aurelian.Graphics.Vulkan.ComputeMath;
using Aurelian.Inference;
using Aurelian.Inference.Dominatus;

namespace Valencia.WhisperGpu;

internal sealed class MeasuredController(IComputeStreamController inner) : IComputeStreamController
{
    public double CpuMs { get; private set; }
    public List<ComputeStreamObservation> Events { get; } = [];
    public int Plan(ComputeStreamForecast forecast)
    {
        long start = Stopwatch.GetTimestamp();
        int depth = inner.Plan(forecast);
        CpuMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return depth;
    }
    public void Observe(ComputeStreamObservation observation)
    {
        long start = Stopwatch.GetTimestamp();
        inner.Observe(observation);
        CpuMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Events.Add(observation);
    }
    public int AdmissionLimit(int capacity)
    {
        long start = Stopwatch.GetTimestamp();
        int result = inner.AdmissionLimit(capacity);
        CpuMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return result;
    }
}
internal sealed class StaticController(int depth) : IComputeStreamController
{
    public int Plan(ComputeStreamForecast forecast) => depth;
    public void Observe(ComputeStreamObservation observation) { }
}
internal sealed record Layer(string Name, int Inner, int Outer, StreamingLinearPlan Plan, float[][] Host, List<GpuTensor> Resident);

/// <summary>Whisper block subgraph adapter over existing Aurelian kernels; no token-generation runtime.</summary>
internal sealed class GpuBlock : IDisposable
{
    private readonly VulkanComputeMath math;
    private readonly WhisperWeights model;
    private readonly string prefix;
    private readonly Dictionary<string, Layer> layers = [];
    private readonly Dictionary<string, GpuTensor> norms = [];
    private readonly List<GpuTensor> temporary = [];
    public List<StreamingLinearResult> LinearResults { get; } = [];
    public int OtherDispatches { get; private set; }
    public double OtherGpuMs { get; private set; }
    public int OtherSubmissions { get; private set; }
    public int AttentionRingSubmissions { get; private set; }
    public double StageActionsMs { get; private set; }
    public ulong ResidentWeightBytes => layers.Values.SelectMany(l => l.Resident).Aggregate(0UL, (n, t) => n + (ulong)t.Elements * 4);
    public GpuBlock(VulkanComputeMath math, WhisperWeights model, string prefix, int tileRows, bool resident)
    {
        this.math = math; this.model = model; this.prefix = prefix;
        foreach (string name in new[] { "attn.query", "attn.key", "attn.value", "attn.out", "mlp.0", "mlp.2" })
        {
            float[] weight = model.Read(prefix + "." + name + ".weight");
            int inner = name == "mlp.2" ? model.Width * 4 : model.Width;
            int outer = weight.Length / inner;
            float[] bias = model.Tensors.ContainsKey(prefix + "." + name + ".bias") ? model.Read(prefix + "." + name + ".bias") : new float[outer];
            var plan = new StreamingLinearPlan(inner, outer, Math.Min(outer, Math.Min(tileRows, 1048576 / (inner + 1))));
            var host = new List<float[]>();
            var device = new List<GpuTensor>();
            foreach (var tile in plan.Tiles)
            {
                var values = new float[plan.TileRows * (inner + 1)];
                for (int row = 0; row < tile.OutputCount; row++)
                {
                    weight.AsSpan((tile.FirstOutput + row) * inner, inner).CopyTo(values.AsSpan(row * (inner + 1)));
                    values[row * (inner + 1) + inner] = bias[tile.FirstOutput + row];
                }
                host.Add(values);
                if (resident)
                {
                    var tensor = math.CreateTensor(plan.TileRows, inner + 1);
                    math.Upload(tensor, values);
                    device.Add(tensor);
                }
            }
            layers.Add(name, new(name, inner, outer, plan, host.ToArray(), device));
        }
        foreach (string name in new[] { "attn_ln", "mlp_ln" })
        {
            var tensor = math.CreateTensor(1, model.Width * 2);
            math.Upload(tensor, model.Read(prefix + "." + name + ".weight").Concat(model.Read(prefix + "." + name + ".bias")).ToArray());
            norms.Add(name, tensor);
        }
    }
    private GpuTensor Tensor(int rows, int columns)
    {
        var tensor = math.CreateTensor(rows, columns);
        temporary.Add(tensor);
        return tensor;
    }
    private void Execute(ComputeMathBatch batch)
    {
        var result = math.Execute(batch);
        OtherDispatches += result.Dispatches;
        OtherGpuMs += result.GpuMilliseconds ?? throw new InvalidOperationException("GPU timestamps unavailable.");
        OtherSubmissions++;
    }
    public float[] Run(float[] values, int rows, bool causal, MeasuredController controller, bool dominatusStages)
    {
        LinearResults.Clear(); OtherGpuMs = 0; OtherSubmissions = 0; OtherDispatches = 0; StageActionsMs = 0; AttentionRingSubmissions = 0;
        math.StreamController = controller;
        var input = Tensor(rows, model.Width);
        math.Upload(input, values);
        GpuTensor? norm = null, query = null, key = null, value = null, attention = null, projection = null, residual = null, hidden = null, result = null;
        IEnumerable<ComputeStage> Stages()
        {
            yield return Stage("norm.attention", () => norm = Norm(input, "attn_ln"));
            yield return Stage("linear.query", () => query = Linear(norm!, "attn.query"));
            yield return Stage("linear.key", () => key = Linear(norm!, "attn.key"));
            yield return Stage("linear.value", () => value = Linear(norm!, "attn.value"));
            yield return Stage("attention", () => attention = Attention(query!, key!, value!));
            yield return Stage("linear.attentionOutput", () => projection = Linear(attention!, "attn.out"));
            yield return Stage("residual.attention", () => residual = Residual(input, projection!));
            yield return Stage("norm.mlp", () => norm = Norm(residual!, "mlp_ln"));
            yield return Stage("linear.mlpUp", () => hidden = Linear(norm!, "mlp.0"));
            yield return Stage("gelu", () => { var batch = math.CreateBatch(); batch.Activate(hidden!, hidden!, TensorActivation.GeluErf); Execute(batch); });
            yield return Stage("linear.mlpDown", () => result = Linear(hidden!, "mlp.2"));
            yield return Stage("residual.mlp", () => result = Residual(residual!, result!));
        }
        ComputeStage Stage(string name, Action action) => new(name, () => { var timer = Stopwatch.StartNew(); action(); StageActionsMs += timer.Elapsed.TotalMilliseconds; });
        try
        {
            if (dominatusStages) new DominatusComputeSchedule().Run(Stages());
            else foreach (var stage in Stages()) stage.Execute();
            return math.Read(result!);
        }
        finally
        {
            math.StreamController = null;
            foreach (var tensor in temporary) tensor.Dispose();
            temporary.Clear();
        }
        GpuTensor Norm(GpuTensor tensor, string name)
        {
            var output = Tensor(rows, model.Width);
            var batch = math.CreateBatch(); batch.Normalize(tensor, norms[name], output, Normalization.LayerNorm); Execute(batch);
            return output;
        }
        GpuTensor Linear(GpuTensor tensor, string name)
        {
            var layer = layers[name];
            var output = Tensor(rows, layer.Outer);
            StreamingLinearResult execution;
            if (layer.Resident.Count > 0) execution = math.LinearResidentTiles(tensor, output, layer.Plan, layer.Resident, controller);
            else execution = math.LinearStreamed(tensor, output, layer.Plan, tile => layer.Host[tile.FirstOutput / layer.Plan.TileRows]);
            LinearResults.Add(execution);
            return output;
        }
        GpuTensor Residual(GpuTensor a, GpuTensor b)
        {
            var output = Tensor(rows, model.Width);
            var batch = math.CreateBatch(); batch.Activate(a, output, TensorActivation.Residual, b); Execute(batch);
            return output;
        }
        GpuTensor Attention(GpuTensor q, GpuTensor k, GpuTensor v)
        {
            var output = Tensor(rows, model.Width);
            if (!causal)
            {
                var execution = math.AttentionTiled(q, k, v, output, 64, Math.Min(64, rows), roundOutput: false);
                OtherDispatches += execution.Dispatches; OtherGpuMs += execution.GpuMilliseconds ?? 0; OtherSubmissions += execution.Submissions;
                AttentionRingSubmissions += execution.Submissions;
                return output;
            }
            for (int head = 0; head < model.Heads; head++)
            {
                var qh = Tensor(rows, 64); var kh = Tensor(rows, 64); var vh = Tensor(rows, 64);
                var scores = Tensor(rows, rows); var probabilities = Tensor(rows, rows); var oh = Tensor(rows, 64);
                var batch = math.CreateBatch();
                batch.Gather(q, qh, 0, head * 64); batch.Gather(k, kh, 0, head * 64); batch.Gather(v, vh, 0, head * 64);
                batch.Attention(qh, kh, vh, scores, probabilities, oh, causal: true);
                batch.Scatter(oh, output, 0, head * 64); Execute(batch);
            }
            return output;
        }
    }
    public void Dispose()
    {
        foreach (var tensor in layers.Values.SelectMany(l => l.Resident)) tensor.Dispose();
        foreach (var tensor in norms.Values) tensor.Dispose();
    }
}
