# M2c — Local transcription and transcript editing

Valencia now transcribes selected audio locally, caches source-aligned words, searches them, and produces retain/remove plans through the existing editing engine. CLI, trusted Copeland scripts, MCP and the native transcript panel use that same path. The working inference provider is **Whisper.net/whisper.cpp on CPU**. It does not use Aurelian for inference. The separately qualified GPU subgraphs and remaining research gaps are described in [M2C_GPU_RESEARCH.md](M2C_GPU_RESEARCH.md).

Word times are **experimental model predictions**, not forced alignment. The implementation preserves their provenance and rejects unusable boundaries. The generated English fixture recognized all 19 words, but usable word starts differed from SAPI's reference by 260 ms mean / 680 ms maximum across 13 uniquely matched words. Two words had unusable times. Review proposed cuts before applying them; this is not production-quality phonetic alignment or general transcription-accuracy qualification.

## Run it

Build with `.\scripts\Build.ps1`. The transcription integration tests require the explicitly downloaded tiny.en model; tests never download it. Existing native FFmpeg/FFmpeg CLI requirements remain unchanged. Whisper.net's Windows native runtime also requires the Visual C++ 2022 x64 runtime. No Node.js, browser, Rust or Python runtime is used by the application.

```powershell
# Explicit download: 74 MiB tiny.en; add large-v3 only for the 2.88 GiB stress model.
.\scripts\DownloadWhisperModels.ps1 -Model tiny.en
$env:VALENCIA_MODEL_DIR = "$PWD\artifacts\local\m2c\models"
.\scripts\Build.ps1
.\scripts\TranscriptionSmoke.ps1 -Native

$cli = '.\artifacts\publish\cli-win-x64\Valencia.Cli.exe'
& $cli project create recording.valencia --media C:\Videos\recording.mp4
& $cli project info recording.valencia
& $cli transcribe recording.valencia --asset '<asset-guid>' --stream 1
& $cli transcript search recording.valencia --analysis '<key>' --query 'important announcement'
& $cli transcript show recording.valencia --analysis '<key>'
& $cli textedit plan recording.valencia --analysis '<key>' --query 'important announcement' --mode retain --output cut.json
& $cli textedit apply recording.valencia --plan cut.json --save-as edited.valencia
& $cli export edited.valencia --sequence '<sequence-guid>' --output edited.mp4
& $cli schema export --analysis
```

Progress/diagnostics go to stderr; results are JSON on stdout. Progress counts decoded input samples and can reach 100% while the final native chunk is still processing; job completion is authoritative. Ctrl+C cancels at decode/native-worker/cache boundaries; cancellation returns exit 130. Plan application saves only with explicit `--save-as`. `--model large-v3 --language <code>` selects the second configured model. tiny.en accepts only English. Arbitrary model filenames are not accepted through the engine/MCP.

In the native editor, select media or an audio clip and click **Speech**. **Transcript / Back to media** switches panels. Click a word to seek, Shift-click to select an interval, **Find** searches a phrase, and **Prev/Next** page through words. The current spoken word is marked `*`, selected words `>`, and unusable timing `[?]`. **Retain/Remove** previews red removal ranges; **Apply/Cancel** is explicit. Apply creates one undo entry. The small native Find dialog resets shortcut state and pauses playback. It is not a scripting IDE or rich text editor.

## Ownership and dependencies

| Owner | Implementation |
|---|---|
| Media | `AnalysisStream.Transcription`, selected FFmpeg decoder, SWR mono/16 kHz conversion |
| Analysis | `WhisperTranscriber`, `WhisperModels`, immutable transcript contracts, existing `AnalysisCache` |
| Engine | Existing bounded `AnalysisJobs`, `TranscriptQueries`, `TranscriptEditing`, existing typed edit-plan validator/dispatcher |
| Scripting | Existing Copeland C# backend/Roslyn host, narrow `ScriptApi` methods |
| CLI/MCP | Existing CLI and stdio adapter, extended operations |
| App | Transient transcript/selection/proposal state, Machina controls and existing Aurelian canvas |

