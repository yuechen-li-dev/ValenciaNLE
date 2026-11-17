# Native cursor, hover and custom title bar

**Interaction increment delivered; full playback regression qualification is blocked.**
The native cursor/hover/chrome checks and edited playback/export smoke pass.
The additional rapid-scrub smoke reproduces the historical acquisition stall in
both this build and a pre-change executable. Passing unit tests and editor smoke
do not qualify every native playback path.

The actual Valencia editor now uses shared Machina pointer feedback:

- Hand over buttons and other actionable controls; I-beam in shared text inputs.
- Distinct immediate hover and pressed surfaces, preserving selected colors.
- Move cursor over clip bodies, horizontal trim cursor at edges, crosshair for
  the razor, not-allowed on locked clips, and hand over the ruler/scrubbing.
- Pointer target changes repaint on the next loop instead of waiting for the
  editor's 100 ms periodic UI refresh. The hover-only path reuses resolved layout
  and rebuilds presentation colors without remeasuring/reflowing the UI.
- A Machina-drawn title bar replaces the default caption. Shared geometric icons
  and native Windows hit testing retain minimize/maximize/restore, close policy,
  resize edges and caption dragging. The editing layout reserves 32 client units.

The shared implementation is in the sibling Copeland repository:
`docs/Machina.UI/native-pointer-feedback.md`, `Machina.Standard/Windowing`,
`Machina.Standard/Theme/StandardInteraction.cs`, `Machina.Runtime/Input/UiPointerFeedback.cs`,
`Machina.Pipeline/MachinaPointerPresentation.cs`, and the Aurelian Machina graphics
integration. No new package, browser runtime or renderer was introduced.

`EditorWindow` owns its transient feedback/cursor/chrome alongside the existing
native input context. Native callbacks only invalidate/route input; rendering
runs on the window thread's normal loop. `EditorPointerFeedback` consumes the
existing hit-test tree and timeline viewport; it never changes editing state.
Caption controls post system commands, so close still uses the existing unsaved
project confirmation. Focus/capture loss and cancellation clear transient state.

The native gallery is available with `Valencia.App.exe --widget-gallery`.
Build and reproduce validation from the repository root:

```powershell
dotnet test Valencia.slnx -c Release -m:1 -nr:false -p:UseSharedCompilation=false
dotnet publish src/Valencia.App -c Release -r win-x64 --self-contained false -o artifacts/local/pointer-host -m:1 -nr:false -p:UseSharedCompilation=false
./artifacts/local/pointer-host/Valencia.App.exe --widgets-smoke --output artifacts/local/pointer-gallery
./artifacts/local/pointer-host/Valencia.App.exe --editor-smoke artifacts/local/fixtures/av.mp4 --output artifacts/local/pointer-editor
./scripts/VerifyEditorExport.ps1 -Directory artifacts/local/pointer-editor -SourcePath artifacts/local/fixtures/av.mp4
```

The fixture is the existing generated numbered H.264/AAC 640x360 30 fps six-second
source plus the existing blue second source from `scripts/EditorSmoke.ps1`.
Tests use generated fixtures, not user recordings. Application-owned GPU readback
supplies images; no screen recording/capture session is opened.

Actual counts, timing samples, GPU/driver, validation errors, native-message
checks and independent export verification are in
[validation/pointer-feedback.json](validation/pointer-feedback.json).
Current-source results on the Ryzen 7 7700X / RTX 3070, driver 610.88:

| Check | Result |
|---|---|
| Automated suites | 879 Machina + 76 Aurelian integration + 304 Valencia = **1,259 passed**, zero failed/skipped; 22 added cases |
| Native component gallery | 34 stages; real native cursor assignment, native caption messages and GPU pixel differences passed |
| GPU and caption callbacks | Vulkan validation enabled: zero errors; zero managed caption callback failures |
| Editor pointer event through completed Draw | Four samples: 7.43, 10.59, 6.91, 6.95 ms; sampled wall time, not an optical latency distribution |
| Cached gallery draw | 120 samples: CPU p50 5.45 ms, p95 5.89 ms, p99 6.13 ms, max 11.66 ms |
| Edited native playback | Passed; 9.67 seconds real playback, maximum observed A/V skew 110.54 ms |
| Rapid-scrub regression | Failed: current untraced 284 ms skew, current traced 255 ms; pre-change executable also failed at 253 ms |
| Independent FFmpeg export check | Passed; source selections/cut order, black gaps, silent gaps, H.264/AAC and 9.666667-second duration verified |
| Release publish | Current source published to `artifacts/local/pointer-host`, zero compiler warnings/errors |

