# M2b — Media analysis and automated rough cuts

Implemented on Windows/.NET 10. This milestone produces immutable source facts and deterministic edit proposals, then commits only through the existing editing dispatcher. No LLM, transcription, VAD model, Node runtime or new codec/backend is involved.

## Boundaries and layout

- **Valencia.Media/AnalysisStream** is a worker-confined FFmpeg pull reader. Audio converts sample format to interleaved float at the original rate/channel count. Video converts only sampled frames to 64×36 luma. M0 playback conversion remains stereo 48 kHz; its path was not rewritten.
- **Valencia.Analysis** references Media/Core and contains audio/scene algorithms, immutable artifacts, waveform queries and the local cache. It has no editing, UI or persistence reference and cannot mutate a timeline.
- **Valencia.Engine** owns bounded analysis jobs, typed request/result contracts and the rough-cut planner. It uses M2a plans, `EditingSession` and the existing ripple operation. Project persistence remains Deliverance and excludes derived data.
- **App** prepares waveform indices on workers and draws visible GPU rectangles. **Scripting**, **CLI** and the existing **MCP** adapter delegate to Engine.

The only new production assembly is Valencia.Analysis. No new NuGet package was added. FFmpeg.AutoGen 7.1.1, native FFmpeg 7.1, NAudio 3, existing Copeland/Roslyn, Machina and Aurelian remain the dependencies.

## Source clocks and exact timing

Stream identity is `(asset GUID, native stream index)`. Results retain original rational `MediaTimestamp` anchors plus exact `SourcePosition.Offset` ticks. Sample windows are anchored at the first decoded PTS and advanced by integer sample counts at the original sample rate. There is no nominal-video-FPS substitution. Scene candidates use the actual decoded frame PTS, including VFR fixtures.

Four spaces are explicit:

1. Container origin: `ImportedAsset.ContainerStart`, retained in artifacts.
2. Absolute stream PTS: native integer value/timebase, retained in source positions/candidates.
3. Normalized source time: `AnalysisTime.Normalize(position, containerStart)` subtracts that origin.
4. Sequence time: `AnalysisTime.ToTimeline(clip, source)` adds the clip's timeline start to its exact source offset. This boundary conversion permits `SourceOut`; point evaluation remains half-open through Core's `MapToSource`.

All intervals are `[start,end)`. Settings use integer milliseconds; minimum sample counts round upward. Windows/hops have the same length, `ceil(rate × windowMs / 1000)` samples; a final partial window is retained. Core's checked 254,016,000,000-tick grid and overflow handling remain authoritative for editing. Unrepresentable conversions fail rather than silently round to nominal FPS. Scripting/wire Int64 values remain canonical decimal strings.

Coarse stream clocks quantize individual audio-block PTS. The analyzer uses a continuous sample clock within an epoch; deviations larger than two original stream ticks or one sample, whichever is larger, start a new epoch. It flushes pending windows/bins and never merges across epochs. Epochs and diagnostics preserve detectable discontinuities; sub-quantum timing cannot be inferred. The planner rejects discontinuous audio rather than guessing editing semantics. Backward video PTS reset the scene comparison epoch; forward/VFR spacing remains in the actual candidate timestamps.

Analysis sample coordinates are exact; the preexisting random-seek decoder can introduce a sub-millisecond sample offset with millisecond-clock Matroska. Independent export verification therefore measures PCM alignment within the source's 1 ms clock quantum. It does not claim sample-exact random seeking or physical display/audio latency.

## Audio analysis

One selected stream is decoded once to feed all three audio features. Independent OBS streams are never automatically mixed for analysis. Mono/stereo is supported; more than two channels and mid-stream format changes fail explicitly.

`AudioSettings` defaults:

| Setting | Default | Meaning |
|---|---:|---|
| thresholdDb | -35 | Silence when maximum per-channel window RMS is at/below this dBFS |
| activeDb | -20 | Active above this threshold; otherwise lowActivity |
| windowMs | 20 | Disjoint RMS window and hop |
| minimumSilenceMs | 800 | Minimum returned candidate interval after merging/padding |
| minimumActivityMs | 0 | Merge silence across activity gaps no longer than this |
| mergeSilenceMs | 0 | Additional explicit silence-gap merge allowance |
| leadingPaddingMs / trailingPaddingMs | 0 / 0 | Shrink returned silence candidate boundaries |

