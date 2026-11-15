# M2e real-world qualification

## Scope and machine

Current source was built/published and exercised on Windows 11 (10.0.26300), Ryzen 7 7700X (8 cores/16 threads), RTX 3070 8 GiB, driver 610.88, .NET SDK 10.0.401/runtime 10.0.15. Playback uses FFmpeg software decoding, CPU RGBA upload, Aurelian Vulkan and NAudio 3 WaveOut/device timing. Export uses the shared CPU evaluator/Skia and user-installed FFmpeg 7.1.1 libx264/AAC. OBS capture uses installed OBS Studio 32.1.2/NVENC, WebSocket 5.7.3/RPC 1. NVENC availability in OBS is not a claim of hardware decoding/encoding inside Valencia.

This is a controlled OBS/media qualification, not broad creator-footage or editorial certification. No substantial private multi-hour recording was available. Existing small user recordings were not changed. The isolated portable OBS fixture lives under ignored `artifacts/local/m2e/obs-portable`, with its own scene/profile/credentials and synthetic inputs; user OBS profiles remain untouched. No GPU inference or memory-residency research was resumed.

## Sources

| Fixture | Characteristics | Provenance / limits |
|---|---|---|
| Actual OBS 1080p60 MKV | 17.984 s, 1920x1080 H.264 NVENC, three independent AAC stereo/48 kHz tracks, long GOP/B-frames, video PTS zero/audio PTS -21 ms, 1/1000 stream clocks | Generated numbered moving pattern, SAPI microphone speech, game tone and communications pulses; includes explicit pause/resume and marker |
| Actual OBS 1440p60 MKV | Approximately 14 s, 2560x1440, H.264 NVENC, same independent audio routing | Real 1440 output/encoding, with the original 1080 pattern upscaled by OBS; not native-resolution game footage |
| Two-hour high-resolution stress | 7200.037 s, 1920x1080 H.264, three continuous AAC tracks, 7,109,246,195 bytes | Repeated OBS video packets with continuous reencoded audio; not two hours of human recording or continuous two-hour playback |
| Preserved M2d creator fixture | Three hours, 320x180, speech/game streams and known source identities | Synthetic low-resolution baseline; its performance is not extrapolated to 1080p/1440p |
| Automated fixtures | MP4/MKV, fractional FPS, VFR, long GOP/B-frames, offsets, discontinuities, known sample phases | Generated temporary test media; used for exact correctness and failures, not editorial realism |

The OBS fixture script can be rerun with `-Width 1920 -Height 1080` or `-Width 2560 -Height 1440`. It refuses to overwrite an active OBS process. Both output configurations, audio routing and completion events were actually exercised. Frame-number text is from the looping source pattern and is not the recording's frame ordinal; independent verification resolves recording frames using their actual PTS.

## Measured results

The exact distributions, raw paths, resource samples and source identities are in [m2e.json](validation/m2e.json). Measurements came from a normal desktop, not an isolated benchmark machine. Reported CPU/RAM exclude export child-process usage where stated. OS caches were not flushed. There are 40 seeded headless seeks per source: median/p95 are useful descriptions, while p99 is unqualified.

