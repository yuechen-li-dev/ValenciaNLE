# M2c — Aurelian residency and scheduling research

**Qualified result: real RTX 3070 execution of Whisper-weight encoder and decoder subgraphs, with controlled resident/streamed scheduling ablations. Full Aurelian Whisper transcription remains incomplete.** Product transcription works through the CPU reference provider described in [M2C.md](M2C.md). Neither the product's successful transcript nor these GPU tensor checks establishes full Aurelian inference.

For the measured 32-row, 256-tile configurations, streaming increased block wall latency about **1.48× tiny encoder / 1.37× tiny decoder subgraph**, and **2.19× large encoder / 1.55× large decoder subgraph**. Measured policy time was small; direct scheduling did not consistently recover that loss. GPU traces show **zero useful transfer/compute overlap**. The evidence points toward the submission/resource/transfer path before a scheduler-framework replacement. These ratios apply only to these subgraphs and configurations.

## Actual execution boundary

The isolated `research/Valencia.WhisperGpu` executable directly references existing `Aurelian.Inference`, `Aurelian.Graphics` and `Aurelian.Inference.Dominatus`. Product App/Engine do not gain those inference/scheduling dependencies. No code was ported from Stri-V, Stride or Aetheris; no Rust dependency or general inference runtime was introduced.

`WhisperWeights` reads the pinned real GGML checkpoints, indexes tensor offsets without loading the whole checkpoint, expands selected F16/F32 tensors to FP32, and limits a read to 16,777,216 elements. `ReferenceBlock.EncoderInput` uses actual generated speech decoded through Valencia at mono 16 kHz. MathNet's existing 400-point FFT is used because the inspected Aurelian FFT requires power-of-two lengths. The frontend uses checkpoint mel filters and actual conv1/GELU/conv2/GELU/positional weights. Frontend execution is CPU, not GPU.

The **encoder workload** evaluates the first encoder block: layer norm, Q/K/V projections, multihead self-attention, output projection, residual, layer norm, MLP/GELU/down projection and residual. It uses a prefix of 32 frontend activation rows, with attention restricted to those rows; it is not the full 1500-position encoder context. A separate 1-row shape sweep tests small-work sensitivity.

The **decoder workload** uses actual first-decoder-block weights for causal self-attention and MLP/residuals on the same fixed activation fixture. It omits cross-attention, token embeddings/output logits, tokenization/sampling and KV caching. The 1-row result is **not autoregressive latency/token**; there is no token-generation loop. This is a decoder-shaped block subgraph, not the complete decoder. No audio-seconds/s or tokens/s is inferred from it.

GPU execution reuses Aurelian `Normalize`, `AttentionTiled`/causal `Attention`, `Activate(GeluErf)`, residual and `StreamingLinear` kernels. The CPU scalar oracle independently evaluates those block formulas using the same real weights/inputs. Across 360 measured GPU passes and 72 warmups, all tensor checks passed; maximum absolute error was **8.59e-6**. Acceptance was RMS ≤ .003×max(1, oracle RMS) and max error ≤ .03×max(1, oracle RMS). This qualifies the measured operator/layout subgraphs against the scalar oracle. It is **not** an independent whisper.cpp intermediate-activation comparison and does not qualify the custom frontend as full-model equivalent.

## Controlled modes

| Mode | Residency and control | What actually changes |
|---|---|---|
| R0 resident-static2 | All selected affine block tiles retained on GPU; static depth 2 | No repeated affine weight copies; same streamed-linear shader/dispatch shapes |
| R1 stream-dominatus | Existing `LinearStreamed`, `DominatusComputeFeedforward`, `DominatusComputeSchedule` | Existing ring admission/policy and coarse stage scheduler |
| R2 stream-static2 | Same `LinearStreamed`, direct known stage order, fixed depth 2 | Removes Dominatus hot-path decisions |
| R2 stream-static1 | Same work, fixed depth 1 | Smaller weight working budget / less lookahead |
| R3 stream-static4 | Same work, up to four ring slots | Bounded lookahead experiment; **not dedicated-queue asynchronous prefetch** |
| R4 stream-stage-only | Dominatus outer stages, static depth 2 within each layer | Separates coarse orchestration from tile policy |

No Dominatus policy implementation was changed. R1 creates a fresh existing feedforward controller per pass; its observations evolve within that pass. This does not measure a long-lived controller trained across recordings. Static adapters live in the research executable. Existing Dominatus tests remain passing.