Channel reduction uses the **maximum channel RMS**, not averaged channel samples. Stereo anti-phase speech/tone therefore cannot cancel into false silence. Activity records store measured RMS, not a speech probability. Active/lowActivity/silence is energy segmentation, not speech recognition or diarization. The optional short-activity merge is an explicit editing-candidate heuristic; raw activity records remain inspectable.

Waveform bins contain per-channel minimum, maximum and RMS with sample counts and exact source boundaries. Five energy windows initially form one base bin (100 ms by default). Pair reduction builds a peak/RMS pyramid; RMS aggregation is sample-weighted. At 65,536 base bins the base is pair-reduced and subsequent spacing doubles. Thus long recordings progressively lose fine waveform display resolution while PCM residency stays constant. No PCM arrays span the recording. Silence/activity retain window timing independently of waveform coarsening.

Each activity/scene list has a 65,536-record budget. Excessive fragmentation fails diagnostically; it is not silently dropped. A ten-minute synthetic streaming test crosses the adaptive waveform threshold and verifies all 4.8 million samples remain represented. This is not a multi-hour OBS qualification.

## Scene changes

`SceneSettings`: threshold .35, everyNthFrame 1. All dependent compressed frames still decode; cadence skips feature extraction, not required codec work. Two downsampled luma buffers are retained. The score is `.7 × mean absolute luma difference / 255 + .3 × normalized 16-bin histogram distance`. Candidates retain native PTS, score and epoch.

Generated static, fade, hard-cut and VFR fixtures are tested. This detector finds hard cuts/substantial visual changes. Bright menus or flashes can be candidates; equal-luma color-only changes can be missed. There is no semantic scene or highlight understanding. Scene results are available to callers but do not rank or alter the initial silence-based rough cut.

## Artifacts and cache

`AnalysisArtifact` v1 records asset/stream identity, fingerprint, kind, algorithm/version, canonical parameters, decoder policy, container origin, completion, sample metadata/count, immutable waveform levels, silence/activity/scenes and diagnostics. Result schemas originate in **Engine/Contracts/analysis.obj.ts**, using Copeland's existing `type` profile/TSON catalog. JSON is wire/cache serialization, not a separate schema authoring language.

Cache key: asset GUID + sampled source fingerprint + stream + algorithm/version + canonical typed parameters + actual native avcodec/avutil/swresample/swscale versions and normalization policy. Algorithms: `rms-peak-v1`, `luma64-hist16-v1`.

Fingerprint `quick-v1` hashes file length, UTC modification ticks, and up to 64 KiB each at the beginning, middle and end. Import does not hash a multi-hour file. **This is not a full-content identity proof:** edits outside sampled blocks with preserved length/mtime may evade detection. Analysis rechecks the fingerprint before publication. Generated plans also carry typed `sourcePreconditions`; dry-run and commit reject changed/missing sources independently of project revision. Relinking clears native waveform/proposal state.

Default cache: `%LOCALAPPDATA%/Valencia/Analysis-v1`. MCP uses `.valencia-analysis` inside its explicit workspace. Native tests can override the engine cache directory. The store has a 512 MiB completed-file LRU budget, 64 MiB maximum artifact, and at most one extra bounded working publication per shared cache directory. Base waveform/interval budgets also bound in-memory derived state. An exclusive file lease serializes publishers across engine/process instances; abandoned partial files are removed after acquiring it. Readers need no publisher lease.

Files contain a versioned header, SHA-256 payload checksum and JSON. Writes use a same-directory temporary file, bounded output stream, flush and atomic rename. Cancellation removes incomplete output and preserves earlier cache files. Corrupt/truncated/version-invalid artifacts become cache misses and regenerate. LRU uses explicitly refreshed cache-file modification times. Cache hits, cache-read time, analysis time and cache-write time are exposed in job status. Cache artifacts are optional/regenerable and never embedded in project snapshots.

## Jobs and APIs

`EditingEngine.Analysis` exposes Start, Status, WaitAsync, Cancel, ResultAsync and its cache. One analysis decode runs per engine; equivalent active requests return the same job ID. An incompatible simultaneous request is rejected. At most 32 status records remain; results stay on disk. Compatible waveform/silence/activity requests share an audio pass and artifact. A different parameter set currently requires another pass. Analysis and export have separate bounded workers.

States are running/completed/cancelled/failed, with progress, processed units, cache-hit flag, diagnostics and timing. Cancellation is checked per decoded frame/block and during cache I/O; decoder resources are worker-owned/disposed. Native FFmpeg calls blocked on local storage are not forcibly interrupted. Publication is the completion point; a late cancellation cannot turn a fully published result into a partial artifact.

