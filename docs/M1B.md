# M1b — integrated timeline editor, preview and export

The application now executes the editing workflow: import media → insert clips → split/trim/move/delete → undo/redo → preview the sequence → save/reopen → export H.264/AAC MP4. The existing M0 direct-file player remains available with `--player`; `--smoke` still exercises its original path.

## Structure and authoritative state

No new assemblies or NuGet packages were added. The six existing source projects remain: App, Core, Media, Rendering, Persistence and Scripting. Scripting is still reserved for a later milestone. Existing package versions/notices remain explicit; export additionally needs a user-installed FFmpeg executable with libx264/AAC, alongside the existing FFmpeg shared decoder libraries.

- `App/Editing/EditorDocument` owns the M1a `EditingSession`. Selection, viewport, tool, gesture proposal and playhead are transient. There is no second editing document in UI state.
- `TimelineViewport` uses a checked integer origin and local pixel offsets, binary-search culling and clip hit testing. `TimelineGesture` validates a candidate through `EditingSession.Preview` and commits one command on release. Escape, focus/capture loss and invalid drops cancel. Frame rounding is nearest, ties away from zero. Neighbor/playhead magnetic snapping is deferred.
- `EditorView` uses Machina for shell/widgets/ruler/headers. Dense clips, handles, track backgrounds and the playhead are Aurelian GPU quads, not Machina nodes per clip. Five track rows and five asset rows are visible, with scrolling. A fixed layout scales with the native window; there is no docking system.
- `EditorWindow` owns native event routing, InputMan maps, GPU lifetime, asynchronous jobs and seek generations. Its small standard `SynchronizationContext` queue returns async UI continuations to the window thread. Native mouse-event coordinates are retained in event order; polling final cursor position at press time was found to lose fast drags and was corrected/retested.
- `Core` retains the document/commands/queries. Two targeted additions are noncommitting candidate validation and `RelinkAsset`, which replaces asset metadata/reference through normal validation/undo. Required new-project sequence setup is a non-undoable baseline.
- `Media/SequenceEvaluator` is the **same implementation** called by preview and export. It obtains active clips and source positions from M1a `TimelineQueries`; neither caller invents cuts, gaps or placements.

## Desktop behavior

The editor defaults to 1280×720, 30 fps, stereo 48 kHz. Existing project formats, including fractional frame rates, are honored; a format-settings panel is not implemented. An initial media argument is imported and placed automatically. Otherwise Import registers media; Insert places its video/audio streams at the playhead on compatible tracks, appending tracks if necessary. Container/stream timestamp offsets are retained; unavailable stream bounds remain unknown. Import requires finite known duration.

Clip bodies move within/between compatible tracks; edge handles trim with temporary validity feedback. Selection and a timeline range/name appear in the status area. `V` selects and `C` enables click-to-razor. Ctrl+K splits the selected clip, or active unlocked clips if none is selected. Delete removes selection; Shift+Delete ripple-deletes that clip's range on its own track. Audio/video clips are intentionally independent; there is no linked selection. M1a locks, overlap rejection, source bounds and transaction rules remain authoritative. Editing pauses/restarts preview at the current playhead rather than supporting uninterrupted live editing.

Track headers toggle locks and audio mute/video visibility, and choose the insertion target. `+V`/`+A` append tracks. Wheel scrolls time, Ctrl+wheel zooms around the cursor, Shift+wheel scrolls tracks; wheel over the media panel scrolls assets. Clicking empty timeline space or the ruler scrubs; arrow keys step frames. Space toggles playback, K pauses, L plays at 1×. J explicitly pauses and reports unsupported reverse/shuttle behavior. There are no I/O marks. Full controls are in the root README.

New/Open/Save/Save As use Deliverance project snapshots and native file dialogs. Dirty state compares semantic documents with the last successfully saved document, including after undo. New/open/close protect unsaved edits. Failed loads preserve the existing session. Missing media is reported and may be relinked; all existing clip references/timebases/bounds validate against replacement metadata before commit. Saving never persists playback or rendering caches.

## Shared media semantics and ownership

The evaluator resolves actual decoded video PTS intervals against exact `SourcePosition` values. Nominal FPS is used for the **sequence output grid**, never as a replacement for source presentation timestamps. Native seeks round backward to original stream ticks, then decode forward to the actual target interval. A source cursor retains at most its current/next frame; inactive cursors are disposed. At most eight simultaneously active video clips and eight audio clips are supported. There is no full-recording resident cache.

Video starts black. Active layers compose in increasing track order, so higher order covers lower. CPU RGBA composition letterboxes each layer to the sequence, supports opacity and uses nearest sampling. Each layer's letterbox is black, including over lower layers. Hidden video/disabled clips are omitted. The resulting frame is uploaded to Aurelian for presentation; timeline geometry is drawn directly on the GPU. This is not NVDEC/NVENC or zero-copy acceleration.

Audio is decoded/resampled into stereo float PCM at the sequence sample rate. Blocks split at exact timeline boundaries mapped to sample positions. M1a queries choose audible clips; gains apply before summing, followed by hard clipping to [-1, 1]. Source positions round to the nearest resampled sample, ties away from zero. Audio PTS establishes the source PCM origin; arbitrary discontinuities within a source are not qualified. Gaps are actual zero PCM and black video.

Preview owns independent video/audio workers, a video queue capped around 32 MiB (one large frame minimum), and the existing 16×2048-frame audio block queue. NAudio 3 WaveOut drives sequence time from **played** samples, excluding underrun silence. Source→sequence mapping remains in the evaluator. Audio callbacks do not decode or wait on source work. M1b supports sequence audio rates 8–192 kHz; unsupported rates fail explicitly rather than waiting for an impossible prebuffer.

