# M2e — OBS integration, qualification and frontend handoff

Valencia now supports native OBS WebSocket 5, completed-recording ingestion, recording markers, advisory creator preflight, and CLI/Copeland/MCP/native access. An actual OBS-generated 1080p60 recording completed analysis/transcription, atomic edits, save/reopen and independently decoded H.264/AAC long-form/vertical exports. This is a **controlled creator PoC**, not qualification of human editorial quality, broad transcription accuracy or smooth seeking on arbitrary multi-hour footage.

Read [qualification](M2E_REAL_WORLD_QUALIFICATION.md), [frontend handoff](CLAUDE_FRONTEND_HANDOFF.md), [brief](CLAUDE_FRONTEND_BRIEF.md), and [machine evidence](validation/m2e.json). Earlier milestone documents retain historical results.

## Implementation

| Owner | Responsibility |
|---|---|
| `Valencia.Obs/ObsClient.cs` | ClientWebSocket, RPC negotiation, challenge authentication, bounded messages/requests/events, timeout/cancellation/reconnect; no mutation replay |
| `ObsDiscoveryService.cs` | Version/capabilities, recording directory/state, scenes/collections, inputs/audio routing, recording-only profile reads, advisory presets |
| `ObsRecordingSession.cs`, `RecordingManifest.cs` | Take identity, completion events, approximate recording-clock markers, atomic sidecars and take archives |
| `ObsCredentials.cs` | Windows CurrentUser DPAPI client credentials |
| `Valencia.Engine/ObsIngestion.cs` | Explicit control, permitted path boundary, finalized-file probing, idempotent atomic asset registration and marker planning adapter |
| Existing editing/analysis/export owners | Shared authoritative semantics; no OBS-specific editing model |
| CLI, ScriptApi, StdioMcpServer | Thin access paths; 25 MCP tools total, five for OBS |
| `EditorObs.cs` | Native connection/start/stop/pause/mark/import controls/status |
| `EditorLayout.cs`, `EditorView.cs`, `TimelineViewport.cs` | Three explicit native roots, shared monitor/timeline/input bounds and culling |
| `NativeUiText.cs`, `NativeUiPresentation.cs`, `NativeVideoPresenter.cs` | Shared layout/render font metrics, MSDF glyphs and analytic GPU widget shapes |

`Valencia.Obs` is the only new assembly. It does not depend on Core/media/UI. Engine owns ingestion; Deliverance remains canonical persistence. Copeland was read/referenced, not modified. No browser runtime, agent framework, capture engine, codec, new compiler backend or GPU inference research was added.

## Use

```powershell
$cli = '.\artifacts\publish\cli-win-x64\Valencia.Cli.exe'
# Supply VALENCIA_OBS_PASSWORD via the process environment, never a password argument.
& $cli obs configure --endpoint ws://127.0.0.1:4455
& $cli project create recording.valencia --width 1920 --height 1080 --fps-num 60
& $cli obs preflight --project recording.valencia --recording-root C:\Recordings `
    --manifest take.obs-session.json
& $cli obs record --project recording.valencia --recording-root C:\Recordings `
    --manifest take.obs-session.json --seconds 30 --mark-at 5 `
    --allow-recording-control --place --save-as recorded.valencia
& $cli project info recorded.valencia
& $cli creator run recorded.valencia --asset '<asset-id>' --stream 1 --music-streams 2 `
    --recording-manifest take.obs-session.json --use-approximate-markers `
    --keywords 'important announcement;explanation' --output-dir outputs