The existing A/V acceptance remains 150 ms; this task does not loosen it or alter
offline export timing. Cursor/hover work creates no editing revisions or undo entries.

## Presentation blocker: update (2026-11-17)

The ~250 ms waits were a 4 Hz compositor cadence for the whole run, not isolated stalls: every frame after the
first three swapchain images waited about 240 ms to acquire. Both the current and the pre-change executables
failed the same way in the same minutes, and both pass at other times on the same fixture (current: 35 ms median
scrub latency, 41-46 ms maximum skew, 0.03 ms acquire). The failing runs started about five minutes after a session
unlock, matching the 300 s display timeout, but forcing the monitor off, cloaking or off-screening the window, and
eight minutes of real idle did not reproduce the throttle; the trigger remains unproven.

Valencia no longer blocks on it. `VulkanNativeSwapchainPresenter.PollImage` acquires with a zero timeout into a fence
that persists across polls and reports Ready, NotReady or OutOfDate; `NativeVideoPresenter.Draw` skips the frame on
NotReady (captures still render) and records `SkippedFrames`. A/V skew is observed only for frames that reached the
display. The skip path has not been observed in a run yet, because no run since has been throttled.
`artifacts/local/claude-review/idle-qualify.ps1` waits for real idle time and runs the scrub smoke on both builds.

Every `seek-request` diagnostic now names its caller (`from=Member:line`). That attribution exposed a scrub-smoke
failure caused by a temporary diagnostic edit that also started the editor smoke; the edit is gone.

## Isolated existing presentation blocker (historical)

The recurring approximately 250 ms stall is now reproduced and phase-isolated.
In current trace frame 59, Draw took 241.91 ms, including 234.88 ms of swapchain
acquisition/fence waiting; layout took 1.47 ms and native UI preparation 0.50 ms.
The three GPU pass intervals were approximately 0.166, 0.438 and 0.071 ms.
The pre-change executable similarly spent about 257 ms acquiring an image.
Both traces have zero Vulkan validation errors. The underlying WSI/driver reason
is still unknown; this identifies a blocking phase, not proof that Machina layout,
the title bar, GPU shaders, or a particular driver setting caused it.

Reproduce with `--scrub-smoke` and `VALENCIA_RENDER_TRACE` pointing at a trace
path. Raw current/pre-change traces and logs remain under
`artifacts/local/pointer-scrub-traced` and `artifacts/local/pointer-baseline-scrub`.
Hashes, failed commands and stalled-frame details are in the validation JSON.
The comparison is an existing pre-change executable, not a rebuilt source
baseline. The current executable and automated suites were built from source.

The remaining seam is Copeland's
`Aurelian.Graphics/Vulkan/Presentation/AurelianVulkanSwapchain.cs:AcquireNextImage`
and `Vulkan/Native2D/VulkanNativeSwapchainPresenter.cs:TryPresent`.
The acquire path synchronously waits and destroys its local fence in `finally`.
A correct nonblocking version must retain an acquired image and unsignaled fence
between polls and handle resize/shutdown ownership. Shortening the timeout alone
could destroy a fence still in use. That graphics-lifetime change is not patched
into this interaction increment. No threshold was weakened or failed run discarded.

The first native run checked explicit pointer-event-to-Draw wall time, not optical
input latency. Physical pointer/keyboard, Snap flyout selection, mixed-DPI monitor
dragging and long resize stress remain manual checks. Static labels intentionally
remain arrows unless an application implements text selection.
