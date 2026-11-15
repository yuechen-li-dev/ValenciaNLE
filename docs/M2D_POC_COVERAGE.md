# M2d original PoC coverage

The creator path is prioritized over full Premiere parity. “Validated” means the named executable path was run; it does not mean every codec/profile/language or editing gesture is qualified. Current evidence is in `validation/m2d.json` and the M2d report. Older milestone documents retain their historical results.

| Original requirement | Status | Evidence / remaining boundary |
|---|---|---|
| MP4/MKV import | Implemented and validated | Current M0 smokes, timestamp fixtures and three-hour generated MKV |
| WAV/FLAC import | Implemented and validated | Native PCM/noise/impulse and long FLAC transcription tests |
| MP3 import | Implemented but unqualified | FFmpeg finite audio import path; no new format-specific qualification |
| PNG/JPEG still-image clips | Missing | Probe requires finite duration; no still-hold semantics. Add explicit still assets/durations rather than pretend a one-frame decoder is an editor |
| H.264 decoding | Implemented and validated | Three-hour long-GOP/B-frame source and independent cut checks |
| HEVC/AV1 decoding | Implemented and validated | Actual generated libx265/libaom fixtures decoded by current native backend; no hardware decoder claim |
| Multiple independent audio streams | Implemented and validated | Selected microphone plus game/music stream, independent mix checks |
| VFR / nonzero offsets | Implemented and validated | Independent actual-PTS fixture before/at/after edit boundaries; not every discontinuous OBS capture |
| Progressive seeking | Partially implemented | FFmpeg video cue index and decode-forward; bounded decoded-sample audio anchors measured progressively. Audio anchors are process-local, rebuilt after reopening |
| Proxies | Implemented and validated | Cancellable all-intra H.264 max640, exact source PTS; short NTSC hit/fallback and full three-hour low-resolution proxy/native preview. Four-GiB published-cache policy; high-resolution throughput unqualified |
| Source/program monitors | Partially implemented | Direct M0 file player plus edited program monitor and output-aspect view; no simultaneous two-monitor source-trim workspace |
| Insert | Implemented and validated | Existing M1 commands/UI; overlap rejection rather than implicit shifting |
| Overwrite | Deliberately deferred | Compose explicit delete/trim/insert transactions; no dedicated overwrite command/UI |
| Ripple delete | Implemented and validated | Targeted-track/range Core semantics, shared by plans |
| Roll/slip/slide | Deliberately deferred | Require explicit semantic commands and focused boundary tests |
| Razor/split | Implemented and validated | Core/UI tests and native editor smoke |
| Snapping/track targeting | Implemented and validated | Existing timeline model/gesture tests; basic snapping only |
| Linked edits | Partially implemented | Planners require aligned tracks of one source and edit them atomically; no general persistent linked-selection model |
| Nesting | Deliberately deferred | No nested sequence evaluator; preserve explicit source clips |
| Playback/transport | Implemented and validated | Native Vulkan/NAudio smokes; no reverse playback or arbitrary shuttle speeds |
| Transform/crop/opacity | Implemented but unqualified | Shared declarative SDR software compositor; no comprehensive visual/numerical effect suite |
| Cross-dissolve/audio crossfade | Partially implemented | Clip fade envelopes and overlapping separate tracks express transitions; no dedicated transition command/UI or automatic equal-power crossfade |
| Exposure/contrast/saturation | Implemented but unqualified | Explicit shared SDR channel math; no HDR/color-managed grading qualification |
| White balance | Partially implemented | Simple RGB temperature control, not calibrated chromatic adaptation/tint controls |
| Captions | Implemented and validated | Source-aligned words, six-word groups, exact highlight boundaries, native preview, independent MP4 pixel test. Basic Latin layout; no rich animation/complex-script shaping qualification |
| Loudness/cleanup/ducking/fades | Implemented and validated | Native loudnorm/afftdn, measured latency compensation, explicit gain/envelopes; no dynamics compressor or speech-specific neural suppression |
| Multi-aspect output | Implemented and validated | One sequence, explicit fit/fill/focal/framing keys and four H.264/AAC outputs; automatic subject tracking deferred |
| Highlight finding | Partially implemented | Reproducible keyword/marker/activity scores and source-order selection; human editorial judgment remains unqualified |
| H.264/AAC MP4 output | Implemented and validated | Long-form plus Shorts, independently decoded content and exact plan mappings |
| HEVC output | Deliberately deferred | Decoder support does not imply encoder/profile qualification |
| AV1/VP9/Opus output | Deliberately deferred | Introduce explicit export profiles/capability diagnostics and independent mux/timing tests later |
| FCP7 XML import | Deliberately deferred | Needs interchange adapter and fixture-backed mapping; not on creator PoC critical path |
| Three hours → ten minutes + three Shorts | Implemented and validated on generated fixture | Low-resolution synthetic H.264/two-stream recording, CPU tiny.en; real OBS footage, sustained high-resolution preview, editorial quality and six-hour/five-Shorts scenario remain unqualified |

Full original-spec completion and the human Premiere “duck test” are **not claimed**. Deferred features have concrete future command/evaluator/codec/interchange boundaries above; none require a parallel document or command engine.
