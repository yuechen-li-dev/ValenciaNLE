# Native Int64 and long-recording audio seeking

Current follow-up: [render/playback stabilization](M2E_RENDER_STABILIZATION.md) fixes
preview recovery after UI stalls and qualifies current native paths. The historical
250 ms draw incident remains unconfirmed; the failures below are retained evidence.

## Current outcome

Both engine fixes are implemented and exercised through their real code paths.
Copeland scripts now pass native signed 64-bit values to CLR methods, and Valencia
uses container video cues to avoid linear audio seeks through long Matroska files.
The original string contracts remain compatible.

**Realtime desktop playback is not fully qualified on this run.** Hardware tests
exposed approximately 250 ms GPU draw stalls and excessive A/V skew. The detailed
results below retain those failures. The headless exact-time editing, seeking and
independently decoded exports work. This is meaningful progression, with the next
qualification blocker isolated to native frame presentation/pacing rather than
papered over by a bigger queue or a looser sync threshold.

Machine-readable evidence: [int64-audio-seek.json](validation/int64-audio-seek.json).

## Native compiler path

Copeland's `PrimitiveTypeSymbol.Long`, `long` keyword and `L` literals retain Int64
identity through the lexer, parser, binder, MIR and C# emission. Importing `System`
also permits `Int64` annotations. Signed integer literals are parsed directly as
Int64, including values beyond binary64's exact range and the minimum negative
value. Existing unsuffixed Int32 literal rules remain intact.

Arithmetic is checked. Constant overflow/division by zero produces compiler source
diagnostics; runtime overflow propagates as a managed exception. Explicit CLR
conversion methods provide checked narrowing. Stored Int32/float values do not
silently widen or narrow. Small Int32 literals adapt at a known Int64 destination;
use `L` for overloaded CLR calls. `String.From(long)` formats invariant decimal.

Tests execute native parameters/returns, records, arrays, CLR properties/constants,
closed CLR generic arguments and static evaluation. Public CLR constant fields
are lowered to their actual primitive values. General mutable CLR field binding
is not added. Bitwise operations retain the existing Int32-only contract.

JavaScript rejects the native type. Shader targets are not extended. Existing TSON
numbers remain binary64, so a reachable `long` in runtime TSON encoding is rejected
with a diagnostic and asset decoding cannot silently turn rounded doubles into
Int64. Exact wire contracts continue using canonical decimal strings. This fix
does not claim a numeric redesign of TSON or JSON.

Compiler details and examples are in
`../Copeland/docs/Copeland/language/copeland-ts-native-int64.md`.

## Valencia integration

`Valencia.Scripting/ScriptApi.cs` adds native `AddTicks(long,long)`,
`Frames(long,int,int)`, `ActiveAt(string,long)`, `DurationTicks` and `RevisionValue`.
`PlanBuilder.Split` and `Move` accept native long positions and construct the same
versioned plan DTOs/dispatcher transactions as the string overloads.

```typescript
using Valencia.Scripting;
function run(api: ScriptApi): string {
    const exact: long = api.AddTicks(9007199254740993L, 1L);
    const cut: long = api.Frames(60L, 30, 1);
    const sequence = api.FirstSequence();
    const plan = api.Plan(sequence);
    plan.Split(api.FirstClip(sequence), cut, api.NewId());
    api.Apply(plan);
    return String.From(exact);
}
```

The real script-host regression splits a clip, moves the new segment to exactly
`9007199254740993` sequence ticks, applies, undoes and redoes the transaction, and
checks the exact stored position. An overflowing native call returns a structured
runtime failure without changing that document. The preexisting string/cache test
still runs. Trusted local CLR execution remains the security boundary.

`examples/rough-cut.ts` now uses native long frame conversions. The published CLI
compiled and ran it, edited, saved/reopened and exported a five-second H.264/AAC
MP4. Independent FFmpeg decoding verified the cut, black/silent gap and duration;
the original project hash was unchanged. Raw results:
`artifacts/local/m2a-demo/result.json`.

## Audio seek correction

Previously `FfmpegDecoder(MediaAsset, AssetStream, SourcePosition, int)` called
`av_seek_frame` with the selected audio stream. The long Matroska fixture's cues
index video, so an audio-reference seek repeatedly scanned interleaved packets.
Even already indexed backward seeks took seconds and scaled with source position.

For audio in a source containing video, the decoder now seeks against a video
reference, converts to that reference's actual rational timebase with floor
rounding, and decodes the selected audio forward to its exact sample-count anchor.
Requests before the reference stream's start decode from the beginning. Audio-only
sources retain the existing audio-reference path. No nominal-FPS or rounded audio
PTS arithmetic replaces sample counts, and no PCM/source files are rewritten.

The progressive two-second sample-count anchors, timestamp discontinuity rules,
resampler ownership and bounded cursor policy remain authoritative. Nonselected
stream discard was tried as an optional optimization and removed; it is not part
of the shipped fix. The standalone M0 double-seconds constructor is unchanged.

`SequenceEvaluator.ActiveAudioDecodedFrames` exposes cursor work, explicitly
excluding progressive index construction. A generated two-minute MKV with sparse
video keyframes, two independent noise streams, millisecond clocks and nonzero
start offsets verifies exact forward/backward sample selections against independent
FFmpeg sample-counter trimming. Each query decodes at most 500 cursor frames;
mean absolute PCM error is at most `1e-7`. Existing coarse-clock, discontinuity,
resampling, cancellation, preview and export regressions remain passing.

