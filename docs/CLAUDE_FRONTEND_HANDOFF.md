# Claude native frontend handoff

## Pointer feedback and client title bar (2026-11-16)

**Remaining hardware blocker:** rapid scrubbing now reproduces the historical
approximately 250 ms swapchain acquisition/fence wait in current and pre-change
executables. Current traced skew is 255 ms versus the unchanged 150 ms contract.
See POINTER_FEEDBACK.md and validation/pointer-feedback.json; do not assume all
native timing qualification passes. GPU passes and layout are short in the
stalled frames; the underlying WSI cause remains unknown.

See [POINTER_FEEDBACK.md](POINTER_FEEDBACK.md) and its machine-readable evidence.
Use shared `WidgetSession.Cursor` / `IsPressed` and Aurelian's native cursor adapter.
Existing StandardUI controls can use `UiPointerFeedback` plus
`MachinaPointerPresentation.Apply` on the original prepared frame: hover-only
repaint must not wait for the periodic 100 ms layout refresh or mutate the project.
Native position sampling also handles movement into non-client caption regions.

`AurelianWindowChrome` and `WindowTitleBar` own the optional Windows caption boundary.
Reserve its height through `EditorLayout.For(..., captionHeight: 32)`; ordinary
editing panels start below it. Caption drawing/hit testing share one geometry.
Close must remain an ordinary system close reaching the existing Closing/dirty
project policy. Dispose chrome and cursor before destroying the native window/input.
Do not draw or invoke the media/editing engine inside the native subclass callback.
Physical Snap interaction and mixed-DPI multi-monitor drag are still manual checks.

## MSDF and UI failure follow-up (2026-11-16)

See [MSDF_RESULT_FIX.md](MSDF_RESULT_FIX.md) and [validation/msdf-result-fix.json](validation/msdf-result-fix.json).
Valencia now builds with .NET 11 / C# 15; adjacent shared libraries retain their existing targets.
MSDF pixel range must be converted to outline units during generation. Normalize atlas UVs **before**
clipping: reversing the V interval afterward clips the opposite edge and shifts glyphs with different padding.
Use the shared font's kerning/metrics for both measurement and presentation; do not raise Q independently.

Native resize callbacks must only invalidate/cancel transient input; prepare the new layout on the event loop.
Below 320x480 client units, show the noninteractive compact placeholder. Zero-sized windows skip preparation.
`EditorView.TryPrepare` returns `Result<MachinaPreparedPresentation, EditorFailure>` and retains the previous
frame/hit-test tree on failure. UI jobs and input/continuation boundaries likewise translate legacy exceptions
to typed failures and diagnostics. Do not swallow errors as successful edits, discard the current project,
or put these result wrappers on the media-buffer path. EditingSession's existing EditResult/transactions remain authoritative.
Native device/process faults are not made recoverable by this union. No new security or exception sandbox is claimed.

## Rendering and playback stabilization status

[M2E_RENDER_STABILIZATION.md](M2E_RENDER_STABILIZATION.md) supersedes the earlier
playback timing qualification. The confirmed defect was preview FIFO backpressure:
after a UI stall, queued obsolete frames delayed recovery despite an advancing
audio clock. Preview now prepares one frame ahead, publishes on the device clock,
and keeps a bounded latest-due-frame channel. Raw decode and offline export still
retain every requested frame. The 150 ms contract is unchanged and is now also
observed **after** presentation, so a slow draw cannot escape a pre-submit check.

Current RTX 3070 native tests pass, including cuts/gaps, pause/seek/resize,
captions/aspects and concurrent fresh analysis. The historical recurring 250 ms
host `Draw()` stall did not reproduce; no driver/fence root cause or GPU throughput
fix is claimed. Retain the bounded tracing path for any recurrence. This remains a
qualified synchronization correction, not closure of the historical GPU incident.

### Rules the frontend must preserve

* Never block the window loop on swapchain acquisition. Call `PollImage` (via `NativeVideoPresenter.Draw`) and skip
  frames while the compositor has no free image; input, seeks and media clocks must keep running. Observe A/V skew
  only when `LastDrawPresented` is true.

