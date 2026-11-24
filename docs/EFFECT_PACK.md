# Clip Effects 1

`valencia effects list`, MCP `effects_list` and Copeland `ScriptApi.ListEffects()` return the same versioned catalog. CLI and MCP retain the standard edit-plan validation, revision checks, dry-run and atomic commit paths.

| Edit-plan operation | Parameters |
|---|---|
| `color` | `clipId` and a `color` object. Omit any color parameter to leave it at its current value. Exposure is measured in stops, contrast and saturation are multipliers, and temperature is normalized warmth. |
| `region` | `clipId` and an optional `region` object. Set `mode` to `blur`, `pixelate` or `cover`. Set `left`, `top`, `right` and `bottom` in source fractions from 0 to 1. Omit `region` to clear it. |
| `clipMotion` | `clipId` and optional `motion` with `preset` (`punchIn` or `shake`), exact `durationTicks` and optional `strength` (0 to 1). Omit `motion` to clear it. |
| `title`, `removeTitle` | Add, replace or remove a title by stable ID. See [animation presets](ANIMATION_PRESETS.md) for authoring and exact timing. |

Each video clip carries one static rectangular region. Split a clip to change its effect partway through. The rectangle applies to the source before the output crop or reframe. Left and top round down to source pixels, right and bottom round up, and the resolved edges are half-open. `size` is the tent-filter tap spacing for blur or the block size for pixelation, from 1 to 64 original source pixels. Each axis scales with the proxy resolution. Blur samples stay inside the region. Cover substitutes black and then respects the clip's opacity and fades. Use cover at full opacity for concealing source content and check the entire export when the target moves.

### Copeland workflow

```typescript
using Valencia.Scripting;

function run(api: ScriptApi): string {
    const sequence = api.SelectedSequence();
    const clip = api.FirstClipOnTrack(sequence, api.FirstTrack(sequence, "video"));
    const plan = api.Plan(sequence);
    plan.BasicColor(clip, 0.18, 1.08, 1.1, 0.35);
    plan.Region(clip, "cover", 0.25, 0.4, 0.75, 0.58, 8);
    return api.Apply(plan);
}
```

Save as `effects.workflow.ts` and use `workflow preview effects.workflow.ts --project input.valencia` to inspect the diff. Use `workflow run effects.workflow.ts --project input.valencia --save-as output.valencia` to commit and save. In a script, `ClearRegion(clip)` removes the region and `BasicColor(clip, 0, 1, 1, 0)` resets the grade.

### Rendering

The sequence evaluator resolves the clip, geometry and source-sample map used by both backends. Captions are composited after the picture. The default software path works headlessly. Set `VALENCIA_EFFECTS_BACKEND=vulkan` before starting Valencia to enable the Aurelian compute path in both the editor and CLI. The effects worker reuses its pipeline and mapped buffers and waits for its own command to finish before reading back pixels. Existing decoded RGBA frames, monitor uploads and FFmpeg encoding retain their ownership and timing. GPU device setup is paid on the first affected frame of an evaluator, and this path transfers each processed frame through host memory.

`ClipEffects.v.ts` is the authored shader. Regenerate the embedded SPIR-V artifact with `Valencia.Cli effects prepare-shader src/Valencia.Media/Assets/ClipEffects.json` from a source build with DXC and `spirv-val`. Normal use loads the embedded artifact by source hash.

### Dogfood source

*Big Buck Bunny* © 2008 Blender Foundation, CC BY 3.0. Used the Renesas 1080p30 H.264/AAC excerpt, then trimmed the original excerpt to seconds 15–27, reframed it to 720×1280, and used Copeland to add color and three regional treatments. Source SHA-256 `F03E635F0D24CDD8B7B482758B16F14EB0CBFA1056C23611A3A72CF3FF039CED`. [Blender attribution](https://peach.blender.org/about/) · [Renesas excerpt and license](https://github.com/renesas-rz/media/blob/main/Big_Buck_Bunny/README.md)

### Validation

- All 370 Release tests passed. Copeland dry-run and commit produced the same edits. A separate stdio MCP session discovered the catalog, dry-ran a partial color change and region replacement, then committed both.
- RTX 3070 native readback compared 36 pre-encode frames. Maximum CPU/Vulkan difference was one channel value out of 255. Median sampled evaluator times were 9.4–13.8 ms for software and 3.2–3.6 ms for Vulkan across the three effect treatments. The first group took 80 ms on software and 159 ms on Vulkan for its first frame, including opening and seeking the source. Native graphics and the effect device reported zero Vulkan validation errors.
- Independent FFmpeg decoding verified both 12-second H.264/AAC exports. Each had 360 portrait frames, the audio PCM hashes matched, and a 96×64 interior sample from the cover was entirely black. Decoded CPU/Vulkan picture PSNR averaged 58.44 dB.
- During the first effects qualification, the original long-GOP source exceeded the 150 ms reverse A/V threshold in both the pre-effects build at 158 ms and the software-effects build at 169 ms. The Vulkan-effects run passed narrowly at 149 ms. Adding the 640×360 all-intra proxy brought the Vulkan run to 46.5 ms maximum skew and 62.8 ms p95 broad seeks, down from 213.9 ms with the original in that run. Export used the original. The subsequent [reverse decode-ahead qualification](REVERSE_PLAYBACK.md) used the original footage.

### Framing and source coordinates

`outputFraming` edits one output profile. Create it with `profileId`, `name`, `width` and `height`, or patch an existing one by ID with just the fields to change. `fit`, `focalX` and `focalY` control the fit and crop alignment between its edges. New profiles use the sequence frame rate, full sequence range and current caption height. Existing profiles keep their export window, frame rate, caption position and animation keys. Framing keys continue to drive animated alignment when present. Dry-run reports the before and after profiles under `outputProfiles`.

Copeland workflows can use `plan.OutputFraming(profileId, "Portrait", 720, 1280, "fill", 0.55, 0.5)` and `plan.OutputFocalPoint(profileId, 0.52, 0.5)` on the same plan as the clip effects. Caption, audio and other profile settings are preserved.

`valencia source frame <project> --asset <id> --stream 0 --at <ticks> --output <new.png>` writes an ungraded thumbnail of the original source. `--at` is an exact offset in ticks from the stream start. The result includes the decoded presentation timestamp and both image sizes. Add `--x` and `--y` to convert thumbnail pixel-edge coordinates to normalized source coordinates usable in a region effect. For example, `(160, 90)` in a `640×360` thumbnail maps to `(0.25, 0.25)`. MCP `source_frame` uses the same coordinate contract and writes the PNG to a workspace-relative path.

The framing update passed all 372 Release tests. Copeland preview and commit agreed on the portrait profile update, and independent FFmpeg decoding confirmed the new 720×1280 crop on the 12-second excerpt with identical decoded audio. CLI and MCP produced matching thumbnail hashes and normalized points. MCP also validated an output-profile patch and rejected a thumbnail path outside its workspace.

The source, project, Copeland workflow, independent verification and native traces are under `artifacts/local/effects-dogfood/` in the qualification workspace. Run `Valencia.App --effects-smoke portrait-effects.valencia --output <directory>` to sample a saved effects project on both backends.