## Measured improvement

Source: the existing 7.1 GB, 7200.037-second, 1920×1080/60 H.264 Matroska fixture,
with three independent stereo AAC/48 kHz streams and 1/1000 clocks. Its video
repeats controlled OBS footage; audio is continuous/reencoded. It is not two hours
of unique human footage. Machine: Ryzen 7 7700X, RTX 3070, driver 610.88, Windows 11,
.NET SDK 10.0.401/runtime 10.0.15, FFmpeg 7.1.1. Seeking measures CPU PCM evaluation,
not an audio-device latency or acoustic sync measurement.

| Workload | Before | Final source | Interpretation |
|---|---:|---:|---|
| Six paired warm positions, median | 2886.14 ms | 104.56 ms | **27.6× faster** |
| Same warm positions, range | 653.45–4657.94 ms | 92.15–117.62 ms | Three streams mixed per request |
| First request at 690 seconds | 1969.42 ms | 1439.58 ms | Includes initial progressive indexing |
| First request at 5952 seconds | 14821.27 ms | 9660.92 ms | Includes extending the exact index |
| 40 seeded positions, median | 3145.26 ms | 92.17 ms | Historical M2e baseline, identical positions |
| 40 positions, p95 | 9738.26 ms | 1151.11 ms | Cold index extensions included |
| 40 positions, maximum | 13064.00 ms | 8054.23 ms | Cold cost still present |

Fresh processes and identical request ordering were used for the paired 12-query
test: six positions, then the same six again. OS caches were not flushed. Final
measurements ran without other task-owned tests/benchmarks concurrently. Median
averages the middle pair; p95 uses nearest rank. Six warm requests do not qualify
tail latency. The 40-position baseline is historical, not a fresh paired control.
Audio-only process peak working set was 120.25 MB before / 122.49 MB after; the
full video/audio workload peaked at 323.88 MB after. These are process peaks, not
proof of memory usage over an entire two-hour playback.

**Remaining audio limitation:** exact anchors are progressive and process-local.
A first request beyond the indexed high-water mark still counts intervening
decoded samples. This is why first-use/index extension remains seconds long.
Reopening in a new process does not reuse a durable sample index. Analysis/index
preparation can warm the existing process; no new persistent-cache framework was
introduced in this fix.

## Validation and remaining hardware blocker

* Valencia Release solution build: zero warnings/errors. **240 tests passed**:
  Core 73, Persistence 21, media/UI 61, programming 85. No failed/skipped tests.
* Copeland: **1777 compiler/profile tests and 274 C# backend tests passed**, with
  no failed/skipped tests. `Int64InteropTests` contributes 31 real compiler/runtime
  cases, not mock facade calls.
* Native app and CLI were published from current source. The native desktop
  workflow performed editing, scripting, undo/redo, save/reopen and export. Its
  MP4 independently verified: correct source selections, second-source order,
  ten-second duration, black gap and silent gap.
* **M0 hardware smoke failed** its existing 150 ms A/V threshold, including with
  the unchanged direct-file decoder path and an activated foreground window.
  The foreground run peaked at about 432 ms skew. Bounded smoke traces identify
  approximately 245–250 ms `Graphics.Draw()` costs while layout/UI preparation
  normally took under 2 ms. These traces isolate the stage, not the driver/GPU
  root cause.
* The old editor smoke reported `passed` despite approximately 2.84 seconds of
  maximum skew. Its acceptance now rejects skew above the same 150 ms threshold,
  writes evidence first, then exits with failure. The rerun correctly reports
  **failed**, 2.833 s maximum skew and approximately 250 ms synthetic frame times.
  Independently decoded export still passes. Hardware execution is not being
  counted as successful realtime playback qualification.

Renderer/driver pacing diagnosis is the concrete next blocker. No graphics
framework changes, larger playback queues, settings changes or weaker acceptance
thresholds are included to route around it.

## Reproduction

```powershell
dotnet build Valencia.slnx -c Release -m:1 -nr:false -p:UseSharedCompilation=false
# Set VALENCIA_MODEL_DIR to artifacts/local/m2c/models for existing Whisper tests.
dotnet test tests/Valencia.Programming.Tests -c Release --no-build
dotnet test tests/Valencia.Tests -c Release --no-build

dotnet run --project samples/Valencia.Qualification -c Release -- `
  artifacts/local/m2e/long-1080-repeated.mkv artifacts/local/int64-audio/measured-audio --audio-seeks
dotnet run --project samples/Valencia.Qualification -c Release -- `
  artifacts/local/m2e/long-1080-repeated.mkv artifacts/local/int64-audio/measured-full

# From Copeland:
dotnet test tests/Copeland/Copeland.TS.Tests
dotnet test tests/Copeland/Copeland.TS.Backend.CSharp.Tests
```

Current raw TRX, timings, native traces and logs are under
`artifacts/local/int64-audio/`. `scripts/RecordInt64AudioEvidence.ps1` records the
paired results, historical/current 40 queries, test counters, source hashes,
independent exports and explicit native failures in the checked-in evidence file.