Core still has no codec, inference, UI or GPU dependencies. No new production assembly was needed. Production packages added: **Whisper.net 1.9.1** and **Whisper.net.Runtime 1.9.1**. `System.Speech 10.0.0` is test-only. `MathNet.Numerics 5.0.0` is isolated to the GPU research executable. Resolved dependencies and MIT notices are retained in `docs/dependencies.json` and `licenses/`. Models are user-supplied, not bundled. See [Whisper.net](https://github.com/sandrohanea/whisper.net), [whisper.cpp](https://github.com/ggml-org/whisper.cpp) and [OpenAI Whisper](https://github.com/openai/whisper).

## Models and preprocessing

Both GGML mixed-F16/F32 conversions are pinned to `ggerganov/whisper.cpp` revision `5359861c739e955e79d9a303bcbc70fb988958b1`. `WhisperModels.Resolve` verifies complete SHA-256 and byte size before native loading, caching verification by file identity/length/mtime. Replacing a file while preserving all identity metadata is outside the trusted-local-file policy. The explicit downloader verifies temporary files before moving them into the ignored artifacts directory. Nothing downloads during import, UI startup, tests or MCP requests.

| Model | Bytes | SHA-256 |
|---|---:|---|
| tiny.en | 77,704,715 | `921E4CF8686FDD993DCD081A5DA5B6C365BFDE1162E72B08D75AC75289920B1F` |
| large-v3 | 3,095,033,483 | `64D182B440B98D5203C4F9BD541544D84C605196C4F7B845DFA11FB23594D1E2` |

tiny.en is the small English product default. large-v3 provides multilingual architecture/weight stress research; its full-file size alone does not prove that it must stream on an 8 GiB GPU. Original Whisper/compatible distilled and quantized alternatives were considered; this increment selects original F16 conversions for a common auditable loader and controlled weight values. Distilled architectures and quantized kernels would change more than residency and are not qualified here. Both original Whisper code/model weights and whisper.cpp are MIT; conversions retain the upstream provenance. No claim is made that every model hosted under a similar name has the same license or ABI.

FFmpeg decodes the chosen stream, SWR downmixes/resamples to mono float 16 kHz, with no peak normalization. Stereo anti-phase cancellation is expected from downmixing. The native reference backend owns Whisper's mel/STFT preprocessing; product code does not add a second FFT pipeline. The provider uses four CPU threads by default, English, no carried text context, temperature zero/no fallback temperature increments, native token timestamps and word splitting. `DecoderProbability` is a decoder metric, **not calibrated word confidence**.

## Exact clocks and bounded long-form behavior

Original stream PTS/timebase anchors each PCM epoch. Exact sample offsets at 16 kHz produce `SourcePosition` ranges; native `TimeSpan` timestamp ticks are also retained unchanged. Starts map by floor and ends by ceiling to the 16 kHz sample grid. These rational source coordinates are distinct from normalized source time and sequence ticks. Conversions use existing checked exact-time helpers; no nominal FPS substitutes for source PTS.

Default inference windows are 30 s with 2 s overlap (configurable 5–30 s chunks; 1–5 s overlap smaller than half a chunk). One bounded PCM window is held, with worker-confined decode buffers. Overlap ownership uses word midpoint at the center of the overlap; an identical overlapping adjacent word is suppressed. Exact-EOF tails are retained. This is a deterministic stitching policy, not a guarantee against model omissions/repetitions at seams. No-context decoding can change wording between chunks.

Unexpected timestamp jumps or sample-rate changes drain/reset the resampler and start a new epoch. Words/segments never bridge those epochs. Text editing currently rejects words from discontinuous epochs rather than inventing a continuous mapping. Native zero-duration or out-of-chunk predictions remain searchable text with `TimingUsable=false`; they cannot anchor edits. A multi-word native segment without native word boundaries fails explicitly rather than fabricating evenly divided times.

Only complete full artifacts are published. Completed chunks are independently reusable after cancellation/restart. Native cancellation joins `WhisperProcessor.DisposeAsync` before releasing its factory/context, preventing a use-after-disposal race. The provider caps a transcript at 100,000 words; the existing 64 MiB artifact / 512 MiB disk-cache budgets also apply. Very long/high-density recordings can hit an explicit budget error; this is not an unbounded transcript database.

## Artifacts, queries and plans

`AnalysisArtifact.Transcript` adds versioned model/revision/hash/precision, language, preprocessing/alignment descriptions, immutable segments and flat word records. Segments reference word indices to avoid duplicating bulky word data. Source-aligned word and segment IDs derive deterministically from artifact/chunk identities. Model path is provenance, not an MCP-supplied path. Deliverance project snapshots contain none of this derived data.

Keys include source quick fingerprint, asset/stream, model/settings and pinned revision, algorithm/alignment version, and decoder policy. Chunk keys additionally include epoch, rational origin, first sample and sample count. The current preprocessing version is tied to the provider algorithm version; changes require a version bump. Existing cache checksum validation, atomic publication, cancellation-safe temporary files, writer lease and LRU eviction are reused. The quick source fingerprint samples start/middle/end plus size/mtime; it is not a cryptographically conclusive whole-file content identity. Splitting or moving a clip does not invalidate transcription. Relinking invalidates stale-source access and clears attached UI transcript state.

`TranscriptQueries.Search` performs normalized exact-token phrase search or exact-word matching, with bounded context/page sizes. `Range` uses half-open source intervals; `ForClip` restricts queries to a clip's source coverage. Pagination caps results at 128 records, context at 20 words and query length at 1024 characters. CLI show/search accept `--offset`/`--limit`; show additionally accepts `--kind words|segments`. Queries expose immutable records, never mutable document collections. CLI `textedit plan --query` and the Copeland `TextEditPlan` convenience method require one unique phrase match in the source; repeated phrases require explicit word IDs through `TextEditRequest`. They never silently plan from a truncated page of search results. Timeline occurrence ambiguity is checked independently.

`TextEditRequest` selects word IDs, sequence, transcript key, retain/remove mode and optional occurrence IDs. `TextEditAsync` returns candidate occurrences, exact removed ranges, a typed plan and ordinary engine dry-run diff. It never changes the session. Word ranges map through the existing clip source-to-timeline mapping. Retain rounds outward to sequence frame boundaries; remove rounds inward, preserving selected speech rather than overcutting. Boundaries clamp to existing clip coverage. Adjacent/overlapping mapped ranges merge; retain removes the complement inside the chosen occurrence's existing guide clips, preserving preexisting gaps outside that coverage.

Aligned tracks are grouped by the exact source/timeline intercept. Different legal A/V end bounds and already split/trimmed clips are supported. Repeated source coverage at different intercepts returns ambiguity and no plan until occurrence IDs are supplied; CLI supports `--occurrence`. Unrelated sources, locked tracks and unsafe conflicting multi-track mappings reject the proposal. Source ranges stay source-aligned after moves/splits. The planner lowers descending removals into **existing** ripple operations with deterministic created IDs and source fingerprint preconditions. Dry-run/commit use the same revision-checked transaction, one undo entry; stale plans fail without changes.

## Programmability

`examples/text-edit.ts` is an executed Copeland example: transcribe → search → construct plan → validate/apply → undo/redo → save → reopen → export. It writes new files. It uses the actual C# backend and Roslyn, with exact Int64 strings at the ABI. `StartAnalysis` accepts typed settings, `AnalysisStatus/CancelAnalysis/WaitAnalysis/AnalysisResult` reuse jobs, and `TranscriptSearch`, `TranscriptRange`, `TranscriptForClip`, `TextEdit` and `TextEditPlan` supply bounded queries/planning. Normalized range endpoints use canonical sequence-tick strings relative to the artifact's container start. Typed request/results originate in `Contracts/analysis.obj.ts` via the existing `type` profile; there is no separate JSON Schema authoring engine.

MCP retains pinned protocol `2025-11-25`, stdio, workspace path policy and explicit project/revision identity. There are now **14 tools**: existing `start_analysis` accepts `kind:"transcript"`, status/cancel/result tools are reused, and `transcript_query` plus `create_textedit` extend the surface. Existing validate/apply/export tools consume the proposed plan. Search/source-range queries page results; no raw PCM, model path or arbitrary script execution is exposed. Transcription runs off UI/render/audio threads. Trusted local Copeland code retains ordinary CLR privileges; this is not an untrusted-script sandbox.

## Validation and limits

Current-source build, framework-dependent desktop/CLI publish, **187 Valencia tests** (70 Core, 21 persistence, 33 media/UI-model, 63 programming), **329 affected Copeland compiler tests**, **19 inference and 34 graphics infrastructure tests** pass, with no failures/skips. The full Copeland repository suite was not run. Twelve new transcription cases exercise the real native model, schema validation, cache hit/resume, source offsets, resampling/downmixing, silence, split/trim mappings, ambiguity, rejected/atomic plans, undo/reopen, real Copeland compilation, actual stdio MCP initialization/discovery/analysis/search/commit/conflict/export, and cancellation during native processing. Generic M2b cache corruption/eviction/fingerprint tests remain passing.

`TranscriptionSmoke.ps1 -Native` creates synthetic H.264 video with independently decodable binary frame IDs and spoken audio. It imports, transcribes/caches, searches, dry-runs, commits, saves/reopens, exports and repeats through Copeland. The independently decoded 1.166667 s H.264/AAC output retains source frame IDs **72, 81 and 93** at 0.1, 0.4 and 0.8 s. PCM comparison to the selected source window measured **0 samples offset**, MAE **0.001734** (AAC is lossy). This verifies media selections/timing, not the linguistic accuracy of model word boundaries. Source media and original project hashes remain unchanged.

On Ryzen 7 7700X / RTX 3070 / driver 610.88 / Windows / .NET 10.0.15, the 9.47 s tiny.en job took approximately **597 ms analysis**, **903 ms including CLI startup**, and **293 MiB sampled peak working set**. A cache hit took **350 ms including CLI startup**, not repeated warm inference. The same audio through CPU large-v3 took **19.63 s analysis**. A 120 s continuous repeated-speech fixture took **3.39 s**, five chunks, returning 239 words / 13 occurrences of “announcement.” A two-hour sparse-speech FLAC took **2.67 s**, 258 chunks and 38 words; most windows were silence-gated, so this is not two hours of continuous inference. A cancellation requested after 150 ms returned in 413 ms total. These are fixture measurements, not throughput guarantees.

The native GPU smoke searched/navigated, captured transcript/proposal/applied views, verified nonmutating Cancel and one-transaction undo/redo. Final native seek samples were 69/50/29 ms. M0 MP4/MKV playback, M1b timeline/export, M2a programming and M2b waveform/rough-cut native smokes passed again. Physical mouse typing/dialog coverage and acoustic/display sync were not separately manually measured. FFmpeg's coarse container-clock random seek limitation remains: Matroska 1 ms source clocks do not become sample-exact through word timing. The MP4 verification above observed zero offset in its tested window; unusual container discontinuities and long-form post-cut acoustic sync remain unqualified.

Machine-readable evidence is in [validation/m2c.json](validation/m2c.json), schema extraction in `validation/m2c-analysis-schema.json`, and native captures/raw logs under ignored `artifacts/local/m2c*`. Re-run commands are the scripts above; no user footage is modified. Captioning, translation UI, diarization, semantic highlights, forced alignment, untrusted execution and full Aurelian Whisper inference remain outside the delivered product path.
