# M2d — Creator editing backend

Valencia can produce a captioned long-form cut and distinct vertical Shorts from source-aligned analysis through the existing command engine. The implemented path is native C#/.NET 10, FFmpeg software decode/processing/encoding, CPU Whisper DTW refinement, and the existing Machina/Aurelian/NAudio desktop. No GPU inference work was resumed. This is a working creator PoC on generated media, **not full original-spec completion or real-world editorial-quality qualification**. See [the coverage matrix](M2D_POC_COVERAGE.md).

## Run and review

```powershell
.\scripts\Build.ps1
$env:VALENCIA_MODEL_DIR = "$PWD\artifacts\local\m2c\models"
$cli = '.\artifacts\publish\cli-win-x64\Valencia.Cli.exe'
& $cli project create recording.valencia --media C:\Videos\recording.mkv
& $cli project info recording.valencia
& $cli creator run recording.valencia --asset '<asset-guid>' --stream 1 `
    --keywords 'important announcement;release' --target-seconds 600 --short-seconds 20 `
    --output-dir outputs
```

The default is review-only: facts, candidate evidence, profiles, a semantic diff and `creator-plan.json`; no editing or save. Explicit `--commit --verify-history` applies one atomic plan, checks undo/redo, saves a new `outputs/creator.valencia`, reopens and exports all profiles. Original input project and media remain unchanged. Add `--save-as` for a different project destination. Never point an output at source media; the existing engine rejects that. Ctrl+C cancels work; completed prior outputs remain completed, while an unfinished export is never published under its final filename.

The creator preset is 1280×720 long-form and 720×1280 Shorts, H.264 CRF18/AAC192k; `--width/--height` can bound a fixture. A Short is a distinct candidate window, at most the requested length; it is shorter when its retained candidate is shorter. A ten-minute target is best effort, with diagnostics if suitable nonoverlapping material is insufficient. Silence-only input fails rather than inventing highlights. Targets are creator settings, not universal platform standards.

For explicit settings, use `creator plan <project> --request creator.obj.ts --output cut.json`, then `creator apply ... --plan cut.json --save-as edited.valencia` and `creator export edited.valencia --output-dir outputs`. `creator.obj.ts` contains `const $value: CreatorRequest = { ... };` against the compiler-owned contract. `CreatorRequest` includes exact tick-string durations, markers/keywords, noise strength, music/duck gain, target LUFS/peak ceiling and duck attack/release/hold. Selected-stream `AudioSettings.activeDb` controls the activity facts supplied to planning. Manual overrides use ordinary `creator`/`properties` plan operations, never renderer state.

Other entry points: `index build`, `proxy create [--attach --save-as ...]`, `loudness analyze` (source or sequence), `transcribe --alignment dtw`, `captions generate`, `outputs list`, `transcript correct --edits corrections.obj.ts`, and `textedit plan --padding-ms 200`. CLI help lists argument shapes. Native **View** cycles profile aspect/framing; the monitor remains a sequence viewer rather than constraining playback to a Short's export window. Native export toolbar retains its original sequence export; multi-profile export is currently headless. Existing Run script imports changes into the same UI session.

## Ownership and structure

| Owner | New or extended responsibility |
|---|---|
| `Valencia.Core/Timeline/CreatorSettings.cs` | Immutable version-1 effects, caption words/style, output profiles, linear framing/gain keys; optional fields on existing documents/clips |
| `CaptionIndex.cs`, `CaptionEvaluation.cs` | Logical source-aligned caption placement, half-open word selection, dB envelope/fade evaluation |
| Existing editing reducer/session | `SetCreatorSettings`, `SetClipProperties`, validation, revisions, one-transaction undo/redo |
| `Valencia.Media/AudioSeekIndex.cs`, `MediaIndex.cs` | Progressive actual-sample anchors and existing FFmpeg video cue/decode-forward indexing |
| `SequenceEvaluator`, `CreatorRenderer`, `AudioFilter` | Shared preview/export source resolution, basic SDR composition, Skia captions, gains, fades and native cleanup |
| `MediaProxy.cs`, `MediaProcess.cs` | Derived proxy storage and bounded worker-process diagnostics/cancellation |
| `Valencia.Analysis/LoudnessAnalyzer.cs`, `WhisperTranscriber.cs` | Nonmutating native loudness facts and optional model-compatible DTW/PCM timing refinement |
| `Valencia.Engine/CreatorPlanning.cs`, `CreatorEngine.cs`, `CreatorJobs.cs` | Explainable candidates → immutable proposed plan → existing dry-run/commit; bounded normalization/planning worker |
| Existing CLI, ScriptApi and MCP | Thin access paths over those same services; no independent editing rules |