* Current headless import/first software frame: 1080 about **65/63 ms**, 1440 about **80/86 ms**. These are decoder measurements, not visible-window startup latency.
* A successful native program-monitor capture provides a startup-inclusive **upper bound** for visible video, approximately **4.9 s at 1080 /4.3 s at 1440** in these runs. It includes font/device initialization and qualification setup and is not a first-photon measurement.
* Latest native runs played **ten actual seconds each**, around **59 fps at 1080p60 and 60 fps at 1440p60**, with **zero reported audio underrun frames** in the measured playback generation. Maximum sampled device-clock/video presentation skew was approximately **20 ms**. This is not acoustic/display latency or proof of hour-scale drift.
* WDDM per-process GPU observations were roughly **67 MiB dedicated/46 MiB shared** for 1080, **73 MiB dedicated/60 MiB shared** for 1440. Full native analysis/transcription/export workflow RAM was around **1.1–1.4 GiB**; see current raw values rather than treating sampled peaks as exhaustive GPU allocation tracing.
* Native resize/culling ran through desktop 1280x720, wide 1920x1080, tablet 768x1024, phone 390x844 and desktop restoration. A 5000-clip sequence rendered seven visible clips. Its measured 120-frame median/p95 were roughly **5.4/6.5 ms**; the first validated input proposal took about **18 ms**. This is a bounded synthetic canvas test, not a general UI performance guarantee.
* On the identical 1080 fixture, 30 identity-profile frames averaged **43.879 ms** before the compositor fix and **4.023 ms** afterward; direct decode/evaluation was **3.330 ms**. Nondefault scalar color adjustment remains unqualified for realtime full-resolution work.
* The initial OBS creator invocation took **13.735 s**, including source inspection/indices/analysis/transcription/planning/normalization/save/reopen and export. It retained **7.15 s** and generated **two** distinct three-second vertical Shorts; a requested 12-second target/three Shorts was best effort and emitted explicit diagnostics. Export-only wall time was not isolated.
* Actual Copeland analysis against that recording read the cached artifact twice; the recorded reads were approximately **33/4 ms** in the first measured script, with later warm reads retained in machine evidence. Script compilation is reported separately. Native playback ran alongside a background proxy job, which completed; cache reuse and underlying existing-proxy reuse can differ, so `CacheHit:false` alone is not proof of cold encoding throughput.
* A separate fresh-identity/proxy-directory resource run on the 17.984-second 1080 source measured **212.53 ms audio analysis**, **941.37 ms CPU DTW transcription** (36 returned words), **859.13 ms proxy generation** (**9,157,726 bytes**) and **7.86 ms cached audio retrieval**. The audio artifact was **192,946 bytes**, including waveform/silence/activity data; this is not a waveform-only size. These are artifact-cache misses followed by a hit, with existing model weights and unflushed OS caches. They do not establish human-speech accuracy or multi-hour throughput.

### Long-seek defect and remaining limit

The first two-hour run hit its **ten-minute cancellation budget** in `SequenceEvaluator.VideoCursor.ReadAt`: a large forward query decoded the entire interval from the preceding request. The log is retained as `artifacts/local/m2e/qualification-long-before-seek-fix.log`. Forward/backward discontinuous queries now seek instead. The regression uses a real ten-minute file, checks exact source PTS/independent pixels, and limits decoded work to preceding GOPs.

After the fix all 40 two-hour video/audio requests completed. Video median/p95 were **361/614 ms**, maximum **688 ms**. Three-track exact audio remained **3.15/9.74 s median/p95**, **13.06 s maximum**. The measured seek calls totalled **152.25 s**, and process peak working memory was **342,556,672 bytes**. Medians average the middle pair and p95 uses nearest rank. Memory stayed bounded, but this is **not smooth multi-hour audio scrubbing**.

Audio sample-count anchors are progressive/process-local; cold indexing and distant three-stream queries remain expensive. This is the next concrete backend performance issue. Persisting/reusing qualified anchors and reducing seek/decode startup should preserve exact clocks and discontinuity handling; nominal timestamp/FPS arithmetic is not an acceptable substitute. Video proxies leave original audio authoritative and do not solve this cost.

## Output and protocol correctness

The actual 1080 OBS creator outputs were independently decoded with FFmpeg CLI. **15 output frames** cover first/last frames and frames around cuts across long-form/two Shorts. Source selection uses actual source presentation timestamps, never nominal source FPS. Top 65% RGB comparisons exclude caption pixels; maximum mean RGB error was **1.090/255**, including lossy reencoding/resampling. Dimensions, duration, H.264/AAC streams, source ordering/ranges and profile cropping were checked.

**Four 200 ms PCM windows** compare all three independent source streams, explicit gain/duck envelopes and source sample origins against separately decoded AAC output. Minimum correlation was **0.999708**, maximum mean PCM error **0.001211**, and all measured offsets were **zero samples** within the +/-64-sample search. Native FFmpeg noise-reference latency compensation follows the earlier independently measured two-hop policy. These selected windows do not establish continuous acoustic sync. Captions and profile settings also have automated raster/timing/export tests and actual native captures; human caption legibility/editorial review remains pending.