* `NativeVideoPresenter`, Machina realization and every Vulkan resource remain on
  the window thread. Media/analysis/export workers prepare CPU data and never call
  graphics methods. Avoid synchronous file/model/index work in UI callbacks.
* `UpdateVideo(width,height,rgba)` copies/uploads synchronously and waits for its
  GPU copy before returning. The caller can release its CPU reference afterward.
  Queued preview frames own managed arrays; do not mutate them while shared.
* `Draw()` realizes the existing passes and presents; `ClearVideo()` clears the
  monitor immediately when requesting a new seek. Keep generation rejection in
  `EditorWindow`; never let an obsolete asynchronous open publish its frame.
* The audio-device clock is authoritative. `PreviewFrameClock` is per-generation
  media internals, not editing state. Do not restore a blocking historical-frame
  FIFO or remove pacing: an unpaced drop-oldest queue would decode to EOF.
* Device-clock reads are serialized with preview producers. Stop/cancel/join the
  media generation before disposing its audio device. Library teardown must use
  `ConfigureAwait(false)`; it cannot require UI-context pumping during shutdown.
* Resize remains a window-thread surface rebuild at the next drawable frame.
  Existing device-idle waits at surface teardown are intentional ownership guards.
  Upload/pass completion fences remain intact. No asynchronous upload ring or
  altered frames-in-flight policy has been introduced.
* Reuse unchanged prepared Machina declarations and the MSDF atlas where possible;
  invalidate on semantic view/viewport changes. Keep current font metrics and
  presentation consistent. Never build one widget per clip or restore a stretched
  UI bitmap. PNG encoding/readback and cold font preparation are not cheap UI work.

For a diagnostic run, set `VALENCIA_RENDER_TRACE` to an artifact JSON path before
starting the app. It retains at most 4096 frame records, reports dropped trace
records, captures debug-utils warnings/errors, CPU phases, GPU timestamp intervals,
GC observations and post-present media clocks. It excludes decoded pixels. This
format and Aurelian timing properties are experimental diagnostics, not v1 engine
contracts. GPU timestamps and host Stopwatch clocks are separate domains; their
difference is not input-to-photon latency. Do not leave profiling enabled by
default or treat untested device-loss behavior as qualified.

Stable boundaries remain immutable Core/Engine v1 commands, queries, plans and
persistence. `DecodeSession`, `SequencePlayback`, the window adapter and diagnostic
records are source-level integration seams. Copeland supports native checked
`long` while canonical wire timestamps remain decimal strings.

## Build and ownership

Run `scripts/Build.ps1`, then `artifacts/publish/win-x64/Valencia.App.exe <project.valencia|media.mp4>`. Runtime is Windows/.NET 10, native Machina.UI, Aurelian Vulkan, FFmpeg and NAudio 3. Sibling project roots are configured in `Directory.Build.props`. FFmpeg DLLs/executable and explicitly installed Whisper weights remain external requirements; see M0/M2C. No Node/npm/browser frontend is part of Valencia.

[M2e](M2E.md), [qualification](M2E_REAL_WORLD_QUALIFICATION.md) and [design brief](CLAUDE_FRONTEND_BRIEF.md) define scope and evidence. Keep the current MSDF/analytic-shape path and three explicit native layout roots. Do not restore a stretched UI bitmap or invent a relational layout engine.

Codex owns semantic/timing/media/persistence/OBS correctness and backend qualification. Claude owns native authoring, visual hierarchy, workflow and interaction design. New semantic operations belong in Core's reducer, not renderer-only mutable state. Version-1 engine DTOs are the authority; UI/OBS adapters remain evolving source-level seams, not a promised binary plugin ABI.

## Source and lifetime map