No new product assembly, language, VM, agent framework, database or Rust dependency. New packages are **SkiaSharp 3.119.1** and **SkiaSharp.NativeAssets.Win32 3.119.1**. Licenses/native notices are retained. FFmpeg 7 `avfilter-10.dll`, `ffmpeg.exe` with afftdn/loudnorm/libx264/AAC, and installed Arial/Consolas are required for the corresponding paths. Missing font/glyph/native filters fail explicitly. No model or codec download occurs implicitly.

Deliverance remains canonical. Existing schema-1 files acquire optional `Creator`, clip effects and `ProxyKey`; nested creator data has version 1 and full validation. New readers accept older files. Older strict readers may reject the additive fields; backward readability by old binaries is **not** claimed. Versioned project snapshots contain semantic settings/words, not decoded frames, waveforms or proxy files. Corrupted-load and atomic-save behavior stays on the existing persistence path.

## Exact seeking, indexing and proxies

Source PTS/timebases are retained, with video DTS/duration available in frame diagnostics. Video seeks use the demuxer's preceding keyframe/cues and decode forward, selecting the greatest actual PTS not later than the requested source position. No nominal source FPS is substituted. Before a first frame, or past a known last-frame interval, evaluation returns black. Backward requests reopen cursors. UI cancellation/generation rejection remains unchanged.

Coarse Matroska clocks cannot identify PCM sample phase by packet PTS alone. `AudioSeekIndex` measures cumulative decoded sample counts from a known origin and stores anchors about every two seconds, with at most 12,000 anchors/source and eight source/stream owners. Seeking resumes through an exact native anchor with bounded preroll, then counts/resamples samples. Native decoder handles close after each indexing operation. Index lookup/build is serialized on media workers; a first late cold seek can require progressive scanning and is cancellable during decode. It is not an instant arbitrary-position index. Persisted index artifacts describe coverage; actual audio anchors are process-local and rebuilt after restart.

Timestamp delta above two native ticks starts a forward audio epoch preserving sub-tick sample phase; missing time is silence. Backward audio discontinuities are rejected as ambiguous. Noise cleanup over a discontinuity explicitly requests separate epochs/disabled cleanup rather than silently shifting audio. Unusual resets, damaged demux indexes and nonmonotonic captures remain qualification limits.

Optional proxies: all-intra H.264 CRF20, max640 width, original absolute PTS and VFR cadence, reciprocal-integer source timebase only. Preview may resolve the proxy; audio/final export use originals. Missing/stale/corrupt proxies fall back to originals. `ProxyKey` is only a regenerable hint. The quick fingerprint samples beginning/middle/end plus size/mtime: not cryptographic proof that unsampled bytes are unchanged. Proxy files/manifests publish atomically; completed cache entries are evicted by manifest access time toward a four-GiB budget. Generation has a four-GiB individual output cap; temporary generation can add up to four GiB before completed-entry eviction. External writers/locked files can make cleanup fail explicitly. High-resolution long-source throughput and strict cross-process quota enforcement remain unqualified. `VALENCIA_PROXY_DIR` selects the local cache; no automatic full-source transcode blocks opening.

## Alignment, text and captions

`TranscriptionSettings.Alignment="dtw"` selects whisper.cpp's model-specific attention-head DTW through Whisper.net 1.9.1, disables incompatible flash attention, and uses a separate versioned artifact key. Token-path boundaries delimit successive word spans; measured 10 ms PCM energy windows trim silence at their edges, with conservative minimum silence lengths. No uniform word distribution is fabricated. Original word text/ranges/native timings and DTW boundaries remain inspectable. The default `native` mode preserves M2c compatibility. Corrections create a new cached artifact with `OriginalText`; no media/project timing mutation.