A second reproducible actual-OBS fixture wrapper records, ingests, plans, commits, saves/reopens, exports and independently verifies its outputs. Its directory and result are recorded in machine evidence. CLI and real Copeland compilation were exercised on actual OBS media. Actual stdio MCP initialized, discovered 25 tools, queried the project and connected/read status from actual OBS. Separate real-WebSocket simulated-server tests cover authentication failures, response fragmentation, lost connection/reconnect, timeouts/cancellation, stop-ack/drain/status races, external take identities, path/revision rejection, idempotence and atomic ingestion. Mock OBS is never counted as a real OBS run.

Current M0 MP4/MKV playback, M1b native editing/save/reopen/export, M1b independent black/silent gap/source-order checks, and the three-hour M2d creator/export plus independent video/audio verification were rerun. Current suites total **238 passing, zero failing/skipped**. Copeland and other sibling source was not modified; referenced projects were built from current source, while older upstream test totals are not added to this count.

## Reproduce

```powershell
.\scripts\Build.ps1
# Requires installed OBS; refuses an already active OBS process and uses only its isolated fixture.
.\scripts\PrepareObsQualification.ps1 -Width 1920 -Height 1080
.\scripts\ObsCreatorSmoke.ps1 -RecordFixture -Commit
.\scripts\ObsProtocolSmoke.ps1
.\scripts\NativeQualification.ps1 -Project artifacts/local/m2e/recorded-1080.valencia
# After generation, independent output verification can be repeated without recording:
.\scripts\VerifyObsCreator.ps1 -Source '<recorded-file.mkv>' -Directory '<creator-output-directory>'
dotnet run --project samples/Valencia.Qualification -c Release -- '<source.mkv>' '<evidence-directory>'
# Fresh analysis/proxy resource measurements (existing local model required):
dotnet run --project samples/Valencia.Qualification -c Release -- '<source.mkv>' '<fresh-directory>' --resources
```

Creator wrapper defaults to proposal-only unless `-Commit` is supplied. Recording itself requires `-RecordFixture`. The verifier is deliberately bounded to <=60-second qualification inputs; production analysis remains streaming. Model weights are explicit existing local dependencies; none of these scripts downloads them. `RecordQualificationEvidence.ps1` collects already-executed results, test cases, hashes and screenshot indices, not a replacement for executing them.

## Remaining qualification and decisions

1. Obtain representative human multi-hour OBS footage, multiple voices/languages and editorial review; no WER/editorial score is invented from TTS.
2. Improve/requalify exact long audio seeking. It remains seconds-long even though the forward-video catastrophe was removed.
3. Measure prolonged playback drift, continuous scrubbing tails, real acoustic/display timing and wider source/codec/color matrices. Ten-second device runs and VFR fixtures do not establish these.
4. Add native stream-role, error, marker-refinement, profile/export and transcript UX against existing APIs. Split-file automatic assembly, replay-clock marker alignment, linked selections and sophisticated trims remain absent.
5. Qualify accessibility, touch and varied DPI. MSDF/native widget resize and shared font measurements are demonstrated; complex shaping/bidi and arbitrary tiny-window UX are not.
6. Quick fingerprints hash size/mtime and beginning/middle/end samples. They avoid mandatory full-file import hashing but are not cryptographically conclusive content identity. Derived cache budgets remain local (analysis 512 MiB, proxies 4 GiB); indices are process-local. No new distributed cache or background service was introduced.
7. Actual OBS media sometimes emits FFmpeg's 'keyframes not correctly marked' warning. It was retained in logs. Selected frame/export comparisons passed; this is not a promise that every malformed/damaged recording is repaired.

Ready for frontend implementation and further measured qualification against a working backend. Full human-footage/editorial qualification and smooth multi-hour audio interaction remain incomplete.