| Source | Responsibility |
|---|---|
| `Valencia.App/Editing/EditorWindow.cs` and partials | Native loop, dispatch/dialogs, seek generations, jobs/playback lifetime |
| `EditorDocument.cs` | Current EditingSession/saved-state comparison; transient selection, playhead, viewport, profile and analysis state |
| `EditorView.cs`, `EditorLayout.cs` | Machina declaration, shared local panels, three explicit native roots |
| `TimelineViewport.cs`, `TimelineGesture.cs` | Local-time pixels, visible culling, validated drag/trim ghost, one release commit |
| `EditorKeymap.cs` | Silk adapter for the editor's single InputMan profile: left/right modifier folding, one tick per key transition, focus reset |
| `EditorShortcuts.cs` | Command-to-shortcut table (Premiere Pro Windows defaults), per-command user overrides from `%APPDATA%\Valencia\keybindings.json`, conflict checks |
| `Valencia.Rendering/NativeVideoPresenter.cs` | Window-thread Vulkan, MSDF/analytic shapes, monitor upload, canvas/readback |
| `NativeUiText.cs`, `NativeUiPresentation.cs` | Shared font metrics, wrapping/ellipsis and native realization/fallback |
| `Valencia.Core/Timeline`, `/Editing` | Immutable model, exact mapping/queries/captions, validation/commands/transactions/history |
| `Valencia.Engine` | Facade/plans, analysis/creator/export jobs, workspace policy, OBS ingestion |
| `Valencia.Media` | Worker-confined FFmpeg, shared preview/export evaluator, device playback, composition/proxies |
| `Valencia.Persistence/ProjectFile.cs` | Canonical Deliverance snapshots/validation |
| `Valencia.Scripting`, `Valencia.Cli` | Trusted Copeland host, CLI, optional stdio MCP |

Small gestures use the same Core session directly; asynchronous engine wrappers share that session. There is no independent UI editing document. Requery a coherent immutable snapshot after commit/revision change and rebuild the view; no reactive subscription framework is promised. Selection/hover/drag ghosts/playhead/viewport/progress stay transient. Load must succeed and validate before replacing session/view. Stable IDs and semantic properties stay in the document.

Use `MachinaPresentationPipeline.Prepare(root, width, height, new UiLoweringOptions(NativeUiText.Instance))`. Prepared output supplies both frame and hit testing. Use existing Machina `UI`, `StandardUI.Frame/Panel/Button`, stack/grid facilities. Do not create one widget per clip. Font measurement and presentation must use the same authority. `NativeUiText.WrapHints` and `Ellipsize` fix clipped footer/status labels; preserve measured widths. Current fonts are regular 12-unit Space Mono/30-unit Crimson headings, a starting choice rather than product-design doctrine.

Desktop/landscape tablet uses a side media panel. Portrait roots stack panels, use two-column media entries and two transport rows. Bounds/visible track count follow the root. Portrait captures are Windows resize tests, not Android/iOS support. Tiny windows, accessibility, popovers, touch and varied DPI remain explicit work. Current flat batching renders widget geometry, media/canvas, then text; overlapping popovers require ordered layers through existing rendering contracts. Normal startup uses packaged shaders; `Valencia.App.exe --prepare-shaders src/Valencia.Rendering/Assets/VideoQuad.json` regenerates video/MSDF/analytic artifacts from Copeland sources.

## Existing API calls

Examples use `Valencia.Core`, `.Editing`, `.Timeline`, `Valencia.Engine`, `Valencia.Media`, `Valencia.Scripting`. These are current signatures, not a proposed interface.

```csharp
await using var engine = await EditingEngine.OpenAsync(projectPath, cancellation: token);
var snapshot = engine.Session.Snapshot;
var query = engine.Query();
var sequence = query.Sequences[0];
var tracks = query.Tracks(sequence.Id);
var duration = query.Duration(sequence.Id);
var active = query.Evaluate(sequence.Id, new SequenceTime(0));
// ActiveClip includes stable IDs, source/stream and exact SourcePosition.
var missing = engine.MissingMedia();
await engine.SaveAsync(saveAsPath, token);
var opened = await engine.OpenIntoAsync(otherProjectPath, engine.Session.Snapshot.Revision, token);
```

Do not pass authoritative ticks through floating-point seconds or Copeland `number`. `SequenceTime.TicksPerSecond` is 254016000000. SourcePosition retains rational timestamp plus exact tick offset. `TimelineTime.FromFrames`, `FromSamples`, `SnapToFrame`, `Rebase` provide explicit rounding. Intervals are half-open. Viewport calculations subtract the exact local origin before using floating-point pixels. Canonical wire Int64 values remain strings. Every MCP tool, including project_info, requires projectId; obtain it from CLI project info/configured project metadata first.