Two tile-row settings (64 and 256, reduced for the largest layer to the existing one-million-element tile bound) and depths 1/2/4 supply explicit affine-weight budgets. They do not cap unrelated Vulkan allocations. Within a fixed model/part/row/tile configuration, model values, FP32 precision, shader implementations, dispatch shapes, inputs, causal policy, output validation and device remain identical. Changing tile size is a separate batching configuration, not an isolated residency ablation.

`LinearResidentTiles` is a narrow Aurelian addition: caller-owned resident tiles run the **same** `CreateStreamingLinearBatch`/submission ring with no weight copy. It avoids comparing a fast different resident GEMM against a slower streamed kernel. Norm weights remain resident in every mode. Activations and temporary tensors follow the same allocation policy in all modes. No KV-cache budget is measured because this workload has no KV cache.

## Machine, provenance and timing

Windows build 26300, Ryzen 7 7700X, RTX 3070 **8192 MiB**, driver **610.88**, .NET SDK **10.0.401**, runtime **10.0.15**, Vulkan with validation enabled. Shader settings are held fixed; cooperative matrix execution was not enabled. Background applications, GPU clocks and driver caches were not locked, so five-trial dispersion matters. Modes run in a fixed order; this is not randomized statistically powered performance research.

Both mixed-F16/F32 checkpoints use revision `5359861c739e955e79d9a303bcbc70fb988958b1`; exact hashes/sizes are in [M2C.md](M2C.md) and the machine record. The audio fixture SHA-256 is `46DC5B6906EE7D1FBC28A7411E8FE61FA464D80D2ED4D022AEAFEC8FFC39D9CE`. Source and shader hashes, driver/device facts, memory telemetry, per-trial errors and raw result hashes are retained in [validation/m2c-gpu.json](validation/m2c-gpu.json). Representative actual timestamp traces are retained in [validation/m2c-gpu-traces.json](validation/m2c-gpu-traces.json); full traces remain under ignored `artifacts/local/m2c/gpu-*.json`.

One first pass warms each configuration; five subsequent complete block passes are measured. Frontend, Vulkan initialization, block loading/upload, first-pass and warm steady-state times are distinct fields. `loadMs` is per-configuration selected-weight unpack/packing and initial resident upload, not full-model cold latency. Initial shader compilation/cache state can affect first passes. Reported reference timings below include full transcription; GPU block timings exclude frontend/setup.

General-purpose telemetry additions in `IComputeStreamController` and `SubmissionRing` expose completed GPU compute/copy timestamp intervals and CPU recording/submission/completion-wait durations. Fences remain authoritative for query reads and resource reuse. CPU policy timers wrap Plan/Observe/AdmissionLimit; weight preparation and managed allocation counts are separate. Descriptor validation/configuration and command recording share a CPU recording interval. Task dispatch/dependency-resolution phases are not separately isolated. GPU driver allocations, total device-wide VRAM peak and exact GPU idle occupancy were not measured by these counters.

## Latency breakdown

Representative 256-tile, 32-row medians in ms:

| Model / subgraph | Mode | Wall | GPU compute | GPU weight copy | Policy CPU | Ring record CPU | Ring completion wait CPU |
|---|---|---:|---:|---:|---:|---:|---:|
| tiny encoder | R0 resident | 22.12 | 4.38 | 0 | .001 | 4.21 | 4.45 |
| tiny encoder | R1 Dominatus streaming | 32.66 | 3.63 | 2.80 | .202 | 4.52 | 4.89 |
| tiny encoder | R2 direct streaming | 31.27 | 3.65 | 2.81 | .001 | 4.53 | 4.42 |
| tiny decoder subgraph | R0 resident | 28.82 | 4.44 | 0 | .001 | 3.26 | 3.43 |
| tiny decoder subgraph | R1 Dominatus streaming | 39.47 | 3.68 | 2.83 | .095 | 3.39 | 4.59 |
| tiny decoder subgraph | R2 direct streaming | 38.17 | 3.66 | 2.79 | .001 | 3.47 | 4.52 |
| large encoder | R0 resident | 39.86 | 8.25 | 0 | .010 | 15.26 | 2.74 |
| large encoder | R1 Dominatus streaming | 87.17 | 17.30 | 12.35 | .350 | 16.47 | 4.80 |
| large encoder | R2 direct streaming | 84.55 | 16.81 | 12.34 | .013 | 16.37 | 4.42 |
| large decoder subgraph | R0 resident | 72.64 | 18.98 | 0 | .009 | 13.39 | 10.71 |
| large decoder subgraph | R1 Dominatus streaming | 112.93 | 19.92 | 12.42 | .221 | 14.49 | 4.52 |
| large decoder subgraph | R2 direct streaming | 112.85 | 19.13 | 12.40 | .011 | 14.39 | 5.03 |

