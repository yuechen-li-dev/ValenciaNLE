# Animation presets

Valencia renders timed titles and clip motion through the same sequence evaluator
for scrubbing, playback and export. Use `effects list`, MCP `effects_list` or
`ScriptApi.ListEffects()` for preset discovery. Use the ordinary versioned edit
plan for validation, dry-run, commit and undo. The dry-run reports before/after
title cues under `titles` and clip motion under the affected clip's properties.

| Effect | Presets | Controls |
| --- | --- | --- |
| Timed title | `static`, `fade`, `slideUp`, `pop`, `impact` | Text, position, font size, RGBA fill/outline, exact start/end, entrance and exit durations |
| Video clip entry | `punchIn`, `shake` | Exact duration through ten seconds and strength from 0 to 1 |

Titles use a single line of the bundled Archivo Expanded Bold face with an
outline. They are composited above picture and captions in output-relative
coordinates, so the same cue can be used in a portrait output. Keep the text
within the font's glyph coverage. The renderer uses a fixed face and simple
single-line layout. Multiple cues may overlap and draw by ascending start time,
then ID. Titles are anchored at exact sequence times and use half-open
`[start, end)` ranges. A title is placed inside the sequence's media duration
and can fill a gap. Later clip moves, cuts and ripple edits leave the title at
its sequence time. If a project is shortened, rendering ends at the media
duration. Existing project files load with an empty title layer. Saved titles
and clip motion require this build or newer.

Clip motion modifies the existing video geometry without changing source
selection, grade, masked regions or audio. The entry begins at the clip's
current start. A split starts a separate entry on the right-hand segment.
A short clip clamps the configured motion duration to its length.

## Copeland and plans

The runnable [montage workflow](../examples/montage-titles.workflow.ts) sets
three title cues, cuts the first video clip and applies a punch-in and a shake
with one edit plan. Start with a project containing a clip from time zero for
at least 10⅓ seconds. The workflow uses a 30 fps timing grid and accepts a
`reaction` argument for the impact title. Use CLI preview to inspect the diff.

```powershell
Valencia.Cli workflow preview examples/montage-titles.workflow.ts --project input.valencia
Valencia.Cli workflow run examples/montage-titles.workflow.ts --project input.valencia --save-as montage.valencia
Valencia.Cli export montage.valencia --output montage.mp4
```

Copeland uses native `long` ticks for `Title`, `StyledTitle` and
`ClipMotion`. Use `RemoveTitle` or `ClearClipMotion` to remove them.
`ScriptApi.Titles(sequenceId)` lists saved cues. In JSON or TSON plans, use
canonical decimal strings for `start`, `end`, `entranceTicks`,
`exitTicks` and `motion.durationTicks`. Fill and outline in a plan are
packed 32-bit RGBA numbers. MCP clients can read sequences and their title cues
with `timeline_query` and use `validate_edit_plan` and `apply_edit_plan`.

## Native timeline menu

Right-click a timeline clip for split, delete, ripple delete or seek. Right-click
the ruler or a gap for seek and marker placement. Use the mouse or the arrow
keys and Enter to choose an action. Escape or an outside click dismisses the
menu. The reusable Machina `WidgetBuilder.ContextMenu` renders as a separate
native overlay and follows the Windows menu-animation preference. Its host API
is documented in Copeland's `docs/Machina.UI/native-widgets.md`.

The montage demo is derived from *Big Buck Bunny* © 2008 Blender Foundation,
CC BY 3.0. It uses the same [attributed 1080p30 excerpt](EFFECT_PACK.md#dogfood-source)
as the first effects pack and preserves its audio.

## Validation

- The full Valencia Release run passed 381 tests. Title tests compare decoded
  export frames against evaluation at exact 30000/1001 frame times, including
  the black frames before and after a title over a gap. Tests also cover undo,
  plan diffs, Deliverance round trips and clip motion over an existing grade.
- The Machina Release run passed 892 tests with one filter. The existing
  whole-checkout font dependency scan
  `RenderBridgeBoundaryTests.ProjectFiles_DoNotIntroduceForbiddenFontDependencies`
  timed out in a separate 30-second hang probe.
- Native RTX 3070 editor smoke checked the menu's eased placement, hovered
  cursor, Escape dismissal and undoable marker selection. Preparation took
  33.97 ms cold and 1.17/0.65 ms for the two warmed samples. This measures
  overlay preparation on the host thread. Vulkan reported zero validation
  errors or warnings.
- Copeland CLI preview and commit agreed on the montage. Actual stdio MCP
  calls discovered the effects and validated and applied a title removal.
  Independent FFmpeg decoding verified the 360-frame 720×1280 H.264/AAC
  export and matching decoded PCM against the untreated sequence. Native
  forward/reverse playback of the saved montage passed 24 seeks with 38.9 ms
  maximum A/V skew, zero audio underruns and zero Vulkan validation errors.

The demo and run evidence are under `artifacts/local/title-demo`.