```csharp
var before = engine.Session.Snapshot;
var command = new EditCommand(Guid.NewGuid(), 1,
    new MoveClip(sequence.Id, clipId, targetTrackId, new SequenceTime(targetTicks)), before.Revision);
var proposed = engine.Session.Preview(command); // no commit or history consumption
var committed = engine.Execute(command, token);
if (!committed.Success) { /* surface Error/Message; retain current document */ }
var undo = engine.Undo(engine.Session.Snapshot.Revision, token);
var redo = engine.Redo(engine.Session.Snapshot.Revision, token);
```

Multiple commands use `EditTransaction(Guid, 1, expectedRevision, ImmutableArray<EditCommand>)` and `engine.Execute(transaction, token)`, yielding one logical undo entry. See `EditCommands.cs` for supported operations; ripple deletion has explicit track scope and split IDs. Locks, overlap/source bounds and revisions remain enforced by Core. Preview during pointer capture, commit once on release, cancel on Escape/capture loss/focus loss/resize. A failed proposal must not partially commit.

```csharp
var preview = engine.Plan(plan, commit: false, cancellation: token);
if (preview.Valid) {
    // A dry run reserves nothing; commit revalidates revision/preconditions.
    var applied = engine.Plan(plan, commit: true, cancellation: token);
}
Guid analysisJob = engine.Analysis.Start(new AnalysisRequest(assetId, streamIndex, "audio"));
var progress = engine.Analysis.Status(analysisJob);
var finished = await engine.Analysis.WaitAsync(analysisJob, token);
var artifact = await engine.Analysis.ResultAsync(finished.Key, token);
engine.Analysis.Cancel(analysisJob); // cancel the owned job, not only its waiter
var cut = await engine.RoughCutAsync(sequence.Id, artifact.Key, new RoughCutPolicy(), token);
var textCut = await engine.TextEditAsync(textEditRequest, token);
Guid creatorJob = engine.CreatorJobs.Start(creatorRequest, engine.Session.Snapshot.Revision);
var creatorStatus = engine.CreatorJobs.Status(creatorJob);
engine.CreatorJobs.Cancel(creatorJob);
```

Analysis kinds: audio, scenes, transcript, loudness, proxy and index. Contracts are authored in compiler-owned `Valencia.Engine/Contracts/analysis.obj.ts` and `edit-plan.obj.ts`; reuse type-profile/TSON extraction. Enums/records/interfaces may be preferable for some contracts; no competing schema authoring system is needed. Artifacts are derived resources outside the Deliverance project. Use compact pages/summaries for large data. CreatorRequest.MusicStreamIndices must be explicit with multiple nonspeech streams: communications cannot be silently treated as music.

```csharp
Guid exportJob = engine.Exports.Start(sequence.Id, outputPath, profileId);
var exportStatus = engine.Exports.Status(exportJob);
engine.Exports.Cancel(exportJob);
var exportFinished = await engine.Exports.WaitAsync(exportJob, token);
// Or await engine.ExportProfilesAsync(sequence.Id, outputDirectory, token).
```

Export captures an immutable revision, runs independently of editing/device/wall clocks and publishes temporary output only after finalization. Progress includes frames/total/state/error. Dispose/cancel owners on project shutdown. The toolbar currently calls `SequenceExport.RunAsync` for a single sequence export; profile jobs are available for a native selector/queue.

```csharp
// Open/replace on a worker, not as blocking work in render/input/audio callbacks:
await using var playback = await SequencePlayback.OpenAsync(snapshot, sequence.Id, at, token, profile);
playback.Play();
// Window loop:
if (playback.Update() && playback.Current is { } frame)
    graphics.UpdateVideo(frame.Width, frame.Height, frame.Rgba);
playback.Pause();
```

Reuse `EditorWindow.Seek/BeginSeek/CompleteSeek`: pause/clear old output, cancel/dispose obsolete workers, open from a coherent snapshot, reject obsolete completion, then upload/present on the window thread. NAudio device positions drive realtime playback, never offline timestamps. Frame/sample buffers stay off the command API. `SequenceEvaluator` is disposable and worker-confined; preview/export must share its semantics.