These columns **must not be summed**: CPU work/waits can overlap GPU execution, and timing coverage differs. Policy, preparation, descriptor recording, pool/tensor allocation, synchronous non-linear operations, input upload/readback and teardown all contribute differently to wall time. `outerStageOverheadUpperBoundMs` includes more than Dominatus (input/readback/disposal); attributing it entirely to scheduler policy would be wrong. Small differences between R1/R2/R4 fall within visible trial/clock variation in several configurations. Actual timestamp copy durations also vary; a single bandwidth number is not justified.

Full median/min/max, cold setup/first-pass, allocation/transfer/submission counts, all budgets, scheduler ablations and 1-row results are in [the generated tables](validation/m2c-gpu-tables.md) and JSON. Five-sample nearest-rank p95 is the maximum; it is not a reliable production tail estimate.

## Residency budget and memory interpretation

The small checkpoint contains **151,041,024 bytes** of tensors when entirely expanded to FP32; large-v3 contains **6,173,962,240 bytes** before activation, descriptors, KV or driver overhead. Their on-disk F16 sizes are ~74 MiB and ~2.88 GiB. Neither whole checkpoint was uploaded/executed as a complete resident model. Large-v3 F16 might fit in 8 GiB with a suitable runtime; full FP32 feasibility was **not tested**, and this report does not claim mandatory streaming.

R0 owns only the selected block's padded affine weights (tiny 8.27 MiB at the 256 setting), plus shared norm weights. Streaming holds bounded per-layer weight slots, e.g. tiny 1.50/3.00 MiB for static depth 1/2; the depth-4 setting can remain at 3.00 MiB when a layer has fewer tiles. Some budgets grow through larger padding; the generated tables record actual allocated weight capacity, not an invented nominal cap. `streamingSlotBytes` is the maximum current layer's slot capacity, not total VRAM. Host arrays keep selected tile data for all six affine layers in every mode to hold disk access/weight values fixed; this is host-weight replay, not disk streaming of a whole checkpoint.

Memory counters report Aurelian-owned live/high-water allocations and cumulative allocator activity. Process RAM high-water is measured across each entire executable run, including CPU oracle/loader and earlier modes, not a mode-specific or device-wide VRAM peak. Initial resident setup and streamed steady-state copies are distinct. R0 sends no affine weights during warm passes; streaming recopies the same selected block weights per pass by design. No redundant *within-pass* tile loads were required by the plan. Persisting a whole model across tokens cannot be assessed without the missing token loop.

## What the traces establish

All measured streaming configurations showed **0 ms copy/compute intersection** across the captured useful-compute and nonzero-upload intervals. Two/four admitted command slots do not establish asynchronous transfer overlap. The current ring records copy → barrier → compute on its submission queue, with shared scratch dependencies; direct scheduling preserves that same synchronization. Nonlinear steps and decoder head loops also complete synchronously. The plant reports two created queues, but this path does not use a distinct transfer queue with independent copy/compute scheduling.

R3 is therefore a **negative lookahead/prefetch experiment**. It neither implements nor proves dedicated-queue prefetch. Deeper rings often increase wall time through resource/descriptor work. A future real transfer-queue experiment needs independent reusable weight slots and proper ownership/fence dependencies; this milestone does not introduce that backend redesign merely to obtain a more attractive result.

Coarser tiles reduce dispatches materially (tiny encoder 263 → 113; decoder subgraph 269 → 119), and warm wall time improves. The 1-row tiny decoder-shaped case still costs ~27.77 ms resident / 37.08 ms streamed, close to its 32-row result. This is evidence of launch/submission/resource cost at small work sizes. It is **not** evidence about real KV-cache token decoding or full encoder latency/chunk.

## Reference and long-form comparison

