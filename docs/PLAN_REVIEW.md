# Composited review and keyframes

Keep editing decisions in a Copeland `.workflow.ts` or typed `.obj.ts` plan. Review renders the proposed timeline against an identified project revision without touching the current edit, undo history or saved project. PNGs and MP4s are review artifacts. They can be regenerated from the checked-in workflow and its inputs.

## Review a candidate

Use the same command with a single sequence tick for a PNG frame, up to 12 comma-separated ticks for a contact sheet, or a half-open tick range for an MP4.

```powershell
Valencia.Cli workflow review examples/review-motion.workflow.ts --project input.valencia --at 812851200000,821318400000,838252800000 --output review.png
Valencia.Cli workflow review examples/review-motion.workflow.ts --project input.valencia --start 762048000000 --end 1320883200000 --output review.mp4
Valencia.Cli plan review edit.obj.ts --project input.valencia --at 821318400000 --output planned-frame.png
Valencia.Cli timeline review input.valencia --at 821318400000 --output current-frame.png
```

Select `--profile <id>` to see that candidate's output crop, including a new
profile created by the edit. The result records the starting and current
revisions, whether the starting snapshot is still current, and the exact
sample ticks with image rectangles and contributing video clip IDs. Review
renders from original media at the sequence or profile's composition
resolution and downscales the result. Video starts on the given sequence
tick using the profile frame rate, and its audio comes from the same edit.
Review video is limited to 30 seconds and 1,800 frames. The output's longest
edge defaults to 640 pixels and can be set between 64 and 960. Use a new
filename in an existing directory.

MCP `review_timeline` takes `projectId`, `sequenceId`, `revision`,
`output`, `selection`, and an optional validated `plan`. Output paths stay in
the server's workspace. It returns artifact metadata so a local client can
open the media. Trusted direct Copeland scripts can use
`api.ReviewFrame(plan, ticks, output, profileId, maximumEdge)`, or the
corresponding `ReviewContactSheet` and `ReviewVideo` calls. Pass an empty
profile ID for the sequence format. An isolated workflow produces review
media through the host's `workflow review` command. A separate
`workflow run --save-as ...` publishes its edits.

## Animate a clip or title

```typescript
const camera = plan.Keyframes();
camera.Key(0L, 0.5, 0.5, 1.0, 0.0, 1.0, "hold");
camera.Key(api.Frames(97L, 30, 1), 0.5, 0.51, 1.24, -4.0, 1.0, "ease");
camera.Key(api.Frames(118L, 30, 1), 0.5, 0.5, 1.0, 0.0, 1.0, "linear");
plan.ClipKeys(clip, camera);
```

Each key contains exact ticks, output-fraction X/Y, scale, clockwise
degrees, opacity, and interpolation for the segment starting at that key.
A channel holds its endpoint value outside the keyed interval.
`TitleKeys(id, curve)` uses the same builder. `ClearKeys("clip", id)` or
`ClearKeys("title", id)` removes the keyed layer. JSON and typed object
plans use `{ operation: "transform", target: "clip" | "title", targetId, curve }`
with canonical decimal strings for each key's `at` value.

Clip key times are authored relative to the current source in-point. The
stored anchor moves with the source so trimming or splitting a clip
preserves the phase of the animation. Replacing a curve anchors it to the
clip's current in-point. Title keys are relative to the title start. Keys
set placement and multiply existing opacity. The built-in entrance motion
continues to apply on top. Preview and final export share the evaluator.
Graded or masked rotation uses CPU sampling. Axis-aligned grading and
masks can use the existing Visual TS Vulkan effect.

The [review-motion workflow](../examples/review-motion.workflow.ts) expects
a video clip starting at sequence zero and long enough to reach frame 154
on a 30 fps grid. Its default revision cues the punch-in two frames earlier
and raises the title. Pass `--args '{"timing":"late"}'` to review the first
draft with the lower title and later punch-in.

## Validation

The Release suite passed 385 tests. The review tests cover exact fractional
frame selection, a title over a silent gap, unchanged undo history, stale
revisions, an existing output, cancellation, and clip animation through a
split and Deliverance round trip.

On the *Big Buck Bunny* excerpt, the first draft placed the title across the
bird's face. The revised Copeland workflow moved the punch-in from frame 99
to frame 97 and raised the title for both portrait and widescreen reviews.
The portrait contact sheet rendered from the candidate matched the committed
sheet byte for byte. A typed TSON plan, a direct Copeland script, and the
actual stdio MCP server also rendered byte-identical PNGs from the same plan.
Independent FFmpeg decoding found 66 frames and unchanged decoded audio in the
2.2-second portrait review clip. The published Windows application passed 24
seeks and reverse playback on an RTX 3070 with 41.3 ms maximum A/V skew, zero
audio underruns, and zero Vulkan validation errors.

The example footage and its attribution are described in [Clip Effects 1](EFFECT_PACK.md#dogfood-source).
Review media and raw validation evidence are under `artifacts/local/plan-review`.