```csharp
using var scripts = new CopelandScriptHost();
var result = await Task.Run(() => scripts.RunFileAsync(scriptPath, engine,
    cancellation: token, allowProjectReplacement: false), token);
```

Compilation may do synchronous work before awaiting: keep it on a worker. Pass the current session and forbid GUI project replacement. Results retain source-located diagnostics/cache status. Scripts run ordinary CLR code and are trusted local code; type checking and AssemblyLoadContext are not security sandboxes. Prefer validated plans for external agent proposals. MCP has no arbitrary-script tool.

```csharp
var obs = await engine.ConnectObsAsync(new ObsConnectionOptions(endpoint), password,
    manifestPath, expectedRecordingRoot, allowRecordingControl: true, cancellation: token);
var discovery = await obs.DiscoverAsync(token);
await obs.StartAsync(token); // explicit user action
var marker = await obs.MarkAsync("Explain this", cancellation: token);
await obs.PauseAsync(true, token);
await obs.PauseAsync(false, token);
await obs.StopAsync(token);  // awaits actual STOPPED/drain event
var imported = await obs.ImportIntoAsync(engine, engine.Session.Snapshot.Revision, token);
await engine.DisconnectObsAsync(); // never auto-stops OBS
```

Present `Recording.Snapshot`, `Recording.Client.Status/Version`. The low-level Events channel has one session observer; the UI must not race it with another consumer. Snapshot polling suffices. Display completion/routing/marker uncertainty honestly. `ObsCredentials.Load/SaveAsync` own DPAPI. Native Connect reads saved/environment credentials and asks for an expected recording directory; it does not reconfigure OBS output.

## Concurrency and errors

Window input/layout/upload/presentation stay on the window thread. Decode/index/Whisper/compilation/export stay on workers. Reuse `EditorSynchronizationContext` and `RunJob`, which marshal completion/status and prevent conflicting GUI edits. Backend revisions still apply. Snapshot jobs, revalidate stale proposals, dispose all playback/evaluator/job/OBS/script owners. Cancelling WaitAsync generally cancels only the waiter; cancel the job too. Structured edit failure is distinct from a runtime exception: surface diagnostics without discarding the project.

## Capability and UX matrix

| Feature | Engine | Native UI now | Limitation / suggested work |
|---|---|---|---|
| Media/bins | Metadata, exact streams, stable assets | Import/media entries/scroll | No bins/rich metadata/role picker; prioritize stream naming/roles |
| Monitors | Sequence evaluator/direct player | Program monitor, separate M0 | Add source inspection without another placement model |
| Timeline | Exact queries, GPU canvas | Ruler seek/scrub, zoom/scroll | No overview; long audio seeking remains slow |
| Edits | Insert/move/trim/split/delete/ripple/history | Capture preview/release/razor/shortcuts | Independent tracks, no linked A/V or advanced trims |
| Tracks | Add/state/order/model identity | +V/+A, lock/mute/visibility/target | No reorder/rename UI; generic stream labels |
| Waveforms | Cached multiresolution source data | Visible GPU bars after Analyze | Better stream choice/job/cache status |
| Transcript | Local DTW/search/corrections/text plans | Small word/search/select/retain/remove panel | Full viewport/corrections/model picker absent |
| Captions | Source words/style/grouping/highlight | Preview saved settings | No inspector; groups are per clip and can change on split |
| Effects/color | Simple SDR transform/color/fades | Preview saved properties | No controls; nondefault scalar color not realtime-qualified |
| Audio | Cleanup/gain/duck/loudness | Saved processing plays | No roles/meters/gain UI; never duck communications by assumption |
| Multi-aspect | Profiles/framing/export windows | View cycles framing | Add profile/range/framing UI; monitor transport is still full sequence |
| Export | H.264/AAC, jobs/cancel/profiles | Single export/status/cancel | Native profile selector/settings/queue needed |
| OBS | Auth/discovery/control/completion | Connect/start/stop/pause/mark/import | Preflight/credential settings/take review absent |
| Markers | Types/notes/OBS clock/archives | Highlight action, Ctrl+Shift+M | Show uncertainty and add source refinement |
| Analysis/plans | Jobs/cache, typed diff/dry-run/atomic commit | Analyze/overlays/Apply/Cancel | Show affected tracks, source evidence and full diff |
| Copeland | C# backend/Roslyn, exact string ABI | Trusted Run script/status | No IDE; improve diagnostics/trust indicator |
| Persistence | Deliverance atomic snapshots/validation | Save/open/save-as/dirty/relink | Missing-media panel/recovery UX needed |
| Input/accessibility | InputMan/semantic edits | Shortcuts and OS dialogs | GPU widgets lack an accessible control tree |
| Errors/jobs | Explicit failures/revisions/bounded owners | Status/logs/cancel | Persistent details/retry/error panel needed |

