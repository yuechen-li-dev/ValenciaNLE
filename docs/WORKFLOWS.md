# Copeland TS editing workflows

Use the **Script** button in the media panel and pick a `.workflow.ts` or
`.workflow.tsx` file to run an editing job on the active sequence. The picker
accepts both Copeland TS extensions. Files without the workflow suffix use the
existing direct script path.

The CLI accepts a Copeland TS entry file without requiring a workflow suffix. This example adds
markers every five seconds using named arguments, a generic helper, an enum
with a payload, `match`, conditionals and a bounded loop.

```powershell
$cli = '.\artifacts\local\workflow-cli\Valencia.Cli.exe'
& $cli workflow preview examples/chapter-markers.workflow.ts --project recording.valencia --args '{"mode":"short","label":"Chapter"}'
& $cli workflow run examples/chapter-markers.workflow.ts --project recording.valencia --args '{"mode":"short","label":"Chapter"}' --save-as chaptered.valencia
```

`examples/portrait-assembly.workflow.ts` shows a second pattern. It places an
existing media asset in a new vertical sequence and returns the new sequence ID.
Pass that ID to `export --sequence` after saving the workflow result.

The host compiles through Copeland MIR, the C# backend and Roslyn. The default
entry is `function run(api: ScriptApi): string`. Pass `--entry` to select
another named function or `--sequence` to select a timeline. The desktop
supplies its active sequence. The CLI defaults to the first sequence.
`--revision` accepts the exact decimal project revision.

Inside a workflow, `Argument(name, fallback)` reads a named string and
`SelectedSequence()` returns the selected stable ID. Use the existing
`Plan`, `Validate`, `Apply`, and analysis methods for editing logic.
`CreateSequence` accepts explicit video dimensions, rational frame rate and
audio sample rate. `InsertAsset` places registered media and uses the
same stream placement rules as the editor. Markers are accessible with
`AddMarker`, `Markers` and `RemoveMarker`. All timeline ticks can cross the
ABI as native `long`; marker JSON uses decimal strings.

Preview executes the workflow against a private session and returns its summary
and whether the document would change. Run executes it again, then publishes a
validated document only if the original live snapshot is still current. A
successful publish increments the live revision once and creates one undo
entry. Errors, observed cancellation and revision conflicts leave the live
project untouched. Workflows that generate IDs or consult changing inputs can
produce different IDs or results when run again. The CLI writes a project only
when an explicit `--save-as` succeeds.

The workflow facade stages document edits. Analysis may populate derived caches.
Use separate CLI commands or the direct script entry for project replacement,
export, save and OBS control. Copeland code runs with CLR privileges, so only run
local code you trust. Call `Checkpoint()` in long loops to observe cancellation
between engine calls. This entry runs one workflow per host at a time.

**Ctrl+N** creates a separate sequence with the current output format and
standard tracks. **Ctrl+Tab** cycles sequences. Switching resets transient
transport and selection state. **M** adds a marker at the program playhead.
**Shift+M** and **Ctrl+Shift+M** seek to the next or previous marker.
Markers have stable IDs and fixed sequence timestamps. Trimming or ripple
edits leave their coordinates fixed. Markers beyond the current end become
navigable again when the sequence grows past them. OBS highlight markers use
**Alt+Shift+M** and remain attached to recordings. **J** still pauses with an
unsupported message. Reverse transport needs a separate playback implementation.

## Verification

The Release suite passed all 359 tests. The RTX 3070 desktop smoke ran a
`.workflow.tsx` file, undid its edits together, navigated a marker, created and
switched sequences, and completed the existing edited playback and export checks.
The published CLI previewed and committed the portrait assembly, saved and reopened
the result, and exported a six-second 1080×1920 H.264/AAC MP4. FFprobe verified the
streams and FFmpeg decoded the whole file. The input project hash stayed the same.