# Review creator-plan.json; --commit --verify-history explicitly applies/saves/exports.
```

`obs configure` protects client credentials under `%LOCALAPPDATA%/Valencia`; it does not configure OBS. Local connections are default. Remote use requires an explicit endpoint, `--allow-remote`, authenticated `wss` and normal certificate validation. Passwords never enter project/manifest/MCP payloads or reports. OBS's server config remains its own responsibility.

`info`, `preflight` and `presets` are read-only/advisory. Input routing proves configured track flags, not audible signal or encoder availability. Presets never write a profile, stream key, scene or encoder setting. CLI `record` explicitly starts/stops a bounded 1..600-second take. Cancelling local work or disposing a client never automatically stops recording. MCP control requires local server startup `--allow-obs-control`; clients cannot enable it. No arbitrary script tool exists over MCP. Native buttons and trusted script calls are explicit actions.

## Completion and recovery

Real OBS 32 acknowledged StopRecord before encoder drain; the connector waits for `RecordStateChanged/STOPPED` and reconciles briefly lagging frontend status. Import requires completion evidence, a stable readable file without writer sharing, native FFmpeg probe success, permitted paths and the expected revision. Asset additions are atomic and idempotent. Existing paths with a different identity require explicit reconciliation. Source media is never changed.

`RecordFileChanged` is capability-gated (5.5+): it starts a file, not finalizes it. Split files import separately after stop; automatic placement/creator assembly currently requires one file. New externally started takes receive new identities. Current/per-take sidecars preserve ongoing, stopped and unknown observations through atomic replacement. Disconnection invalidates continuity and never replays recording mutations. There is no durable command journal or background service.

Markers retain reported OBS duration, receipt time/round trip, pause state, connection continuity, kind and note. Wall time is not source PTS. Absolute source uncertainty is **unbounded until refined**. Explicit approximate use makes conservative -2/+4-second candidate windows and accounts for probed split durations. Discontinuous observations and mistake/retake markers are excluded from positive seeds. Existing `SourceMarker` ranges support manual refinement, but no refinement UI exists yet. Replay files have a distinct kind; recording-clock markers cannot map to them without a replay start anchor. Replay control is an optional low-level seam, not a qualified product workflow.

## Targeted fixes

* OBS AAC begins at -21 ms while video begins at zero. Rough-cut alignment now compares source/timeline intercepts and overlapping domains instead of identical clip edges.
* Several nonspeech streams require explicit `musicStreamIndices`. Only selected music receives music gain/ducking; communications are preserved without that processing.
* Identity/geometry composition uses direct copies/native Skia. On the same 1080p fixture the prior identity-profile path averaged 43.879 ms/frame, the new path 4.023 ms, direct decode/evaluation 3.330 ms. Geometry now uses pixel-center nearest sampling, shared by preview/export. Scalar nondefault color adjustment remains and is not realtime-qualified at 1440p.
* A retained evaluator decoded every intervening frame on large forward queries. The two-hour run hit its ten-minute cancellation budget. Discontinuous forward/backward queries now reset cursors and seek; an exact one-second locality threshold preserves sequential playback. All 40 seeded video/audio seeks then completed. A real-media regression verifies PTS, independent pixels and bounded decoded-frame work.

## Native UI correction

The old frontend rasterized a fixed 1280x720 UI and stretched it. Current text uses the existing Aurelian/Machina MSDF atlas/glyph adapter; widget geometry uses analytic GPU shapes. Three explicit C# Machina roots follow Copeland TS's desktop/tablet/mobile profile concept: phone below 600 client units, portrait tablet 600..1023, desktop from 1024, with a landscape-tablet override for width >=600 and width >=height. No website/React runtime was imported.

Monitor/ruler/canvas/hit testing share those roots. Resize/focus loss cancels an edit gesture. Title/footer clipping came from mixing seven-pixel bitmap layout metrics with larger MSDF glyphs. Layout and rendering now share `NativeUiText`; shortcut groups wrap by measured width, status text ellipsizes without splitting Unicode scalars, and footer space is reserved per root.

ASCII uses bundled OFL Space Mono/Crimson Text through existing native font authority. Other text uses a pixel-sized Skia/system-font fallback. Complex shaping/bidi, touch and varied-DPI qualification remain pending. Current flat panels batch widget geometry, media/canvas, then text; overlapping popovers will need ordered layers through existing Machina/Aurelian contracts. Very short windows remain cramped. Portrait captures qualify 768x1024 and 390x844 Windows layouts, not mobile-platform support.

## Validation and readiness

Release build/publish and **238 tests passed**: Core 73, persistence 21, media/UI 60, programming 84; zero failures/skips. Tests use real Copeland compilation, CLI/MCP subprocesses, actual stdio initialization/tools, and a loopback WebSocket peer for OBS failures. Mock OBS is not counted as actual OBS qualification. Actual OBS 32.1.2 recordings and native RTX 3070/NAudio/Vulkan paths were exercised separately.

Ready for native frontend development against the existing engine. Controlled recording-to-export works. Human multi-hour footage, editorial review, broader speech testing, sustained playback and multi-hour audio-seek performance remain named qualification gaps. See the detailed evidence instead of treating this as unrestricted real-world readiness.