The SAPI fixture provides independently generated word-start annotations, not a human speech corpus. Mean/max errors and compared-word counts are recorded in machine evidence. Refined timing improves the fixture but is **not phonetic ground truth**, calibrated confidence, diarization or overlapping-speaker qualification. Unusable and discontinuous word times cannot anchor precise edits. Chunk omissions/duplications and dense overlapping speech still require review.

Text retain padding expands source context before outward sequence-frame snapping and clamps to each occurrence. Remove retains inward snapping and rejects nonzero padding; callers must explicitly choose a larger selection to remove more context. Repeated source placements still require explicit occurrence selection. Existing source-fingerprint/revision preconditions guard commits.

Captions are source-aligned semantic words grouped into at most six visible words per clip. An immutable placement index is compiled once; exact `[start,end)` queries select the highlighted word or none during an internal pause. Split/move/trim changes placement, not source timing. Preview/export use the same evaluator. Basic outlined text/translucent background and yellow highlight render through pinned Skia CPU RGBA, then existing Aurelian presentation or FFmpeg encoding. Horizontal safe width is 88%; groups shrink to fit. Profiles override vertical placement. This is a basic Latin style, without complex-script shaping, karaoke motion curves or a full caption editor.

## Audio, effects and output timing

Loudness uses FFmpeg `loudnorm` integrated gating/oversampled true-peak estimation (BS.1770/EBU R128), not peak normalization. Sequence normalization streams the **proposed edited mix** through the same audio evaluator, then materializes a static master gain. Default target −16 LUFS, −1 dBTP; configurable bounded creator targets. Gain is constrained by measured true-peak headroom, and an unreachable target is reported. No compressor is hidden in this policy. The evaluator also applies a hard sample ceiling; lossy AAC/reconstruction can overshoot sample limits, so exported true peak is measured separately. Momentary metering is deferred.

Noise suppression is native `afftdn`, static floor −45 dB, configurable 0–40 dB reduction; it is not a neural speech separator. An impulse test exposed its two-hop signal latency. The adapter subtracts `2*floor(sampleRate/80)` samples from output positions and feeds bounded silent tail input before EOF. Tests confirm impulse position at 16/44.1/48 kHz. Decoder preroll warms the filter; seeking with different warmup histories can yield small DSP differences and is not advertised as bit-identical full-history noise estimation.

Clip/track gain, clip-linear fades, master gain and explicit dB gain envelopes are applied in bounded stereo blocks. Creator audio cuts have 5 ms fades. Music ducking is precomputed from selected microphone activity: default 80 ms attack, 200 ms hold, 300 ms release and −12 dB attenuation, with −12 dB base music gain. Selected audio streams stay independent. Manual envelope replacement is authoritative. Crossfades are expressible by overlapping clips on separate tracks plus fade envelopes; no general transition/linked-track framework was introduced.

Effects include crop, position/scale/opacity, linear fades, exposure/contrast/saturation and simple warm/cool RGB temperature. The shared reference compositor is encoded SDR channel math and nearest-neighbor scaling, **not HDR/ACES/calibrated white balance**. Higher video track order composites on top. Advanced effect fidelity/performance is not qualified merely because parameters exist.

Each output profile has resolution/rate, `[Start,End)` sequence window, fit/fill/center focal settings, caption position and optional linear framing keys in **sequence time**. One underlying edit document drives all outputs. Offline timestamps use exact frame ticks; duration rounds up to the next output frame and audio sample. The last video frame can extend through that output-frame interval; audio beyond the selected window is silence, never audio from the next unselected clip. Preview dropping/device clocks do not drive export. At most four export requests are queued, one encoder runs, and cancellation aborts the batch's remaining jobs. Each file uses the existing temporary/mux-finalize/atomic-publication path.

## Planning, scripting and protocol

