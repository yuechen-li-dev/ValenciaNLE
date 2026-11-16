# Valencia

Latest UI fixes: [button alignment and scrub stabilization](docs/UI_SCRUB_STABILIZATION.md).
Paused scrubbing uses reusable video-only decoding; input/seek diagnostics are saved under `%LOCALAPPDATA%\Valencia\Logs`.

Current backend follow-up: [native rendering/A/V stabilization](docs/M2E_RENDER_STABILIZATION.md)
adds clock-paced preview and post-presentation timing checks. Current native tests
pass; the historical 250 ms draw incident remains unexplained rather than being
attributed to an unverified GPU fix.

A native C#/.NET 10 video editor in development. **M2e adds native OBS integration, controlled 1080p60/1440p60 qualification, and a native frontend handoff.** Editing, trusted Copeland scripts, CLI and optional local MCP share the same engine. Product transcription remains CPU-based.

Start with [M2E.md](docs/M2E.md), [qualification and limitations](docs/M2E_REAL_WORLD_QUALIFICATION.md), and the [frontend handoff](docs/CLAUDE_FRONTEND_HANDOFF.md). The controlled OBS-to-export PoC works; human editorial quality and broader footage remain unqualified. [Native Int64 and audio-seek fixes](docs/INT64_AUDIO_SEEK_FIX.md) add exact native scripting and fast indexed audio seeks; initial sample-count indexing still has a cold cost. [M2D.md](docs/M2D.md) retains creator commands and historical coverage.

```powershell
$cli = '.\artifacts\publish\cli-win-x64\Valencia.Cli.exe'
& $cli project create recording.valencia --media C:\Videos\recording.mkv
# Obtain asset/stream IDs from project info. Model setup is documented in docs/M2C.md.
& $cli creator run recording.valencia --asset '<asset-id>' --stream 1 `
    --keywords 'important announcement' --output-dir outputs