`AnalysisContracts.Page` returns summaries or pages of silence/activity/scenes/waveform data; limit 1–128. A prepared `WaveformIndex` chooses existing pyramid levels and performs time-range lookup. Viewport changes do not re-decode or rebuild analysis.

## Rough-cut policy and semantics

The initial planner supports unlocked, **aligned single-source track groups**. Every nonempty track must reference the analyzed recording, have matching clip placements/source ranges and preserve source order. The selected analyzed audio stream must be present. Empty tracks are harmless. Unrelated music/media, differently trimmed tracks, locked tracks, reordering and source discontinuities are rejected. All aligned source tracks ripple together, including other OBS streams; this is the explicit retention/linking policy, not inferred Premiere linking.

`RoughCutPolicy` defaults: minimumSegmentMs 250, minimumRemovalMs 800, prePaddingMs/postPaddingMs 100 each, mergeDistanceMs 0, maximumRetainedGapMs 0, optional targetDurationMs. Threshold/minimum detected silence belong to the artifact's AudioSettings.

The planner intersects silence with each clip's source range, shrinks removals by activity padding and any extra retained gap allowance, then rounds removal starts **up** and ends **down** to sequence frames. Existing source in-points, splits, moves and gaps are respected. Short retained fragments can be absorbed/merged according to minimumSegmentMs; mergeDistanceMs explicitly permits removing intervening material. MaximumRetainedGapMs retains that much gap when greater than protected padding; padding takes precedence. Target duration is best effort: active material is not arbitrarily truncated to satisfy it.

Removals emit existing ripple operations in descending sequence-time order, with explicit participating tracks. The planner simulates those operations in a separate zero-history EditingSession to resolve split IDs and duration. Right-fragment IDs are SHA-256 derived from the fixed project/revision/artifact/range/clip. One M2a transaction commits the final plan. The live session is untouched during analysis/planning/dry-run. Revision/source preconditions, Core overlap/lock/bounds checks and undo/redo remain authoritative.

Proposals expose retained source ranges, removed timeline ranges, semantic before/after clip diff, estimated duration and target status. No-op proposals contain no plan (M2a rejects empty transactions). Fully removed sequences are valid editing results but cannot be exported by the existing nonempty-sequence exporter. Plans are capped at 500 operations; larger transformations require narrower editorial scope.

## Copeland, CLI and MCP

`ScriptApi` adds StartAnalysis, AnalyzeAudio, AnalyzeScenes, AnalysisStatus/WaitAnalysis/CancelAnalysis, paged AnalysisResult, RoughCut and RoughCutPlan. Existing ValidatePlan/ApplyPlan, Save, undo/redo and export are reused. Methods use the existing supported CLR signatures and canonical string ticks; `await` or a new runtime is not required. **examples/auto-roughcut.ts** executes the full analysis → dry-run → commit → undo/redo → save → export pipeline. Trusted CLR privileges and cooperative cancellation remain unchanged.

One bounded generic Copeland fix allows signed numeric data literals in the existing typed `.obj.ts` projector, so `thresholdDb: -35.5` works. It still rejects calls/other executable expressions. No new compiler infrastructure/backend was introduced; existing profiles/$schema consumers remain tested.

```powershell
Valencia.Cli analyze waveform|silence|activity|audio project.valencia --asset <guid> --stream <index>
Valencia.Cli analyze scenes project.valencia --asset <guid> --stream <index>
Valencia.Cli analyze audio project.valencia --settings settings.obj.ts
Valencia.Cli roughcut plan project.valencia --analysis <key> --policy policy.obj.ts --output cut.json
Valencia.Cli roughcut apply project.valencia --plan cut.json --save-as edited.valencia
Valencia.Cli script run examples/auto-roughcut.ts --project project.valencia
Valencia.Cli schema export --analysis
```

Typed files use exactly `const $value: AnalysisRequest = {...};` or `const $value: RoughCutPolicy = {...};` against the compiler-owned catalog. Defaults are applied by native typed contracts. CLI results are JSON; progress/diagnostics go to stderr. Ctrl+C cancels. Exit codes remain M2a's. Plan creation does not save; apply saves only with explicit `--save-as`.

