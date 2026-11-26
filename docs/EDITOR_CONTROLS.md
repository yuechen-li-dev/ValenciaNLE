# Native effect controls and retiming

Park the Program playhead inside a video clip and select it on the timeline.
Use the inspector to edit position, scale, rotation and opacity at the current
frame. **Set key** records the evaluated pose, and **Prev key** and **Next key**
seek between keys within the clip. The **Controls** tab opens the inspector
when the layout has no separate inspector column. An audio clip selection
offers clip gain in one-decibel steps.

Drag the crosshair on a selected video clip in the Program monitor to change
its position. A title displays its own crosshair while paused. Click that
crosshair to select the title and drag it to place it. The orange handle marks
the proposed position while dragging. Release to update the picture and record
one edit. Clip transforms retain their source anchor through trims and splits.

Right-click near an abutting cut to add a dissolve or equal-power audio
crossfade. The default tries up to four frames per side, using available
source handles. Drag an orange edge to resize a transition without changing
the cut. Right-click an edge to remove the transition. Rejected resize
proposals display the validation message in the status bar.

Right-click an audio track to add a gain point, or right-click an existing
point to remove it. Drag a cyan point horizontally to retime it and vertically
to change its level. The gain curve follows exact sequence ticks and operates
on the entire track. The clip inspector's gain adjustment affects one clip.
Dropping a point on another point's frame fails validation and preserves both.
Escape, lost capture, window resize and focus loss cancel unfinished gestures.

## Speed and freeze frames

The video clip inspector offers **0.5×**, **1×**, **2×** and **Freeze**.
**Freeze** holds the selected source frame for the clip's current length.
Trim its edges to shorten or extend the hold. Copeland can supply an explicit
hold duration and any rational speed in the supported 1/8×–8× range.

Speed changes keep the clip start and source range. Derived timeline duration
and split or trim boundaries must land on exact sequence ticks. Evaluation
rounds the rational source offset down to a tick and selects a decoded frame
using actual source presentation timestamps. A hold maps every time in the
clip's half-open timeline interval to one source position.

Retiming changes video only. Other edits, audio, title cues and automation stay
at their existing times. A shorter clip can leave a picture gap. A longer clip
that overlaps another clip on its track is rejected. Remove an existing
transition before retiming either of its clips. Transitions and the automatic
rough-cut, transcript-editing and creator-source planners currently require
original-speed footage. Frame interpolation and audio stretching are deferred.

## Copeland workflows

The native controls commit the same Core operations as typed plans. Use
`TitleKeys`, `ClipKeysPreservingOrigin`, `ClipGain`, `TrackEnvelope` and
`Transition` for the corresponding edits. `ClipKeysPreservingOrigin` keeps
the earlier of the existing anchor and current source in-point. Use
`plan.Speed(clipId, numerator, denominator)`,
`plan.Freeze(clipId, sequenceTicks, durationTicks)`, or
`plan.Speed(clipId, 1, 1)` to restore original speed.

[The Bunny controls workflow](../examples/editor-controls.workflow.ts)
reproduces the three native edits on the scored Bunny montage from the
[soundtrack kit](SOUNDTRACK_KIT.md). The
[retime workflow](../examples/retime.workflow.ts) clears the montage's video
dissolve, sets the first clip to 2×, and freezes the second clip at frame 230.
The example leaves the audio cues and their levels in place.

```powershell
$project = 'artifacts/local/soundtrack-kit/final/bunny-soundtrack.valencia'
$out = 'artifacts/local/editor-controls'
& src/Valencia.App/bin/Release/net11.0-windows/Valencia.App.exe --effect-controls-smoke $project --output "$out/bunny-native"
& src/Valencia.Cli/bin/Release/net11.0/Valencia.Cli.exe workflow run examples/editor-controls.workflow.ts --project $project --save-as "$out/bunny-script/script.valencia"
& src/Valencia.Cli/bin/Release/net11.0/Valencia.Cli.exe creator export "$out/bunny-script/script.valencia" --output-dir "$out/bunny-script/export"
& ffmpeg -v error -i "$out/bunny-native/native.mp4" -map 0:v:0 -map 0:a:0 -f streamhash -hash sha256 -
```

The RTX 3070 smoke uses native pointer dispatch for a title drag at frame 115,
a dissolve trim from frame 142 to 146, and a music duck moved from frame 150
to 155. It checks cancellation, key navigation, one revision per drag, and
undo, redo and save/reopen. FFmpeg independently decoded the native and
Copeland exports. Their 360 video frames and 48 kHz audio had matching decoded
SHA-256 hashes. The separate retime export kept the original soundtrack hash.
Decoded samples confirmed 2× source selection, a black picture gap, the title
over that gap, and a held picture from the selected source frame.
The native smoke also presses the inspector's **2×** and **Freeze** buttons
and checks decoded preview seeks. See the
[validation record](validation/editor-controls.json) for commands and hashes.