| Execution | Work actually completed | Observed time | Valid comparison |
|---|---|---:|---|
| whisper.cpp CPU tiny.en | Full 9.47 s utterance transcription, model load/preprocessing/word extraction | 597 ms analysis / 903 ms CLI process | Working product baseline |
| whisper.cpp CPU large-v3 | Full same utterance through MP4/AAC | 19.63 s analysis | Separate architecture/size configuration |
| Aurelian tiny encoder | One 32-row first-block subgraph after CPU frontend | 22.12 ms resident | R0/R1/R2/R3/R4 subgraph ablation only |
| Aurelian large decoder subgraph | One 32-row causal self-attention/MLP subgraph | 72.64 ms resident | Same selected weights/kernels across modes only |
| Full Aurelian Whisper | Encoder + cross-attentive autoregressive decoder + output tokens | **Unavailable** | No end-to-end speed or accuracy ratio |

The external reference uses encoded MP4 audio while the GPU fixture uses the original WAV; the controlled GPU modes all use the exact same WAV/activation arrays. These are not equivalent end-to-end workloads, so Aurelian-versus-reference competitiveness is **unresolved**. The scalar oracle is the equivalent-work correctness control; it was not used as a claimed optimized reference-performance baseline.

Reference product tests process 120 s of continuous repeated speech in 3.39 s/five windows, and two hours of sparse-speech FLAC in 2.67 s/258 windows with silence gating. This demonstrates bounded chunk coordinates and cache progression, not sustained two-hour Aurelian inference. Continuous-input RAM plateau, long-form full-GPU stability, per-token tail latency, and isolated decode/preprocessing/alignment/serialization timings remain unqualified. Model/job total and cache timing fields are measured; finer reference phase attribution is not fabricated.

## Decision and remaining blockers

**Outcome A for the tested policy: keep Dominatus; do not replace it based on this evidence.** The measured hot-path decision work is small, and bypassing it does not consistently repair latency. Keep coarse orchestration available. The next narrow GPU optimization should target reusable descriptors/activation allocations and serialized host/queue handoffs, then compare an actually independent transfer path before increasing policy complexity. Static schedules remain useful controls and can own deterministic dependency order without a framework migration.

Answers to the research questions:

1. Selected-block residency saves approximately 1.4–2.2× wall time in the representative configurations above; full-model slowdown is unknown.
2. Timestamped repeated copies account for a measured portion, not the entire gap. “Unavoidable PCIe cost” cannot be inferred from these timings because implementation and batching also matter.
3. Tile-policy evaluation is a fraction of a millisecond in these configurations, substantially smaller than the tens-of-ms gap.
4. Bypassing Dominatus leaves zero overlap and similar synchronization behavior. The evidence does not identify Dominatus as the source of the serial queue/barrier design.
5. Warm R0 eliminates repeated weight traffic; streamed tiles move once per scheduled affine layer/pass. Full-model/token redundancy is unmeasured.
6. Fine tiles increase dispatch count and latency; coarser batching helps these fixtures.
7. Small decoder-shaped work remains expensive; real autoregressive sensitivity/KV behavior is unknown.
8. More lookahead did not create useful overlap; true asynchronous prefetch is unimplemented.
9. The best observed budget depends on shape/tile size; the tables include small-budget cases that outperform deeper rings. There is no qualified whole-model VRAM optimum.
10. Full Aurelian/reference competitiveness is unknown because equivalent full-model inference is missing.

The next concrete blocker is **complete model execution**, specifically the remaining encoder stack and decoder cross-attention/KV cache/token/logit path. A full-model resident allocation/accuracy baseline must precede whole-model memory-pressure/throughput claims. Building that as an ad hoc second runtime here would expand scope and bypass existing inference ownership; this increment leaves a bounded readable adapter and measured seams instead. Track B is **partial**, even though its representative hardware experiment works. Track A remains independently usable.

## Reproduce

```powershell
.\scripts\DownloadWhisperModels.ps1 -Model tiny.en,large-v3
$env:VALENCIA_MODEL_DIR = "$PWD\artifacts\local\m2c\models"
.\scripts\Build.ps1
.\scripts\TranscriptionSmoke.ps1 -Native
.\scripts\WhisperGpuResearch.ps1 -Trials 5
# Optional standard-library report generation; Python is research tooling only.
python research/SummarizeM2C.py
```

The summarizer also reads the test/regression evidence and the separately measured `reference-large.json`; regenerate that with `Valencia.Cli transcribe <project> --asset <id> --stream 1 --model large-v3` when reproducing the complete record. It does not silently run or download a model. The two models consumed ~2.96 GiB under Valencia's ignored artifacts directory with the user's explicit authorization. No weights or generated media are committed as source.