The existing pinned MCP 2025-11-25 stdio server gains **start_analysis, analysis_status, analysis_result, cancel_analysis, create_roughcut** (12 tools total). The latter takes project/revision/sequence/artifact key and optional typed policy. Existing validate_edit_plan/apply_edit_plan/export tools perform commitment/output. Schemas for settings/policy derive from the Copeland catalog. Results are paged; no raw PCM/video or unrestricted executable scripts are exposed. Project assets/cache remain under the existing workspace policy. MCP edits remain in memory unless explicitly persisted through another authorized native/CLI/script path.

Native FFmpeg emits Matroska keyframe-index diagnostics during some existing random seeks in the generated PCM/MKV fixture. They remain visible on stderr. Protocol stdout, independently decoded frames/PCM, and output duration/codecs are verified; these diagnostics are not suppressed or counted as codec correctness evidence.

## Native UI

Select an audio clip to choose its exact stream, otherwise Analyze uses the selected asset's first audio stream. It processes audio features and video candidates with progress/cancellation. Up to four waveform artifacts are attached to transient UI state. Only visible clips/bins are GPU quads, with independent channel peaks; scrolling/zoom uses existing levels. Select/analyze another OBS audio clip for its waveform. After reopening a project, Analyze attaches its cached data; automatic bulk cache attachment is not implemented.

Rough cut displays red proposed-removal overlays and a status summary. Apply/Cancel replace the Relink/Run script buttons while a proposal is visible. Apply uses the same atomic plan and refreshes preview/timeline. Escape cancels a job/proposal. The native smoke invokes these same handlers and verifies actual GPU readback; physical mouse/file-dialog interactions were not separately manually repeated for M2b.

## Validation and measured limits

Current-source builds/tests, framework-dependent desktop/CLI publish, real stdio protocol calls and independent FFmpeg decoding are recorded in **docs/validation/m2b.json**. `scripts/AnalysisSmoke.ps1 -Native` regenerates the end-to-end demonstration under artifacts/local/m2b-demo and the native waveform captures under artifacts/local/m2b-native. `scripts/Build.ps1`, M0 player smokes, M1b editor/export verification and affected Copeland tests remain passing.

**175 Valencia tests pass**: 70 Core, 21 persistence, 33 media/UI-model, 51 programming/analysis (the 142-test M2a baseline plus 33 new cases). **291 affected Copeland tests pass**, including the new signed-literal case. No failures/skips were observed; the whole Copeland repository suite was not run.

The demonstration is a six-second 160×90/30 H.264 Matroska recording with numbered frames and binary frame-ID stripes, a visual change at 3 seconds, stereo anti-phase 440 Hz signal/silence [2,4), and an independent mono 880 Hz stream. It analyzes/caches, generates/dry-runs a typed plan, commits, saves/reopens, exports four-second H.264/AAC, and repeats via the actual Copeland CLI script. Independent decoding reads the stripes and verifies output source-frame IDs **30 and 135**, as well as image error, duration/codecs and PCM alignment. MCP's default padded cut separately verifies source-frame ID 129. MCP tests perform actual initialization, discovery, analysis/status/paging, planning, validation/application, export verification, conflicts and cancellation.

Tests cover mono/stereo/silent/active/short sources, original large timestamps, exact offsets, fractional sample windows, adaptive streaming, discontinuities, independent encoded streams, MP4/AAC boundaries, static/fade/hard-cut/VFR scene fixtures, parameter/source/version invalidation, corruption, interrupted/cancelled publication, concurrent publishers, eviction, unsafe multi-track rejection, moved/split in-points, gaps, padding/merging, no-op/full removal, stale revisions/sources, direct/script equality and undo/redo.

Observed on Ryzen 7 7700X / RTX 3070 / Windows / .NET 10.0.15: a 120-second mono PCM fixture analyzed in approximately **0.48 s including CLI startup (~250× realtime)**, with approximately **82 MiB sampled peak process working set**. Analysis itself was about 90 ms and cache publication about 30 ms in that run. The six-second stereo waveform artifact was about **42 KiB**. Native waveform scene preparation/submission measured roughly **8 ms median / 10 ms p95** over 120 frames. These are bounded fixture observations, not throughput guarantees; exact final measurements and cancellation latency are in the machine-readable record. Process peak sampling and end-to-end CLI timings include runtime/setup overhead; they are not isolated DSP benchmarks.

Multi-hour OBS footage, highly fragmented signals, unusual timestamp discontinuities, >2-channel streams, sample-exact coarse-clock random seeks, heavy 4K analysis throughput and physical acoustic/display sync remain unqualified or explicitly unsupported. There is no transcription, semantic highlight ranking, noise processing, loudness normalization, automatic reframing or cloud service in M2b.
