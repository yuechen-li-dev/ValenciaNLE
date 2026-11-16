# Native text alignment and rendering correction

2026-11-15. Applies to Valencia and the adjacent Copeland/Machina/Aurelian source checkout.

## Confirmed cause

The text rectangles and glyph quads could be correctly centered while the **rendered pixels were not**. The shared vertex shaders added:

```typescript
Convert<f32>(builtins.vertexId + builtins.instanceId) * 0.000001
```

to clip-space X. The resulting horizontal shift is that value multiplied by half the framebuffer width. Later glyphs in a batch therefore drifted farther, and wider windows amplified the drift. The textured pass used for raster glyphs and the analytic widget pass had the same problem, with different vertex counts. It also moved supposedly pixel-aligned raster glyphs onto fractional pixels, forcing interpolation and making them look rough/blurred.

An actual Vulkan readback regression reproduced UNDO two framebuffer pixels off-center despite centered layout. Removing the bias made it pass. The earlier `UI_SCRUB_STABILIZATION.md` tests checked CPU geometry and missed this later GPU transform; that qualification was insufficient for visual alignment.

## Corrections

- Removed positional bias from `MsdfText.v.ts`, `AnalyticShape2D.v.ts`, `ProfileMsdf.v.ts`, and the `ForwardTexturedM3.v.ts` source Valencia actually uses. Vertex/instance builtin coverage now lives in a dedicated compiler probe that changes sample UVs; production geometry does not need artificial offsets.
- Standard Machina buttons and badges explicitly fill their label region using `TextNode.FillAvailableSpace`. Removed presentation's `.label`/`.label-region` naming heuristic. Named, anonymous, and scoped buttons now have the same real layout bounds. Existing `UI.Text` signatures and intrinsic sizing remain intact.
- Shared native MSDF qualification centers actual ink bounds instead of advances/approximate line boxes. Machina glyphs use the encoded 0.5 contour; game-specific weight tuning does not silently alter UI font shapes.
- MSDF antialiasing follows the existing CPU one-pixel coverage policy. `VulkanOrderedQuadRenderer.BuildVertices` converts logical field scale to framebuffer scale. A trial differentiating the median distance was rejected for unstable tiny-glyph corners; no experimental coverage-transfer/material fields were retained.
- Valencia keeps hinted raster rendering for Xs/Sm/Md (10/12/11 px), now genuinely pixel-aligned, and uses the shared MSDF path for Lg and larger. All-MSDF small-text trials looked worse than the corrected hinted path; this is a deliberate hybrid, not a claim that all text now uses MSDF. Removed the old local dark-MSDF stroke expansion. No font family, package, editor command or document/export semantics changed.
- Regenerated the three packaged shader artifacts, so the release executable uses corrected precompiled programs.

## Validation

[Machine-readable evidence](validation/ui-text-rendering.json) includes test counters, shader hashes and measured pixel offsets. The native `--layout-smoke` uses the application's Vulkan readback, not Windows screen capture. It compares actual contrasting pixels against authoritative button/hit-target bounds, including UNDO, REDO, PLAY, OUT, arrows and VIEW / SEQUENCE where present. It includes antialiased edges at 20% foreground/background contrast, requires at least 50% peak contrast, and rejects displacement over **1.5 framebuffer pixels** on either axis. It does not merely test label rectangles or file existence.

- 284 Valencia tests pass: Core 73, Persistence 21, Media/UI 105, Programming 85; zero failures/skips.
- 796 Machina fast-lane tests, 50 Aurelian/Machina integration tests, 351 graphics tests and 460 shader tests pass. The shader lane uses the already-installed Naga CLI via a process-local `AURELIAN_NAGA`; nothing was installed.
- Native RTX 3070 validation covers six window sizes (1280x720 through 2560x1440, plus tablet/phone-shaped windows and restore), 5,000-clip culling, source/selection/proposal states, and both small hinted and larger MSDF controls at simulated 100%, 125%, 150%, 200% framebuffer scales. These are real GPU scale tests, not a claim of changing physical monitor DPI or mobile OS support.
- Native playback, scrubbing, pause/resume, resize, edited playback, save/reopen and independently decoded edited H.264/AAC export pass. A/V qualification retains the existing 150 ms limit. Vulkan validation was available and reported zero errors.

After-correction native capture: `artifacts/local/text-quality/release-layout/desktop.png`. Full layout/scale pixel measurements: `artifacts/local/text-quality/release-layout/result.json`.

## Capture cleanup

Reset the previous computer-use kernel and stopped the identified stale Codex capture helper (PID 30536). Codex launched a fresh shared helper; its mere existence is not evidence of ongoing recording. The user confirmed that the yellow border disappeared. Snipping Tool and the user's editor sessions were left intact. Subsequent render checks use only Valencia-owned GPU buffers; no desktop screenshot session is needed.

## Repeat and use

```powershell
dotnet build Valencia.slnx -c Release -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test tests/Valencia.Tests -c Release --no-build
dotnet publish src/Valencia.App -c Release -r win-x64 --self-contained false -o artifacts/publish/win-x64 -m:1 -nr:false -p:UseSharedCompilation=false
& artifacts/publish/win-x64/Valencia.App.exe --layout-smoke artifacts/local/fixtures/av.mp4 --output artifacts/local/text-quality/release-layout
& artifacts/publish/win-x64/Valencia.App.exe --editor-smoke artifacts/local/fixtures/av.mp4 --output artifacts/local/text-quality/release-editor
& scripts/VerifyEditorExport.ps1 -Directory artifacts/local/text-quality/release-editor -SourcePath artifacts/local/fixtures/av.mp4
```

In Copeland, run `dotnet test Machina.UI.slnx -c Release -m:1 -nr:false -p:UseSharedCompilation=false`, the `Aurelian.Machina.Tests`, `Aurelian.Graphics.Tests` and `Aurelian.Shaders.Tests` projects. For shaders, set `AURELIAN_NAGA` to `artifacts/local/webgpu-tools/bin/naga.exe` first. Test/result files remain under each repository's ignored artifacts directory.

Restart/rebuild an existing Debug editor, or launch `artifacts/publish/win-x64/Valencia.App.exe`. Changes span both repositories and have not been committed or pushed in this task. The historical scrubber termination remains a separate, unisolated issue; the current scrub smoke and diagnostic boundary continue to pass.