Candidate discovery uses explicit markers, normalized phrase matches or energy activity. Scores report their evidence (timed-word count, activity, visual-change candidates). They are **not semantic importance judgments**. Bounded candidates are ranked, overlap-filtered, frame-snapped and assembled in source order; three different retained candidates provide Shorts windows. Requested duration is best effort. One source analysis set supplies every output. Complex/moved/repeated/misaligned multi-source timelines are rejected by source-mode planning rather than silently desynchronized.

Limits include 1 MiB typed request/plan source, 500-operation transactions (creator leaves budget below that), 512 candidates, bounded seed/query results, 100,000 caption words, 20,000 gain keys/track, eight simultaneous media cursors/kind, four retained creator plans and one active creator worker. Fixed input facts/revision/settings yield the same semantic plan and stable IDs. Dry-run simulates the existing reducer and consumes no undo history. Apply is revision/fingerprint checked and one atomic undo entry.

`examples/creator-cut.ts` compiles through Copeland C# and Roslyn. It reads a compiler-validated `.creator.obj.ts` request, starts/waits planning, inspects the proposal, commits, undoes/redoes, saves/reopens and exports profiles. `ScriptApi` also exposes source analysis, aligned transcription, corrections, captions, jobs/status/cancellation and ordinary plan operations. Exact Int64 values remain canonical strings; no double roundtrip. Trusted scripts have ordinary CLR privileges; no sandbox claim.

MCP remains optional local stdio protocol `2025-11-25`, now **20 tools**. New `start_creator`, `creator_status`, `cancel_creator`, `apply_creator`, `caption_plan`, `correct_transcript` reuse engine services; existing `start_analysis` accepts index/proxy/loudness/transcript and existing `run_export` accepts `profileId`. Creator queries return compact proposals/references or paged words. `project_info compact:true` returns bounded identity/stream/sequence metadata; legacy full project/timeline queries reject responses above 128 KiB with an explicit narrower-query diagnostic. Full snapshots remain available to trusted engine/CLI clients. Request schemas derive from the same Copeland `type` contract catalog. Actual initialization/discovery/start/status/commit/stale/export protocol tests run a child server. No unrestricted script-execution tool exists. Workspace path policy remains in force.

The current schema extractor supports type aliases, not enum/interface/record declarations in this profile. Contracts use supported named aliases and discriminated unions; the user's permission to prefer other declarations does not imply compiler support. No speculative compiler extension was added for syntax alone. FilmCraft references and adopted concepts are recorded in [filmcraft-reference.md](filmcraft-reference.md).

## Reproduce and interpret evidence

```powershell
.\scripts\Build.ps1
.\scripts\CreatorSmoke.ps1              # Generates an explicitly synthetic 3-hour fixture
.\scripts\CreatorSmoke.ps1 -ReuseFixture # Warm/cache run, preserves input project/media
.\scripts\VerifyCreator.ps1             # Independent source actual-PTS/frame-ID/cut checks
.\scripts\VerifyCreatorAudio.ps1        # Independent original-stream PCM/DSP/mix/AAC checks
.\artifacts\publish\win-x64\Valencia.App.exe --creator-smoke `
    artifacts/local/m2d/outputs/creator.valencia --output artifacts/local/m2d-native
