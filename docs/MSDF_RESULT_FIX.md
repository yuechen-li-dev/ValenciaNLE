# MSDF typography and recoverable UI errors

2026-11-16. Current source in Valencia and the adjacent Copeland checkout; changes are not committed or pushed.

## Confirmed rendering defects

1. `Copeland/src/Machina.UI/Machina.Fonts/Generation/MsdfSharp/MsdfSharpDistanceFieldGenerator.cs`,
   `TryCreateProjection`, passed an atlas-pixel range directly to msdfgen's outline-coordinate distance mapping.
   Glyph fit scales differ, so apparent edge softness varied with glyph and size. The range is now
   `settings.PixelRange / appliedScale`; the public placement metadata remains in atlas pixels.
   The analytic rectangle test `EncodedDistanceRangeIsInAtlasPixelsRegardlessOfProjection` failed in all four
   configurations before this change and passes afterward (SDF/MSDF/MTSDF, 32/64-pixel fields).
   Two pixels inside an edge with an eight-pixel range now encode 0.75, independent of projection.
2. `AurelianMsdfTextPresentationAdapter.AdaptInto` normalized the vertical atlas UV convention **after**
   clipping. Cropping transparent padding from the top consequently removed the opposite texture edge.
   This moved visible glyphs by different amounts; Q's padding made the discrepancy particularly conspicuous.
   `AurelianGlyphRunAdapter` now supports normalization before clipping, selected by the native presentation
   adapter. Existing callers retain their default convention. No per-letter baseline offsets were added.
3. `AurelianNativeUiFont` ignored font-authored pair adjustments. The finite printable ASCII pair set is
   computed once with the atlas and supplied to the existing `DistanceFieldTextLayout`. Both measurement
   and drawing use those advances. There is no per-frame font parsing or pair-cache growth.

The Noto Serif Display ExtraCondensed Black Q also has an intentional descender. At 48 px, its outline bottom
is 11.376 px below the common baseline; E's bottom is zero. `FontMetricsTests` compares individual advances
and outline bounds against independent Skia font parsing (0.002 px tolerance) and compares run origins with
the existing direct-outline renderer, including `AVATAR`, `To WA`, and `SEQUENCE`.

`--typography-smoke` renders the actual Vulkan UI path and independently draws the same font outlines with
Skia at the qualified run origins. Before the UV correction, the SEQUENCE ink occupied rows 26..73 rather
than the reference's 23..69. Afterward both occupy 23..69; the measured word bounds match on all four edges.
The instruction line also matches all four edges; the AVATAR sample differs by at most one pixel.
This checks contour bounds, not pixel-identical antialiasing between two renderers. Small controls retain
the existing hinted raster path, and complex-script shaping is not added here.

## Confirmed crash and correction

Windows Application event 1026 at 2026-11-16 12:47 local time records:

```text
Machina.Layout.Diagnostics.LayoutError: AnchorFrame.Width must be non-negative.
FrameResolver.ResolveHorizontal -> MachinaPresentationPipeline.Prepare
-> EditorView.Prepare -> EditorWindow.Prepare -> GLFW resize callback
```

The unmanaged callback escape produced exit 0xc000041d. An empty 1x1 or 20x720 editor reproduces the
invalid layout before the fix: Header computes a negative six-button cell width. Two of six new resize
test cases failed with the same LayoutError. The layout resolver correctly rejected invalid input;
its validation has not been weakened.

Resize now cancels transient input and invalidates the view, without preparing layout in GLFW. The event
loop prepares later. A positive client area below 320x480 gets a noninteractive “Enlarge window to edit”
placeholder; a zero-sized window skips preparation. Restoring a usable size rebuilds the normal editor.
No project or playback state is reset. Keyboard, focus and scroll callbacks also use the guarded input
boundary. UI continuations report failures and allow subsequent queued work to run.

## Result and runtime

Valencia's SDK is pinned to 11.0.100 and its projects target .NET 11 / C# 15, including the Windows app and tests.
Published framework-dependent executables need the .NET 11 Desktop Runtime. Existing Copeland, Deliverance
and InputMan project targets are unchanged. The scripting host now declares its actual net11.0 host target;
the scripting ABI and editing wire contracts are unchanged. No packages were added.

`Valencia.Core/Result.cs` uses the compiler-supported C# 15 declaration:

```csharp
public union Result<T, E>(Ok<T>, Err<E>);
```

The actual declaration also supplies `Success`/`Failure` factories. Separate sealed record cases avoid
ambiguity for `Result<string, string>`. Consumers match Ok, Err, and the explicit uninitialized `null`
case; the default generated union is not success. Tests preserve Int64 values above binary64's exact range.
See the [language documentation](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/union).

`EditorOperation` translates legacy/third-party exceptions once at the application boundary. Layout
preparation, background UI jobs, native input and UI continuations consume typed results. Cancellations
become cancellation status, not an error dialog. A failed preparation retains the last successful frame
and hit-test tree and restores viewport configuration; repeated identical layout failures do not flood
the diagnostic log. A later valid preparation can succeed normally.

This is a bounded adoption, not replacement of every throw. Existing editing validation already returns
EditResult, and EditingSession transactions remain responsible for atomicity. Results do not roll back
arbitrary partial side effects. Fatal native/device failures, access violations, out-of-memory conditions
and programming failures during process teardown are not guaranteed recoverable. Third-party APIs can
still throw internally, and a debugger configured to stop on first-chance exceptions may still stop.

## Validation

- Full Release solution build: zero warnings, zero errors.
- Valencia: **300 passed, zero failed, zero skipped**: Core 76, Persistence 21, Programming 85, Media/UI 118.
- Affected shared suites: **364 passed, zero failed, zero skipped**: Machina.Fonts 311; Aurelian.Machina 53.
- RTX 3070 / driver 610.88, Windows, .NET 11.0.0: typography readback, ten resize sizes (including tiny,
  narrow and restored), four simulated logical/framebuffer scales, scrub, edited playback and direct M0 playback passed.
- Native scrubbing: 24 gestures / 2400 moves, 33.26 ms median and 41.00 ms p95 request-to-frame latency;
  41.98 ms maximum A/V skew, no audio underruns. Deliberately injected callback failure is reported and contained.
- Edited playback: 43.06 ms maximum A/V skew across the 9.667 s sequence; save/reopen semantic equality
  and live Copeland script integration passed. M0 maximum A/V skew 83.08 ms. Existing 150 ms contract is unchanged.
- Vulkan validation was available and reported zero errors in the native checks.
- Independent FFmpeg CLI decode verified the edited H.264/AAC export: 1280x720 at 30 fps, 48 kHz audio,
  duration 9.666667 s, retained source frames, cuts, black/silent gap and blue second source.

Native pointer events in these smokes are synthetic through the actual handlers; this run does not claim
a new physical-pointer/desktop-capture qualification. GPU images come from the application's own readback;
no Windows screen-recording session was started. Cold font construction is still a startup cost, not a
claimed playback performance improvement. Before/after logs, TRX files and images are under ignored
`artifacts/local/font-result-*`; the compact record is [validation/msdf-result-fix.json](validation/msdf-result-fix.json).

## Repeat

```powershell
dotnet build Valencia.slnx -c Release -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test Valencia.slnx -c Release --no-build -m:1 -nr:false --logger trx
dotnet publish src/Valencia.App -c Release -r win-x64 --self-contained false -o artifacts/publish/win-x64 -m:1 -nr:false -p:UseSharedCompilation=false
$app = './artifacts/publish/win-x64/Valencia.App.exe'
& $app --typography-smoke --output artifacts/local/font-result-final-typography
& $app --layout-smoke artifacts/local/fixtures/av.mp4 --output artifacts/local/font-result-final-layout
& $app --scrub-smoke artifacts/local/fixtures/av.mp4 --output artifacts/local/font-result-final-scrub
& $app --editor-smoke artifacts/local/fixtures/av.mp4 --output artifacts/local/font-result-final-editor
& scripts/VerifyEditorExport.ps1 -Directory artifacts/local/font-result-final-editor -SourcePath artifacts/local/fixtures/av.mp4
& $app --smoke artifacts/local/fixtures/av.mp4 --output artifacts/local/font-result-final-player
# From ../Copeland, build current source:
dotnet test tests/Machina.UI/Machina.Fonts.Tests -c Release -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test tests/Integrations/Aurelian.Machina.Tests -c Release -m:1 -nr:false -p:UseSharedCompilation=false
```
