# M2e rendering and A/V stabilization — 2026-11-15

> **2026-11-16 recurrence:** the pointer-feedback qualification reproduced the
> historical approximately 250 ms stall in current and pre-change executables.
> The blocked phase is swapchain acquisition/fence waiting, not the measured GPU
> draw passes. Rapid-scrub A/V qualification fails. See
> [POINTER_FEEDBACK.md](POINTER_FEEDBACK.md#isolated-existing-presentation-blocker)
> and [current evidence](validation/pointer-feedback.json). The historical
> "not reproduced" statements below describe that earlier run, not current status.

## Outcome and qualification boundary

**Verified synchronization correction; historical GPU-stall cause remains unconfirmed.**
The final source passes all 19 native acceptance cases, including the unchanged
150 ms A/V contract, now observed after presentation as well as before drawing.
253 Valencia tests, 351 Aurelian graphics tests and 2051 Copeland compiler/backend
tests pass with no failures/skips. Native edited export independently decodes and
matches its intended selections, gaps, duration and codecs.

The original recurring approximately 250 ms host `NativeVideoPresenter.Draw()`
delay did **not** reproduce in the first current-source M0 trials or the previously
published executable. The user reported no known system/workload changes. We do
not claim a driver, Vulkan fence, GPU-kernel root cause or GPU throughput fix for
that historical incident. Its old trace measured host wall time, not GPU execution.

A different but relevant timing failure was reproduced and fixed: an advancing
audio clock plus a blocked UI left bounded preview FIFOs full of obsolete frames.
That is a confirmed cause of A/V lag after UI stalls. Thus this is a working,
qualified partial correction rather than invented closure of an unreproduced bug.

Machine evidence: [m2e-render-stabilization.json](validation/m2e-render-stabilization.json).
Frontend contracts: [CLAUDE_FRONTEND_HANDOFF.md](CLAUDE_FRONTEND_HANDOFF.md).

## Reproduced baseline and causal control

Initial current-source M0 runs passed at 33–35 ms pre-present maximum skew. The
published executable also passed. Controlled static/dynamic/UI comparisons drew
in milliseconds, with sub-millisecond GPU pass intervals and no validation errors.

With the real high-resolution qualification workflow, screenshot/capture work
produced a **724.55 ms UI-loop gap**. The 1080p run failed at **619.125 ms** maximum
skew and zero audio underruns. Its captured maximum draw wall time was only
**32.998 ms**; the 725 ms loop gap did not occur inside that GPU draw. GC pause
increments were small. A contemporaneous AV comparison also failed at 697 ms
skew under regression-test CPU load. These failures are preserved under
`artifacts/local/render-stabilization/final/`, not silently replaced by later runs.

The source confirms the mechanism: both preview producers used short bounded
channels with `FullMode.Wait`. During a UI pause the video producer blocked on old
frames while the independent audio device continued. On return, the UI drained a
few obsolete frames before the producer could run again. A larger queue would
retain more stale work; an unpaced drop-oldest queue would instead decode to EOF.

A controlled counterfactual used the **same App, Rendering, Aurelian and driver**,
replacing only `Valencia.Media.dll` with this-turn pre-fix source-built output.
An explicitly injected 700 ms UI sleep every sixty probe iterations yielded:

| Identical probe, renderer and injected UI stalls | Maximum post-present A/V skew | Underruns |
|---|---:|---:|
| Pre-fix media engine | **552.979 ms**, failed | 0 |
| Clock-paced media engine | **42.229 ms**, passed | 0 |

This is a real device-clock/FFmpeg regression control. The injected sleep is **not**
claimed to reproduce the historical GPU/driver delay. Binary hashes and exact
arguments are retained in the evidence. An earlier control before the additional
post-present check measured 535/35 ms and is also retained.

## Targeted implementation

* `Valencia.Media/PreviewFrameClock.cs` attaches the generation's device clock once
  it exists. The first video frame can prebuffer before audio initialization.
  Subsequent frames are prepared one ahead and published only when due.
* M0 `DecodeSession` explicitly opts into realtime mode from `PlaybackSession`.
  That mode uses the existing bounded capacity with `DropOldest` for **managed CPU
  preview frames**. Default raw decoding retains its lossless `Wait` behavior;
  frame-order/drain tests still decode every source frame.
* `SequencePlayback` uses exact sequence ticks for the same per-generation pacing.
  Source timestamp mapping, evaluation, audio generation and offline export do not
  use this preview clock and are unchanged.
* Device-clock queries are serialized with producers. Transport/frame selection
  remain owner-thread operations. No GPU method is called from these workers.
* Shutdown stops audio, cancels/joins producers, then disposes the device they read.
  Cleanup still runs after stop failure. Async library joins use
  `ConfigureAwait(false)`: pacing exposed a shutdown continuation that otherwise
  required UI-context pumping while `EditorWindow.Dispose` synchronously joined it.
  The stalled process had already written a passing workflow result, demonstrating
  why **process exit** must also be required. That trial was excluded and rerun.
* `PlayerWindow`, `EditorWindow` and the probe additionally sample the device clock
  after `Draw()`/presentation. This prevents a fresh pre-submit frame from hiding
  a slow GPU/present call. The existing **150 ms** limit was not relaxed.
* Creator/high-resolution acceptance drivers now reject bad timing and captured
  Vulkan errors. `RenderStabilization.ps1` enforces process-exit deadlines.

No asynchronous GPU upload, larger video queue, render graph, frontend redesign,
driver-setting change or weaker resource synchronization is part of this fix.
No packages or assemblies were added. The existing FFmpeg/NAudio/Machina/Aurelian
owners remain in place; Core/Engine v1 commands and persistence are untouched.

## Frame-path instrumentation

Set `VALENCIA_RENDER_TRACE` to an artifact JSON path. Native tracing is opt-in and
retains at most 4096 frame records plus bounded post-present observations. It
reports trace truncation and does not retain decoded pixels. Transient media
diagnostic records are not saved into projects or editing history.

| Phase | Measurement |
|---|---|
| FFmpeg packet/decode and RGBA conversion | Host monotonic read/preparation durations and decoded-ready ticks |
| Timeline evaluation/frame selection | Host evaluation/selection durations, exact source/sequence coordinates |
| CPU-to-GPU upload | Staging allocation/write, recording, queue-submit, fence wait, retirement host times |
| Texture copy | Existing Aurelian timestamp helper around copy/layout-transition commands |
| 2D passes | Vertex preparation, command recording, CPU submit/wait/retirement; raw GPU pass timestamp intervals |
| Presentation | Acquire including image-fence wait, compositor copy/record/submit/wait, present-call wall time |
| Machina | Declaration/lowering/presentation construction and native realization wall times |
| Runtime | GC generation counts, cumulative pause/allocation observations, sampled process working set |
| Playback | Post-present device-clock observations tied to frame identity |

`Vulkan3DGpuTimings` is the existing arbitrary named-pass timestamp utility reused
inside 2D/upload diagnostics; no 3D renderer executes. Query pools are fixed per
renderer/uploader and read only after existing completion fences. Async 2D mode
does not read an unfinished timestamp pool. GPU intervals are not CPU submission
times and the clocks are not calibrated against Stopwatch or a display/acoustic
instrument. Acquire-call and acquire-fence time are combined; presentation-copy
subphases and display-engine GPU time are not individually isolated.

`VulkanValidationCapture` installs a debug-utils callback for traced native runs,
counts warnings/errors and retains the first 128 messages. Capture starts after
device creation. Default Khronos validation was available and reported **zero
warnings and errors** in final traced cases. Synchronization-validation-specific
features and forced device loss were not exercised.

## Native measurements

Windows 11/RTX 3070 8 GiB, driver 610.88, Ryzen 7 7700X, .NET SDK 10.0.401/runtime
10.0.15, FFmpeg 7.1.1. Software video decode, CPU RGBA transfer, Vulkan presentation
and NAudio 3 WaveOut. OBS fixtures are the existing 1080p60/1440p60 H.264 recordings
with three independent AAC stereo/48 kHz streams; the small comparison fixture is
640×360/30 H.264/AAC. No hardware decode or acoustic/first-photon claim is made.

All comparisons use the real native presenter/window. Static/dynamic/UI probes
target 60 loop iterations per second; video retains actual source PTS. Ten warmup
frames are excluded below; nearest-rank p95/p99 and all raw/max values remain in
the machine report. These are **host Draw wall times**, not total UI iteration or
video FPS. Captures, seek opens and setup can cost more outside Draw.

| Case | Samples | p50 | p95 | p99 | Max | GPU pass-sum p50 |
|---|---:|---:|---:|---:|---:|---:|
| Resident static texture, cached UI | 230 | 1.603 ms | 2.074 | 2.197 | 2.942 | 0.179 ms |
| Dynamic CPU RGBA upload | 230 | 1.225 | 1.329 | 1.381 | 1.507 | 0.246 |
| Video only | 230 | 1.329 | 1.929 | 2.085 | 2.117 | 0.250 |
| Audio/video | 230 | 1.365 | 1.937 | 2.007 | 3.797 | 0.272 |
| Changing Machina declarations | 230 | 1.790 | 2.110 | 2.226 | 4.790 | 0.260 |
| Edited timeline, cuts/gaps/5000-clip test | 1949 | 4.854 | 5.888 | 6.522 | 15.255 | 0.527 |
| Captioned/aspect-switch creator workflow | 804 | 4.968 | 5.702 | 5.987 | 21.043 | 0.236 |
| Actual OBS 1080p60 qualification | 3485 | 4.993 | 6.034 | 7.002 | 11.187 | 0.395 |
| Actual OBS 1440p60 qualification | 4086 | 5.008 | 6.129 | 7.341 | 15.100 | 0.352 |

The 1440p trace capped at 4096 retained frames and explicitly reports 443 omitted
late records. It is not a complete per-frame trace of that process. The first ten
frames and all-frame maximum are retained separately. Small distributions do not
establish rare-event probabilities. No recurring 250 ms draw was observed in
these tested conditions; this does not identify why it occurred historically.

* **10/10 final M0 trials** passed, alternating tracing on/off. Maximum post-present
  skew ranged **81.6–100.2 ms**, including pause/resume, seek and resize. Final
  generation underruns were zero. Earlier uninstrumented current-source trials
  also passed; profiling is not required to make playback pass.
* Edited desktop maximum skew: **46.8 ms**. Caption/aspect workflow: **42.5 ms**.
* 1080p: **59.59 uploaded video frames/s**, **44.6 ms** maximum observed skew,
  zero underruns over **10.002 actual seconds**. Fresh concurrent proxy analysis
  was a cache miss and ran about **841 ms**.
* 1440p: **58.48 frames/s**, **51.5 ms** skew, zero underruns over **10.003 actual
  seconds**. Fresh proxy analysis ran about **1189 ms**. This is not multi-hour
  playback/drift qualification or proof every UI iteration meets 16.67 ms.

## Tests, exports and reproducibility

**253 Valencia tests:** Core 73, Persistence 21, Media/UI 74, Programming 85.
New tests cover real-decoder stale-frame eviction, paused clock bounds,
cancellation before clock attachment, UI-context-independent shutdown, and timing/
validation acceptance including historical bad values and NaN. Hardware-dependent
shutdown coverage is marked `AudioHardware`; no missing device was counted as a pass.

**Copeland:** 351 Aurelian graphics tests, 1777 compiler/profile tests and 274 C#
backend tests passed. No failures/skips. Release solution build had zero
warnings/errors. Existing independent FFmpeg export checks passed after the clock
change; the native editor's ten-second H.264/AAC export had correct source frames,
black/silent gap, second-source ordering and duration. GPU/audio clocks do not
participate in offline timestamp generation.

```powershell
dotnet build Valencia.slnx -c Release -m:1 -nr:false -p:UseSharedCompilation=false
dotnet publish src/Valencia.App -c Release -r win-x64 --self-contained false `
  -o artifacts/local/render-stabilization/final-host
scripts/RenderStabilization.ps1 -HostDirectory artifacts/local/render-stabilization/final-host `
  -OutputDirectory artifacts/local/render-stabilization/qualified
scripts/VerifyEditorExport.ps1 -Directory artifacts/local/render-stabilization/qualified/editor `
  -SourcePath artifacts/local/fixtures/av.mp4
scripts/RecordRenderStabilizationEvidence.ps1
```

Exact per-case arguments, exit codes, source/binary hashes, traces and test counters
are indexed by the machine report. `--render-probe av --stall-ms 700` is explicit
UI-stall fault injection. The control directory is a copied frozen build with only
the pre-fix media assembly substituted; it is not a distribution candidate.

One intermediate rebuild overlapped a live source-bin executable and hit an output
lock. Those trials are excluded. All final native trials run immutable published
directories and require successful process exit. Do not rebuild a live run folder.

## Remaining open work

The historical 250 ms host draw incident remains **unexplained and not reproduced**.
The next useful evidence is a recurrence with these fine-grained traces; altering
fences or the driver without it would be speculative. No claim of a historical
GPU-stall regression test is made: the new tests reproduce the confirmed preview
FIFO/context defects. Cold font creation/readback/PNG encoding can still block UI
callbacks; the preview now recovers to current device time afterward, but does not
make such callbacks responsive. Broader footage, long-duration drift, forced
device loss, varied refresh rates/DPI and physical input usability remain outside
this qualification. Frontend integration must preserve the lifetime/clock rules
in the updated handoff rather than assuming the GPU incident is closed.