# Review outputs/creator-plan.json. Add --commit --verify-history to explicitly apply/save/export.
```

## Run

Use the built executable:

```powershell
.\artifacts\publish\win-x64\Valencia.App.exe "C:\Videos\recording.mkv"
```

Or build from source:

```powershell
dotnet build Valencia.slnx -c Release
dotnet run --project src/Valencia.App -c Release -- "C:\Videos\recording.mp4"
```

An initial media path is imported and placed on the timeline. With no argument, use **Import**, select a media item, position the playhead and click **Insert**. Use **Open** for `.valencia` projects. Drag clip bodies to move, or their edge handles to trim; a release commits one edit. Clips on separate tracks are independent. Video tracks with higher order cover lower ones; audible audio tracks mix.

The UI uses pixel-snapped raster glyphs for its fixed type scale and analytic GPU widget geometry, with an MSDF path available for scaled text. Three explicit native profiles cover desktop/landscape tablet, portrait tablet, and portrait phone. Layout and rendering share font measurements; shortcut hints wrap on narrow layouts. Portrait Windows captures do not imply mobile-platform support.

**OBS** opens connect/start/stop/pause/mark/import controls. Configure client credentials with `Valencia.Cli obs configure`, supplying `VALENCIA_OBS_PASSWORD` through the process environment. Stored client credentials use Windows CurrentUser DPAPI. Connecting does not change OBS settings; Start/Stop are explicit actions, and closing Valencia does not stop OBS. **Ctrl+Shift+M** records an approximate highlight observation. Import completed takes, then use Insert. See M2e for recording-root boundaries, CLI options and MCP control authorization.

Editor controls:

| Input | Action |
|---|---|
| Space / K / L | Toggle playback / pause / play at 1× |
| J | Pause and report that reverse/shuttle playback is unsupported |
| Left / Right | Step one sequence frame |
| V / C | Select tool / razor tool (click a clip to split) |
| Ctrl+K | Split selected clip, or active unlocked clips when nothing is selected |
| Delete / Shift+Delete | Delete / ripple-delete selection with its linked clips; ripple refuses to desync other unlocked tracks |
| Ctrl+Z / Ctrl+Shift+Z | Undo / redo |
| Ctrl+I / Ctrl+O / Ctrl+N | Import / open project / new project |
| Ctrl+S / Ctrl+Shift+S / Ctrl+E | Save / Save As / export MP4 |
| Wheel / Ctrl+wheel | Horizontal scroll / zoom around cursor |
| View button | Cycle sequence/creator output-profile framing in the program monitor |
| Shift+wheel | Scroll track rows; wheel over media panel scrolls assets |
| Escape | Cancel drag or background job |
| I / O / Ctrl+Shift+X | Mark In / mark Out / clear marks (transient, shown on the ruler) |
| ; / ' | Lift / extract the In–Out range on all unlocked tracks |
| Q / W | Ripple-trim previous edit to playhead / playhead to next edit, all unlocked tracks |
| Up / Down, Home / End | Previous / next edit point, sequence start / end |
| , / . | Insert (shift later media) / overwrite the selected media at the playhead |

Click the ruler or empty timeline space to seek/scrub. Track headers provide lock, mute/visibility and insert-target controls. `+V`/`+A` append tracks. New/open/close guard dirty edits. Missing media can be selected and relinked. There are no I/O marks or automatic linked audio/video selections in M1b.

For the preserved M0 player use `Valencia.App.exe --player recording.mp4`. Its original O, Space/K and Left/Right/J/L transport controls remain unchanged; `--smoke` still tests that path.

**Run script** in the media panel executes a trusted local Copeland `.ts` file against the current editing session. Scripts have ordinary CLR privileges. Escape requests cooperative cancellation; this is not an untrusted-code sandbox. The CLI is the primary headless scripting entry point:

```powershell
$cli = '.\artifacts\publish\cli-win-x64\Valencia.Cli.exe'
& $cli project info project.valencia
& $cli timeline list project.valencia
& $cli script run examples/rough-cut.ts --project project.valencia
& $cli plan validate edit.obj.ts --project project.valencia
& $cli plan apply edit.obj.ts --project project.valencia --save-as edited.valencia
& $cli export edited.valencia --sequence '<sequence-guid>' --output edited.mp4
& $cli schema export
& $cli mcp --root C:\EditingWorkspace --project project.valencia
```

The example script expects a four-second initial clip on video/audio tracks and writes a new project and movie. CLI plan application does **not** save unless `--save-as` is supplied. Scripts can explicitly call `api.Save`. MCP edits remain in its live session; it exposes no arbitrary script-execution tool. See [M2a](docs/M2A.md) for schemas, exact time, protocol configuration and trust boundaries.

For analysis, select media (or a particular audio clip/OBS stream), click **Analyze**, then **Rough cut**. Cached waveforms appear on matching audio clips; red overlays mark proposed removals. **Apply** commits one undoable transaction; **Cancel** leaves the document unchanged. The initial policy keeps all aligned tracks of the same recording together and rejects unrelated or misaligned tracks.

```powershell
& $cli analyze silence project.valencia --asset '<asset-guid>' --stream 1
& $cli analyze scenes project.valencia --asset '<asset-guid>' --stream 0
& $cli roughcut plan project.valencia --analysis '<artifact-key>' --output cut.json
& $cli roughcut apply project.valencia --plan cut.json --save-as cut.valencia
& $cli script run examples/auto-roughcut.ts --project project.valencia
& $cli schema export --analysis
```

Audio jobs calculate waveform, silence and activity together. Analysis produces derived facts and never edits a timeline. Rough-cut application is explicit; source fingerprints and revision checks protect generated plans. These are energy/visual-change heuristics, not speech recognition or highlight selection. See [M2b](docs/M2B.md) for settings, cache policy, source clocks, performance and limitations.

For local speech, explicitly download the pinned default model, set its directory, then click **Speech** in the editor. The **Transcript** panel supports Find, click-to-seek, Shift-click selection and **Retain/Remove → Apply/Cancel**. `[?]` indicates unusable word timing. Native token timestamps are approximate predictions, not forced alignment; preview cuts before applying them.

```powershell
.\scripts\DownloadWhisperModels.ps1 -Model tiny.en
$env:VALENCIA_MODEL_DIR = "$PWD\artifacts\local\m2c\models"
& $cli transcribe project.valencia --asset '<asset-guid>' --stream 1
& $cli transcript search project.valencia --analysis '<key>' --query 'important announcement'
& $cli textedit plan project.valencia --analysis '<key>' --query 'important announcement' --mode retain --output text-cut.json
& $cli textedit apply project.valencia --plan text-cut.json --save-as text-edited.valencia
.\scripts\TranscriptionSmoke.ps1 -Native
```

`examples/text-edit.ts` executes the full headless transcription/edit/save/reopen/export workflow through Copeland. Existing MCP analysis jobs plus `transcript_query` and `create_textedit` expose the same engine. See [M2c usage and qualification](docs/M2C.md) and [GPU research results and limits](docs/M2C_GPU_RESEARCH.md). No weights are downloaded automatically or bundled in published output. Build/test instructions for a fresh checkout must install the documented tiny.en fixture first; all other baseline tests remain independent of Whisper weights.

Requirements:

- Windows x64, .NET 10 Desktop Runtime, Vulkan-capable graphics/driver.
- Source builds use sibling checkouts `../Copeland`, `../InputMan`, `../Deliverance`. Project-reference locations can be overridden with MSBuild properties `CopelandRoot`, `InputManRoot`, `DeliveranceRoot`; the supplied solution groups the default sibling paths under `Infrastructure`. The published application does not require these checkouts.
- **FFmpeg 7.1 shared DLLs**, matching FFmpeg.AutoGen 7.1.1: `avformat-61.dll`, `avcodec-61.dll`, `avutil-59.dll`, `swscale-8.dll`, `swresample-5.dll`, plus that distribution's runtime dependencies. Set `VALENCIA_FFMPEG_DIR` to their directory, put that directory on PATH, or place the libraries in an adjacent `native` directory. A static `ffmpeg.exe` alone does not provide the DLLs.
- Export, proxies and loudness analysis also require **ffmpeg.exe** on PATH (or `VALENCIA_FFMPEG_EXE`), with `libx264`, AAC, `loudnorm` and `afftdn`. Native cleanup requires the matching **avfilter-10.dll** alongside the existing FFmpeg 7 DLLs. Native FFmpeg binaries are not bundled. Validation scripts additionally use `ffprobe`. Caption rendering uses pinned SkiaSharp and installed Windows Arial/Consolas fonts, with explicit failure for unavailable fonts/glyphs.
- The checked-in compiled video shader supports normal startup without a shader compiler. Regenerating it uses the existing Copeland/Aurelian toolchain and requires DXC plus `spirv-val` on PATH.

FFmpeg native binaries are not bundled. The dependency versions and source notices are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and [docs/dependencies.json](docs/dependencies.json).

## Layout

| Project | Responsibility |
|---|---|
| `Valencia.App` | Native editor, Machina shell, GPU timeline interaction, InputMan shortcuts, persistence/jobs and M0 direct-player mode |
| `Valencia.Core` | Immutable timeline document, exact timing, semantic edits, atomic transactions, bounded undo/redo and logical evaluation |
| `Valencia.Persistence` | Deliverance-backed `.valencia` snapshots, schema validation, relative media paths and load candidates |
| `Valencia.Media` | FFmpeg decoding, shared sequence evaluation/composition/mixing, bounded NAudio preview and offline H.264/AAC export |
| `Valencia.Analysis` | Source-clock streaming audio/video analysis, peak pyramid, immutable facts, waveform index and local bounded cache; no editing dependency |
| `Valencia.Rendering` | Narrow Machina-to-Aurelian integration, persistent video/UI textures, native swapchain/resize and shader artifact |
| `Valencia.Engine` | Versioned facade, compiler-owned typed plans/diffs, shared insertion adapter, bounded export jobs and optional workspace policy |
| `Valencia.Scripting` | Copeland C# backend → Roslyn → trusted managed script execution, scoped facade and bounded compilation cache |
| `Valencia.Cli` | Headless CLI and optional MCP 2025-11-25 stdio adapter |
| `tests/Valencia.Tests` | Current-source decode, seek, timing, audio and cancellation tests against generated encoded fixtures |
| `tests/Valencia.Core.Tests` | Hardware-independent document, editing, transaction, timing, query and seeded command-sequence tests |
| `tests/Valencia.Persistence.Tests` | Deliverance round trips, interrupted writes, relocation and failed-load preservation |
| `tests/Valencia.Programming.Tests` | Actual compiler/CLR calls, exact time, plans, CLI subprocesses, MCP protocol and independently decoded exports |
| `samples/Valencia.Headless` | Executable create/edit/undo/save/reopen/evaluate demonstration with a metadata-only fixture |

Core has no codec, UI, graphics or persistence dependency. `Valencia.Persistence` references Core and Deliverance. Media preview and export both use `SequenceEvaluator`, driven by M1a queries and exact source mapping. There is no Stride, Stri-V or Aetheris runtime dependency. The existing leaf `MachinaPresentationTranslator.cs` is source-linked to avoid importing the unrelated engine integration spine.

## Validate

```powershell
dotnet test tests/Valencia.Core.Tests -c Release
dotnet test tests/Valencia.Persistence.Tests -c Release
dotnet test tests/Valencia.Tests -c Release
dotnet test tests/Valencia.Programming.Tests -c Release
.\scripts\Smoke.ps1
.\scripts\Build.ps1
.\scripts\EditorSmoke.ps1
.\scripts\VerifyEditorExport.ps1
.\scripts\ProgrammingSmoke.ps1
.\scripts\AnalysisSmoke.ps1 -Native
dotnet run --project samples/Valencia.Headless -c Release -- artifacts/local/m1a-demo
```

Core/persistence tests and the headless demonstration need no codecs, graphics or audio devices. `Valencia.Tests` requires Windows, `ffmpeg` on PATH, the shared DLLs above and an audio output device for its playback integration check; it creates short encoded fixtures in test-owned temporary directories. `Smoke.ps1` additionally exercises the real native window, Vulkan GPU, NAudio audio device, play/pause, seek, resize, EOF and decoded-pixel/readback agreement. Hardware absence fails the smoke; it is not counted as success. Outputs go under ignored `artifacts/local`.

`Build.ps1` builds all projects, runs four test suites, and publishes the framework-dependent desktop and CLI executables to `artifacts/publish/win-x64` and `artifacts/publish/cli-win-x64`. `ProgrammingSmoke.ps1` generates its own fixture, compiles/executes the example, saves/reopens, applies a typed plan and independently checks the exported frames/audio.

## Current limits

The editor defaults to a 1280×720/30 fps, stereo 48 kHz sequence. Existing project sequence settings are honored; there is no format-settings panel yet. M1b supports SDR, even sequence dimensions up to 8,294,400 pixels and audio rates from 8–192 kHz, with at most eight simultaneous clips of each media kind. CPU composition/software decode and RGBA uploads are intentional initial paths. Video layers use letterboxing and opacity; audio uses gain, mute and final hard limiting. Waveforms, transforms and effects are deferred.

Known HDR PQ/HLG inputs are rejected. Full color management, display rotation/non-square pixels, arbitrary timestamp discontinuities, audio-device hot swapping, proxies and reverse/shuttle playback are not implemented. Long OBS recordings and heavy 4K layer stacks remain unqualified. Scripting is trusted local CLR execution with cooperative cancellation; schema authoring deliberately supports a finite structural subset. The Machina controls use the existing bitmap text rasterizer; timeline rectangles are native Aurelian GPU quads. See [M0](docs/M0.md), [M1a](docs/M1A.md), [M1b](docs/M1B.md) and [M2a](docs/M2A.md).

M2b supports independently selected mono/stereo audio streams. Rough cuts require an unlocked, aligned, ordered single-source sequence; they preserve existing gaps. Analysis discontinuities are retained as separate epochs, and automated editing across them is rejected. Waveform resolution adapts to a fixed bucket budget. Analysis cache data is regenerable and is excluded from `.valencia` project files.
