# Button alignment and scrubbing stabilization

2026-11-15. Changes are confined to Valencia; no packages or engine document contracts changed.

## Delivered

- Rasterized labels center their **visible ink bounds** horizontally and vertically, using the actual device scale's hinted advances/bearings. Previously the alignment used the 1x advance/line box. Tests cover command labels and track-style labels at 100%, 125%, 150%, and 200%. Existing Machina action buttons already provide stable label-region IDs; no alternate widget system was needed.
- Protect the shared raster font/atlas dictionaries during measurement and layout. Parallel tests exposed an actual `Dictionary` concurrent-update exception; the regression now explicitly exercises concurrent measurement/face warmup. GPU atlas realization/upload remains on the window thread.
- Paused seeks use `SequencePreview` and the existing `SequenceEvaluator`. They reuse bounded video cursors and do not decode audio, build sample-count indexes, prebuffer PCM, or initialize WaveOut. Play opens synchronized audio at the final exact timestamp. Pause also cancels an outstanding request to start playback.
- A drag retains one active decode and one latest pending position, rather than canceling/reopening an A/V player on every mouse event. Release invalidates obsolete generations and requests the final actual pointer coordinate. No edit commands are added to history. Focus/capture loss, Escape, and resize clear pending gestures; capture uses the explicit Win32 HWND and is released only by its owning window.
- `VideoCursor` compares actual decoded PTS before converting pixels. One reference-counted native candidate survives lookahead; skipped/preroll pictures are no longer each converted to managed RGBA. Only the selected picture is converted. Cached same-frame queries do not reconvert. Candidate references are released with their decoder; export and preview still share the same evaluation and source-interval rules.
- Stale playback generations retire on the serialized media worker, rather than joining their workers on the UI thread. GPU uploads remain on the window thread. During dragging the last confirmed picture remains visible until its replacement arrives; the displayed playhead/monitor may therefore lag the pointer by one decode. Release never publishes an obsolete generation.

## Crash diagnosis: remaining qualification limit

The reported backwards-ruler crash was **not reproduced** in the existing Debug executable or rebuilt Release executable with the colorful MP4/OBS fixtures. Windows Application event 1000 did record an earlier Debug process failure at 18:14:05 with exception `0xc000041d` (exception escaping a native callback). That event contains no managed stack proving which operation threw. Do not attribute that historical crash to the font cache, capture handle, audio cancellation, or GPU without further evidence.

Mouse down/move/up now enter a guarded boundary. A managed failure cancels the gesture, reports an error, and saves its full stack instead of escaping through GLFW. Native access violations/device failures are not made safe by a managed catch. A deliberate callback exception in the native smoke proves containment and unchanged document revision; it does **not** reproduce the user's original defect.

Normal runs write `%LOCALAPPDATA%\Valencia\Logs\last-editor.json` (128 recent events, elapsed monotonic times, seek generation, exact ticks, pointer coordinates, requested/completed/uploaded frames, source PTS, and errors). Gesture down/up persist a checkpoint even if a subsequent native fault prevents unwinding. `last-input-error.txt` contains the latest pointer exception; `last-error.txt` remains the main program's failure report. Logs are diagnostic snapshots, not a durable journal; they may include local media information. Smoke logs stay in their requested output directory and deliberately injected errors are identified as probes.

If the abrupt crash recurs, these files identify the last completed step and, for managed callback failures, the actual stack. The unisolated historical termination is the remaining blocker to claiming a fully diagnosed crash fix. A running older Debug instance needs restarting/rebuilding to use these changes; it was left open to preserve the user's work.

## Measurements and validation

Machine: Ryzen 7 7700X, RTX 3070, NVIDIA driver 610.88 (`32.0.16.1088`), .NET runtime 10.0.15, SDK 10.0.401, FFmpeg 7.1.1 binding, NAudio 3.3.0. Vulkan validation was available with zero errors on both scrub fixtures.

The retained samples and exact commands are in [validation/ui-scrub-stabilization.json](validation/ui-scrub-stabilization.json). Ordinary desktop/background workloads were not disabled; these are bounded local measurements, not exclusive-machine benchmarks. CPU seek latency excludes GPU presentation and is distinct from native request-to-displayed-frame latency.

| Comparison | Before | After |
|---|---:|---:|
| 1080p60 MP4, 12 identical random requests, CPU decode-ready median | 295.81 ms | 165.24 ms |
| Same comparison, p95 / maximum | 801.42 ms | 401.85 ms |