Each seek/edit creates a cancellation generation. Rapid seeks coalesce; obsolete generations cannot publish a frame into the current monitor. The monitor clears immediately on seek. If a required cut frame is late, it goes black instead of holding the obsolete source. Old output is stopped/disposed and decoder workers finish before their contexts are released. GPU resources remain window-thread owned. Local native FFmpeg calls themselves are not interruptible while blocked in storage I/O.

## Offline MP4 export

`SequenceExport.RunAsync` captures an immutable snapshot and evaluates sequence times independently of wall time, UI visibility, audio devices or preview dropping. Video times are exactly `n × frame period`; audio uses exact sample indices and the same evaluator. Output uses sequence dimensions/rate, libx264 CRF 18/veryfast/YUV420P, and stereo AAC at 192 kb/s with the sequence audio rate.

Output duration rounds **up to the next whole output frame**. The sample stream rounds up to that duration's sample count. Samples after semantic sequence end are silent. A final CFR video sample remains displayed for its frame interval; a subframe ending can therefore extend the container by less than one frame. Source ranges remain exact; M1b does not claim subframe visual cuts in a CFR MP4.

Video and PCM use separate bounded Windows named pipes into FFmpeg. Raw frames/audio are not accumulated in temporary recordings or resident arrays. The FFmpeg process owns encoder drain, delayed frames, AAC padding metadata and mux finalization. Success publishes a same-directory temporary destination only after both producers and FFmpeg complete. Cancellation/failure kills the process, cancels producers and removes partial output; an existing completed destination remains intact. Direct replacement of a registered source path is rejected. Project state is unchanged. Progress/cancellation is available in the desktop job UI. Export is CPU based and requires `ffmpeg.exe` on PATH or `VALENCIA_FFMPEG_EXE`.

## Executed validation

Reproduce from the repository root:

```powershell
.\scripts\Build.ps1
.\scripts\Smoke.ps1 -AppPath artifacts/publish/win-x64/Valencia.App.exe
.\scripts\EditorSmoke.ps1
.\scripts\VerifyEditorExport.ps1
```

Release build/publish completed with zero warnings/errors. **124 tests passed, zero failed, zero skipped**: 70 Core, 21 persistence, 33 media/player/editor. All original 107 tests remain. New coverage includes viewport precision at six hours, zoom/culling/hit testing with 5,000 clips, transient drag/trim validation, cancellation/locks, InputMan undo/redo, relink validation and new-project baseline history.

Real media tests generate distinct H.264/AAC MP4/MKV sources with visible frame numbers, fractional 30000/1001 fps, different color patterns, tones and impulses. They check source in-points, exact cut/gap boundaries, upper-track precedence, mixing/mute, cancelled seek generations, and fresh backward playback. A six-hour shifted timeline resolves real media and gap audio without decoding six hours; this is an accelerated large-time check, **not** a six-hour realtime soak.

Export tests independently decode the resulting MP4 through FFmpeg CLI, checking frame counts, selected source frames before/after cuts, black gaps, silent gaps, decoded AAC alignment against independently selected source PCM, and semantic equivalence after Deliverance reopening. Cancellation is triggered after frame production begins and verifies previous-destination bytes and temporary-file cleanup. These tests go beyond process exit/file existence.

The native editor smoke imports two actual sources, inserts, splits, drags both stream segments, trims, deletes/undoes, saves/reopens, rejects an obsolete seek, plays the ten-second edited sequence through WaveOut/Aurelian, and exports. It then renders 120 benchmark iterations with 5,000 clips, validating a real drag proposal without committing it. Frame timing includes the native loop/draw and excludes its 1 ms sleep; input timing measures viewport/gesture validation, not pointer-to-photon latency. Exact current measurements are retained in `docs/validation/m1b.json`.

On the available **RTX 3070**, actual ten-second playback measured about 33 ms maximum sampled frame/audio-device-clock difference. The source videos were 640×360/30 fps H.264/AAC with 440/880 Hz audio; export was 1280×720/30 H.264/AAC at 48 kHz. Runtime was .NET 10.0.15, SDK 10.0.401, Windows x64; native libraries/CLI were Gyan FFmpeg 7.1.1 shared. No hardware encoder/decoder test is claimed. The preserved MP4/MKV M0 smokes also passed.

The desktop smoke's export was independently decoded again by `VerifyEditorExport.ps1`: selected first-source frames matched, the moved segment resolved to the correct source time, the gap was black/silent, the second source was blue in the correct range, and codec/duration checks passed. Native Windows input automation separately reopened the project with Ctrl+O, performed body/handle drags, and verified Ctrl+Z/Ctrl+Shift+Z and dirty-state restoration. Screenshots are in `artifacts/local/m1b-smoke/`.

## Limits and remaining qualification

This is the working M1b workflow with deliberate limits: SDR only, even sequence dimensions up to 8,294,400 pixels, eight simultaneous clips per kind, 1× playback, basic CPU layer composition, stereo mix and fixed native layout. No format-settings UI, clip-property inspector, waveform display, automatic linked selections, magnetic snapping, continuous live editing, transforms, advanced color, effects, scripting, captions or AI features were added. The existing Machina bitmap font remains basic.

Ten-second realtime playback and accelerated six-hour addressing do not establish multi-hour OBS performance, long-duration device-counter behavior, storage-stall behavior or perceptual audio quality. Long VOD/random-seek soak tests, heavy 4K/many-layer throughput, audio-device changes, unusual timestamp discontinuities, rotation/non-square pixels, all DPI/keyboard layouts and perceptual listening remain manual qualification. macOS, GPU codec interop and zero-copy export remain outside this milestone.
