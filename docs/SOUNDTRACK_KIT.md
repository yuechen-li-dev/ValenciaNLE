# Soundtrack and transitions

Sound hits, track gain and transitions are ordinary edits in the same typed plan
used by Copeland, CLI and MCP. They can be reviewed on a private candidate,
saved, undone and replayed. Use `effects list` to discover the available
operations.

| Copeland builder | Edit-plan operation | Behavior |
| --- | --- | --- |
| `SoundHit(track, clip, asset, stream, at, sourceOffset, duration, gainDb)` | `soundHit` | Insert a source audio range on a sequence frame. Pass stable IDs and exact `long` ticks. |
| `TrackGain(track, db)` and `TrackEnvelope(track, envelope)` | `trackGain` | Patch the track level or its automation. Use `GainEnvelope()` and `Point(at, db)` to author the envelope. |
| `Transition(track, id, outgoing, incoming, before, after)` | `transition` | Add or replace a video dissolve or an equal-power audio crossfade at a shared cut. |
| `ClearTransition(track, id)` | `transition` with the nested transition omitted | Remove a transition. |

Sound hit source offsets are relative to the audio stream origin. Placement
must be on the sequence's frame grid. The first selected source sample is
mixed at the first output sample on or after that boundary, including on
fractional video frame grids. Source media and audio samples remain outside
the project. Sound hits appear as editable audio clips.

Track gain and envelope values add in decibels. The envelope interpolates
linearly in dB and holds its first and last values outside its keyed range.
An empty envelope clears automation while preserving the track level. Use one
track for the music bed to duck it independently of the original audio and
effects. For JSON or TSON plans, use canonical decimal strings for exact tick
fields and write an envelope point as `{ time: { ticks: "..." }, gainDb: -12 }`.

## Cut transitions

The clips must meet on one track. The cut and sequence duration stay in
place. The half-open window begins at `cut - before` and ends at
`cut + after`. A nonzero `before` needs an incoming source handle before its
in-point. A nonzero `after` needs an outgoing source handle after its
out-point. Valencia rejects unknown or insufficient handles and overlapping
transition windows on a track. The WAV importer records an implicit zero
origin when the demuxer supplies a finite stream duration.

The video dissolve interpolates the two composited SDR pictures on the
transition's track. Tracks above the dissolve and title overlays keep their
normal placement. Audio uses cosine and sine weights at the output sample
time. Existing clip fades, gain automation and the master ceiling still apply.
Very similar signals can add at the midpoint, so leave mix headroom.

Editing a cut removes transitions that lose valid adjacency or handles.
Track mute and visibility apply normally. If a participating clip is disabled,
the cut plays as an ordinary edit. Dry-run shows added, modified and removed
transitions and track gain changes alongside clip edits. Copeland and typed
plans edit the controls, and the native monitor plays the result.

## Bunny workflow

The [soundtrack example](../examples/soundtrack-kit.workflow.ts) adds a local
synth bed, two sound hits, a music duck around each hit, a dissolve and an audio
crossfade. The first hit matches the earlier montage's frame-97 punch-in.
The other lands at the frame-150 cut. The workflow starts from the 12-second
Bunny project with both picture and original audio cut at five seconds. The
right clips use original source seconds 8–15, so the bird shot dissolves into
the meadow. The demo synthesizes the bed and impacts locally at 48 kHz.

```powershell
Valencia.Cli workflow review examples/soundtrack-kit.workflow.ts --project bunny-cuts.valencia --start 762048000000 --end 1524096000000 --output review.mp4
Valencia.Cli workflow run examples/soundtrack-kit.workflow.ts --project bunny-cuts.valencia --save-as bunny-soundtrack.valencia
Valencia.Cli creator export bunny-soundtrack.valencia --output-dir exports
```

The workflow accepts `music`, `hit`, `nextVideo` and `nextAudio` as named
`--args` values. The latter two identify the right clips. Local demo assets
and the TSON plan that prepares the cut are under `artifacts/local/soundtrack-kit`.
*Big Buck Bunny* is credited in [Clip Effects 1](EFFECT_PACK.md#dogfood-source).

## Verification

- The full Windows Release suite passed 390 tests. The published build and
  final re-encode used the same source. The re-encode matched the independently
  decoded export byte for byte.
- Tests sample both sides of the dissolve and compare every pixel against
  independently evaluated source sides. The audio tests measure sine/cosine
  weights, track envelope levels and an impulse on a fractional video frame.
  At frame 2 of 30000/1001 video, that impulse begins at sample 3204 at 48 kHz.
  Other tests cover missing handles, undo, save/reopen, and sample-identical
  PCM seeks versus continuous decoding.
- Copeland candidate and committed sheets match byte for byte. The stdio
  MCP server independently rendered both sources at the cut, then validated
  and removed the picture transition in its live session.
- FFmpeg independently decoded the 12-second, 360-frame H.264/AAC portrait
  export. The decoded image strip shows the bird dissolving into the meadow.
  Comparing the audio against an encode with only the sound hits removed
  shows separate transients at frames 97 and 150.
- Native playback initially failed when WAV seeking re-packetized PCM before
  an indexed sample. Exact-anchor PCM seeks fixed that failure. A second run
  observed 233 ms A/V skew when the incoming video cursor first decoded, with
  238 ms for two-input evaluation. Playback now prepares one upcoming
  transition input in a half-second window. If playback starts within that
  window, it primes the input before the audio clock starts. On the RTX 3070
  rerun, maximum two-input evaluation was 22.0 ms and A/V skew was 43.1 ms.
  Reverse playback displayed all 16 dissolve frames. There were zero audio
  underruns and zero Vulkan validation errors.

The trace captures, prepared project and final media are in
`artifacts/local/soundtrack-kit/final`.