The baseline is the former full A/V open path before the preroll conversion fix; the final column is the reusable paused video path after it. An intermediate video-only implementation alone did not improve the 1080p case; source PTS/lookahead profiling identified the remaining conversion work. Positions span the first 12 seconds of this fixture, not hours into an arbitrary recording. The OS cache was not flushed. Median averages both middle samples for even sample counts; initial progress updates used the upper middle sample (376/212 ms), superseded by the conventional medians above.

Native smokes exercise 24 ruler gestures / 2,400 move events per fixture, source-monitor to timeline switching, final-position publication, audio restart, pending-play cancellation, callback error containment, and unchanged revision. They use the actual coordinate/capture handlers with synthetic events. Physical mouse drags and Space playback were also exercised in a separate native editor window. Synthetic move counts are not a claim of 2,400 physical mouse actions. Native result files report measured latency distributions, skew and underruns.

Final native request-to-displayed-frame results: colorful 640x360/30 MP4 median **32.15 ms**, p95 **41.56 ms**; OBS 2560x1440/60 MKV (three independent AAC streams) median **89.78 ms**, p95 **142.30 ms**, maximum **147.34 ms**. Both use the editor's 1280x720/30 sequence, with zero unexpected callback failures, zero underruns and zero Vulkan validation errors. Corresponding maximum observed A/V skew was 40.81 / 41.60 ms. Native resize/layout captures cover six window sizes plus source/proposal/selection states; the 5,000-clip visible-range check passed. Device-scale geometry tests do not imply physical multi-monitor DPI qualification.

The current tests pass: Core 73, Persistence 21, Media/UI 100, Programming 85; **279 total, zero failures or skips**. Regression coverage includes exact PTS/cuts/gaps/layers, backwards seeks, no-audio preview, cancellation, snapshot invalidation, retained-frame conversion counts, centered glyph bounds, shared cache access, and diagnostic bounds/stack preservation.

Native M0 playback and the edited editor smoke pass the unchanged **150 ms** A/V limit. The edited smoke plays across cuts/gaps/another source, saves/reopens and exports H.264/AAC. Independent FFmpeg decoding verifies the source selections, black/silent gap, second blue source and duration. Its duration is 9.666667 seconds: Claude's linked trim shortens both video and audio. The verifier now compares against the document duration recorded before export, retaining the existing 40 ms tolerance; its old hardcoded ten-second expectation described the earlier unlinked trim. Export semantics were not changed to satisfy that check.

## Repeat

```powershell
dotnet build Valencia.slnx -c Release -m:1 -nr:false -p:UseSharedCompilation=false
# Run each of Valencia.Core.Tests, Valencia.Persistence.Tests, Valencia.Tests, Valencia.Programming.Tests:
dotnet test tests/Valencia.Tests -c Release --no-build
dotnet publish src/Valencia.App -c Release -r win-x64 --self-contained false -o artifacts/publish/win-x64 -m:1 -nr:false -p:UseSharedCompilation=false
dotnet run --project samples/Valencia.Qualification -c Release --no-build -- artifacts/local/m2e/motion.mp4 artifacts/local/ui-scrub-fix/comparison --scrub-seeks
& artifacts/publish/win-x64/Valencia.App.exe --scrub-smoke artifacts/local/fixtures/av.mp4 --output artifacts/local/ui-scrub-fix/final-short
& artifacts/publish/win-x64/Valencia.App.exe --scrub-smoke 'artifacts/local/m2e/recordings/2026-11-15 12-56-56.mkv' --output artifacts/local/ui-scrub-fix/final-obs
& artifacts/publish/win-x64/Valencia.App.exe --smoke artifacts/local/fixtures/av.mp4 --output artifacts/local/ui-scrub-fix/final-player
& artifacts/publish/win-x64/Valencia.App.exe --editor-smoke artifacts/local/fixtures/av.mp4 --output artifacts/local/ui-scrub-fix/final-editor
& scripts/VerifyEditorExport.ps1 -Directory artifacts/local/ui-scrub-fix/final-editor -SourcePath artifacts/local/fixtures/av.mp4
```

`--scrub-seeks` retains the full-player comparison and the new preview mode; reproducing the historical numbers requires the recorded pre-conversion source. Current full-player playback also benefits from the selected-frame conversion fix. Release output: `artifacts/publish/win-x64/Valencia.App.exe`. No changes were committed or pushed.