```

The fixture is **160×90/30 H.264, 300-frame GOP, B-frames, two mono 48 kHz AAC streams**, generated SAPI speech for one minute every ten minutes plus silence, and continuous 330 Hz game/music. Visible decimal frame numbers and a binary frame-ID stripe permit independent verification. Long-form output is bounded to 320×180; Shorts are 180×320. The Shorts use different source windows, but repeated fixture speech does not establish topic diversity. This deliberately qualifies timing/streaming/automation, not production-resolution throughput, real OBS quality or creator judgment. Proxy generation is optional and unnecessary for this small source.

The earlier cold pipeline took 249.32 s, sampled peak CLI working set 424,833,024 bytes. Its time-zero inspection is not qualified as a decoded first video frame. After the denoiser timing fix, a cache-assisted complete rebuild/save/reopen/four-export run took **76.87 s**, sampled peak **211,382,272 bytes**, and a checked first decoded video/PCM inspection took **99.53 ms**. These process measurements exclude separate FFmpeg child working sets and do not measure sustained VRAM. Machine: Ryzen 7 7700X / RTX 3070 8 GiB driver 610.88 / Windows / .NET 10.0.15. Cold outputs were superseded by corrected outputs; only final independent verification qualifies final media.

Current-source validation: **210 Valencia cases** (73 Core, 21 persistence, 47 media/UI-model, 69 programming), **329 affected Copeland cases**, **351 Aurelian graphics and 19 inference regression cases**, all passed with zero failures/skips. These infrastructure suites are regression tests, not a claim of complete GPU Whisper. Desktop and CLI publish succeeded. M0 MP4/MKV playback, M1b editing/export, M2a programming, M2b analysis/rough cut and M2c transcript native smokes passed again.

Independent final export verification covers **61 video positions**, including before/at/after long-form cuts and all profile windows, against original actual source PTS/frame identities. Seven independently decoded 200 ms audio windows across long-form and all three Shorts show **zero sample offset**, correlation at least **0.99979**, and PCM MAE below **0.00157** against independently decoded/processed original streams. Actual encoded long-form loudness is **−18.47 LUFS / −0.85 dBTP**: static gain was peak-constrained and AAC reconstruction overshot the nominal peak target by 0.15 dB. The diagnostic filter's `output_i` is not the measured Valencia file.

The real three-hour Copeland example completed in 102.89 s including planning/normalization/save/reopen/four exports. Three output containers are byte-identical to CLI; the fourth has identical decoded audio and independent SSIM reported as 1.000000 (67.61 dB), with eight tiny decoded-frame differences. Semantic plan reproducibility is tested; bit-identical arbitrary encoded containers are not promised.

DTW refinement measured **77.3 ms mean / 245 ms maximum** start error on **15 uniquely matched words**; all 19 fixture words have usable times. This does not establish general alignment accuracy. The small VFR/offset/B-frame random-seek test measured 24 requests, roughly **0.10–10.91 ms** decode resolution, excluding external verification process startup. Native edited preview seeks measured **181–308 ms**, with **5.02 s actual playback**, **zero underrun frames** and maximum observed presentation skew **33.54 ms** across a cut. This is device/video timing, not acoustic/display measurement or hours of elapsed playback. The direct three-hour source smoke also exercised the near-end seek, with zero final-generation underruns. Increasing only edited audio's bounded queue to 64 blocks (about 1 MiB/2.7 s at 48 kHz) removed an initially observed underrun; the original direct-player queue is unchanged.

An optional full-source proxy qualification generated a **661,562,439-byte** all-intra proxy in **22.81 s** for this tiny three-hour source. Attached-proxy native preview passed with zero underruns and about 33.42 ms maximum presentation skew. This result is not a high-resolution throughput prediction. Native FFmpeg logged Matroska keyframe warnings during some source seeks; logs retain them, while tested actual-PTS/source-frame comparisons passed. Unusual or damaged cue recovery remains unqualified.

Machine-readable current test totals, individual cases, alignment/seek measurements, artifact hashes, native smokes, independent checks and output characteristics live in [validation/m2d.json](validation/m2d.json) and [validation/m2d-tests.json](validation/m2d-tests.json). Compiler-extracted catalogs are in `validation/m2d-analysis-schema.json` and `validation/m2d-edit-schema.json`. `scripts/RecordCreatorEvidence.ps1` captures the current source manifest, test/artifact hashes and measurements. Detailed local artifacts remain under ignored `artifacts/local/m2d*`; large recordings/models/exports are not committed. Build and publish failures during development were fixed and affected checks rerun; historical binaries are not substituted for final-source verification.

Remaining gates: actual long OBS footage at creator resolutions; extended acoustic/display sync; general natural/overlapping speech alignment; high-resolution caption readability; full effect/reference suites; strict multiprocess proxy quota; real editorial usefulness; full original codec/interchange/advanced-trim matrix. GPU inference/residency research remains separate and incomplete.
