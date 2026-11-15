using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Aurelian.Graphics.Vulkan.ComputeMath;
using Aurelian.Inference;
using Aurelian.Inference.Dominatus;
using Valencia.Analysis;

namespace Valencia.WhisperGpu;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length < 3) throw new ArgumentException("Usage: model-id audio.wav output.json [rows=32] [trials=5]");
            var modelIdentity = WhisperModels.Resolve(args[0]);
            var loader = Stopwatch.StartNew();
            var model = new WhisperWeights(modelIdentity.Path);
            int rows = args.Length > 3 ? int.Parse(args[3]) : 32;
            int trials = args.Length > 4 ? int.Parse(args[4]) : 5;
            if (rows is < 1 or > 128 || trials is < 1 or > 20) throw new ArgumentException("Bounded rows/trials required.");
            var input = ReferenceBlock.EncoderInput(model, args[1], rows);
            double frontendMs = loader.Elapsed.TotalMilliseconds;
            loader.Restart();
            using var gpu = VulkanInferenceSession.Create(new() { MaximumTensorElements = 16777216, EnableValidation = true });
            double gpuInitializationMs = loader.Elapsed.TotalMilliseconds;
            var results = new List<object>();
            foreach (string part in new[] { "encoder", "decoder-self-attention-mlp" })
            {
                bool causal = part != "encoder";
                string prefix = causal ? "decoder.blocks.0" : "encoder.blocks.0";
                var timer = Stopwatch.StartNew();
                var expected = ReferenceBlock.Run(model, input, rows, prefix, causal);
                double oracleMs = timer.Elapsed.TotalMilliseconds;
                foreach (int tileRows in new[] { 64, 256 })
                foreach (string mode in new[] { "R0-resident-static2", "R1-stream-dominatus", "R2-stream-static2", "R3-stream-static4", "R4-stream-stage-only", "R2-stream-static1" })
                {
                    bool resident = mode.StartsWith("R0", StringComparison.Ordinal);
                    var loading = Stopwatch.StartNew();
                    using var block = new GpuBlock(gpu.Compute, model, prefix, tileRows, resident);
                    double loadMs = loading.Elapsed.TotalMilliseconds;
                    double firstPassMs = 0;
                    for (int trial = -1; trial < trials; trial++)
                    {
                        using var policy = mode == "R1-stream-dominatus" ? new DominatusComputeFeedforward() : null;
                        int depth = mode.EndsWith("static1", StringComparison.Ordinal) ? 1 : mode.StartsWith("R3", StringComparison.Ordinal) ? 4 : 2;
                        var controller = new MeasuredController(policy ?? (IComputeStreamController)new StaticController(depth));
                        long allocated = GC.GetTotalAllocatedBytes();
                        timer.Restart();
                        float[] actual = block.Run(input, rows, causal, controller, mode is "R1-stream-dominatus" or "R4-stream-stage-only");
                        double wallMs = timer.Elapsed.TotalMilliseconds;
                        double maximumError = actual.Zip(expected, (a, b) => Math.Abs((double)a - b)).Max();
                        double rmsError = Math.Sqrt(actual.Zip(expected, (a, b) => Math.Pow((double)a - b, 2)).Average());
                        double scale = Math.Sqrt(expected.Select(x => (double)x * x).Average());
                        bool correct = double.IsFinite(maximumError) && rmsError <= .003 * Math.Max(1, scale) && maximumError <= .03 * Math.Max(1, scale);
                        if (!correct) throw new InvalidDataException($"GPU mismatch {part}/{mode}: max={maximumError}, rms={rmsError}, scale={scale}");
                        if (trial < 0)
                        {
                            firstPassMs = wallMs;
                            continue;
                        }
                        var completed = controller.Events.Where(e => e.Completed).ToArray();
                        double overlapMs = 0;
                        foreach (var copy in completed.Where(e => e.UploadBytes > 0 && e.TransferStartTicks.HasValue))
                            foreach (var compute in completed.Where(e => e.ComputeStartTicks.HasValue))
                            {
                                ulong start = Math.Max(copy.TransferStartTicks!.Value, compute.ComputeStartTicks!.Value);
                                ulong end = Math.Min(copy.TransferEndTicks!.Value, compute.ComputeEndTicks!.Value);
                                if (end > start) overlapMs += (end - start) * copy.NanosecondsPerTick!.Value / 1e6;
                            }
                        results.Add(new {
                            part, mode, rows, trial, tileRows, loadMs, firstPassMs, wallMs, oracleMs, correct, maximumError, rmsError,
                            schedulerDecisionMs = controller.CpuMs,
                            ringCpuRecordingMs = completed.Sum(e => e.CpuRecordingMilliseconds),
                            ringCpuSubmissionMs = completed.Sum(e => e.CpuSubmissionMilliseconds),
                            ringCpuCompletionWaitMs = completed.Sum(e => e.CpuCompletionWaitMilliseconds),
                            ringWeightPreparationMs = block.LinearResults.Sum(r => r.PreparationMilliseconds),
                            outerStageOverheadUpperBoundMs = Math.Max(0, wallMs - block.StageActionsMs),
                            gpuComputeMs = block.LinearResults.Sum(r => r.GpuMilliseconds ?? 0) + block.OtherGpuMs,
                            gpuTransferMs = completed.Where(e => e.UploadBytes > 0).Sum(e => e.TransferGpuMilliseconds ?? 0), overlapMs,
                            residentSelectedWeightBytes = block.ResidentWeightBytes,
                            streamingSlotBytes = block.LinearResults.Max(r => r.WeightBufferBytes),
                            hostToDeviceWeightBytes = completed.Aggregate(0UL, (sum, e) => sum + e.UploadBytes),
                            additionalInputUploadBytes = (long)input.Length * 4, deviceToHostBytes = (long)actual.Length * 4,
                            dispatches = block.LinearResults.Sum(r => r.Dispatches) + block.OtherDispatches,
                            submissions = completed.Length + block.OtherSubmissions - block.AttentionRingSubmissions,
                            completionWaits = block.LinearResults.Sum(r => r.CompletionWaits),
                            allocatedManagedBytes = GC.GetTotalAllocatedBytes() - allocated,
                            memory = gpu.Compute.MemoryTelemetry, traces = completed
                        });
                    }
                }
            }
            string destination = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, JsonSerializer.Serialize(new {
                scope = "Measured Whisper block subgraphs, not full model inference/transcription. Decoder cross-attention, logits, KV cache and token generation are not implemented here.",
                model = modelIdentity, audioSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))), frontendMs, gpuInitializationMs,
                activation = "Real-audio native16k PCM → MathNet 400-point STFT → checkpoint mel filters → Whisper conv1/GELU/conv2/GELU/position prefix. Same activation fixture reused for decoder-shape subgraph.",
                precision = "checkpoint F16 expanded to FP32; identical StreamingLinear shaders/tiles in resident and streaming modes; CPU scalar block oracle",
                gpu = gpu.Plant.Facts, runtime = Environment.Version.ToString(),
                checkpointTensors = model.Tensors.Count,
                wholeCheckpointFp32TensorBytes = model.Tensors.Values.Sum(t => (long)t.Elements * 4),
                processPeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                results
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            Console.WriteLine(destination);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