## Screenshots and interactions

`validation/m2e.json` indexes actual GPU readbacks and dimensions for empty/imported/timeline/playback/transcript/caption/vertical/analysis/export/OBS states (1280x720), desktop/wide (1280x720, 1920x1080), portrait tablet (768x1024), phone (390x844), 5000 clips and Unicode. These are application captures, not illustrative mockups.

A separate Windows input check opened the creator project with Ctrl+O, sought on the ruler, split with Ctrl+K, undid/redid, dragged the final video clip 4s ->5s with capture, restored it, played/paused with Space/K and maximized the window. Title/footer stayed visible. Changes were undone and the window closed without saving. See `validation/m2e-interactions.json`. Headless interaction tests also cover cancellation/locked tracks/culling, but are not physical pointer demonstrations. Touch, varied DPI, external mid-drag focus loss and extended editorial use remain unqualified.

## Suggested sequence

The [GPU text rendering correction](UI_TEXT_RENDERING_FIX.md) supersedes the earlier CPU-only alignment qualification. UI/text shaders must preserve input clip positions exactly: never add a vertex/instance-ID positional bias to keep compiler builtins live. Those builtins have a separate compiler probe. Machina buttons/badges use explicit `TextNode.FillAvailableSpace` layout; IDs must not determine label bounds. Small controls use device-sized hinted glyphs, larger text uses MSDF with true-contour alignment, and the renderer converts MSDF field scale to framebuffer pixels. `--layout-smoke` now checks actual GPU ink against control bounds at six window sizes and simulated 1–2x device scales. Avoid application-level offsets to compensate for renderer defects.

1. Media/stream roles, transport/focus, missing-media and persistent errors.
2. Timeline trim/drag affordances, ruler/selection and navigation within three roots.
3. Full transcript/source-evidence/diff inspection with explicit Apply/Cancel/undo.
4. Existing creator/caption/profile/audio settings through native inspectors.
5. OBS preflight/take/marker review and export profiles in one recording-to-export flow.
6. Accessibility/device/DPI qualification, then polish. Preserve the text metric fix.


## Keyboard shortcuts

- Every editor shortcut lives in `EditorShortcuts.PremiereDefaults`. Add a command there, never as a special case in the keymap.
- Matching is InputMan's `ExclusiveModifiers`: a shortcut is a key plus an exact Ctrl/Shift/Alt set, so `K`, `Ctrl+K` and `Alt+K` are three different shortcuts. The Windows key blocks all editor shortcuts.
- Premiere's defaults are the adoption default, not a constraint. Users override per command with an InputMan profile at `%APPDATA%\Valencia\keybindings.json` (the format InputMan's `RebindingManager` saves). A command named in the file takes exactly the file's shortcuts; commands the file does not mention keep their defaults. A file with unknown commands, non-keyboard controls or conflicts is ignored whole and the reason is shown in the status bar.
- A settings screen should run an InputMan rebind session on `EditorKeymap.Engine` with `AllowChord`, `ModifierControls = EditorShortcuts.Modifiers`, conflict policy `Reject` (or `ReplaceExisting` for swaps), then save with `EditorShortcuts.Save`.
- Known Premiere gaps that need features rather than keys: J reverse shuttle, M sequence markers (Ctrl+Shift+M is Valencia's OBS highlight marker), Ctrl+D/Ctrl+Shift+D transitions, Ctrl+R speed, T titles, B/N/R/Y/U trim tools, A track select.
