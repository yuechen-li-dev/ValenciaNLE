# M1a — headless editing core

M1a implements a persistent editing document and logical timeline evaluation. The native player remains M0's single-source player. There is no timeline GUI or edited-video export in this milestone.

## Layout and ownership

- `src/Valencia.Core/Timeline`: immutable records/`ImmutableArray` collections, exact time arithmetic, validation, read-only queries and evaluation plans. Core has no UI, graphics, codec, storage or audio-device dependencies.
- `src/Valencia.Core/Editing`: typed semantic operations, versioned command envelopes and `EditingSession`. A session owns a lock, current snapshot and bounded snapshot undo/redo lists. No general event bus or event-sourcing framework was added.
- `src/Valencia.Persistence`: a narrow Core → Deliverance adapter. The App references this project; Core does not reference Deliverance.
- `src/Valencia.Media/MediaAssetProbe.cs`: independent import adapter reading original FFmpeg stream timestamps/timebases and metadata. M0's floating-point display metadata is not used to construct authoritative edit times. Unknown stream bounds stay unknown; container-duration estimates are not invented as stream bounds.
- `samples/Valencia.Headless`: executable acceptance demonstration requiring no media, GPU or audio runtime. Its generated source is explicitly a metadata fixture, not playable video.

The original five application projects remain, with one storage integration project added to preserve the Core boundary. No upstream source edits or additional NuGet dependencies were needed.

## Document and time invariants

`ProjectDocument` stores identity, application schema, name, assets and sequences. Assets store normalized absolute paths in memory, container identity and video/audio stream metadata. Stable stream identity is `(asset ID, stream index)`; stream timebases and optional bounds are retained verbatim. Sequence settings include dimensions, rational frame rate and audio sample rate. Tracks have kind, contiguous zero-based order, name and lock/mute/visibility state. Clips have stable identity, asset/stream reference, timeline start, source in/out, name, enable, opacity and linear gain metadata.

Object GUIDs are caller-supplied, nonempty and globally unique within the document. Commands do not generate object identities, read media files or consult wall-clock time. Collections exposed by snapshots/queries are immutable. `DocumentSemantics.Equal` compares persisted semantic fields deeply, including original clock representations; runtime revision/history are separate.

Timing rules:

1. All clip and query ranges are **half-open `[start, end)`**. At a split boundary only the right clip is active. At sequence end no clip is active.
2. `SequenceTime` uses checked Int64 ticks at **254,016,000,000 ticks/second**. Standard 24/25/30/50/60, 24000/1001, 30000/1001, 60000/1001 frame grids and common audio sample grids fit exactly. Sequence configuration rejects a frame/sample grid whose individual unit cannot be represented exactly.
3. `SourcePosition` is an original `MediaTimestamp` plus a signed `SequenceTime` offset. This preserves the original numerator/denominator even when a split falls between original source ticks. A large original source epoch need not fit into an absolute sequence timestamp; exact differences can still fit.
4. Clips run at **1× speed**. `duration = sourceOut − sourceIn`, `end = start + duration`, `source(t) = sourceIn + (t − start)`. Source→timeline is the inverse inside `[in, out)` and requires the resulting offset to be representable on the sequence grid. Source positions must use the referenced stream's original timebase. Clip durations must be positive and exactly representable; source bounds are enforced independently wherever known.
5. Edits do not automatically snap to nominal FPS or audio samples. They accept any legal sequence tick. `TimelineTime` offers explicit frame/sample snapping with exact, toward-zero, floor, ceiling or nearest/ties-away-from-zero policies. Exact is the default for editing conversions. Off-grid exact conversions fail, rather than silently changing timing.
6. Arithmetic intermediates use standard .NET `BigInteger`; sequence values and source offsets narrow with checked overflow. Overflow rejects the entire edit. M0's original toward-zero `MediaTimestamp.ToSequenceTime`/`SequenceTime.FromSamples` APIs remain for compatibility; the editing model uses the strict `TimelineTime`/`SourcePosition` path.
7. Sequence duration is the greatest clip end across all tracks, including muted/hidden/disabled clips; empty duration is zero. Gaps and independent overlapping tracks are valid. Clips within a single track are sorted and cannot overlap. Negative timeline starts and zero/negative clip durations are invalid.

## Editing and transactions

| Operation | Exact M1a behavior |
|---|---|
| Add asset | Register supplied metadata/reference; no source file is changed or decoded |
| Create sequence | Add explicit video/audio settings with an initially empty track collection |
| Add track | Insert at the requested order and renumber subsequent track orders |
| Insert clip | Place into a gap; no implicit insert shifting or overwrite |
| Move clip | Set explicit start and destination track within the same sequence; preserve source range/ID |
| Trim clip | Explicitly replace timeline start and source in/out; no inferred anchor or neighbor movement |
| Split clip | Strictly interior sequence timestamp; left retains ID, supplied right ID is new; exact duration/continuity preserved |
| Delete clip | Remove only that clip; surrounding clips stay in place |
| Ripple delete | Remove a positive range inside sequence duration on explicitly listed tracks; close that full range on those tracks |
| Set track state | Set lock/mute/visibility; allowed on locked tracks so they can be unlocked |

Every clip edit checks locks, including both sides of cross-track moves and every targeted ripple track. Unselected locked tracks do not block ripple. State changes are the explicit exception to track locking. Mute applies to audio evaluation; visibility applies to video evaluation. Other state fields remain metadata.

Ripple splits a clip spanning both range boundaries into two source-discontinuous fragments, now adjacent on the timeline. Its left fragment retains the ID; the caller must supply the right fragment ID through `RippleSplitId`. A surviving single fragment retains the original ID. Fully enclosed clips disappear. Later clips shift by exactly the removed range duration, including when part of the range is already a gap. Duplicate/unknown targets, missing/unused fragment mappings and invalid ranges fail. No unselected tracks move.

`EditCommand` carries identity, schema version, typed operation and optional expected revision. Standalone commands require an expected revision; transactions carry a required starting revision and may contain children with no revision or that same starting revision. Child revisions do not increment inside a transaction. Request/command schema is currently 1.

`EditingSession.Execute` serializes commits. It validates each candidate after each command and commits exactly once after the entire transaction succeeds. Intermediate invalid documents are not allowed. Failures return an `EditResult` with code/message, failing child index where applicable, unchanged snapshot and current revision. Successful standalone edits/transactions increment revision once. Empty/unsupported requests, stale revisions and net-no-change transactions fail without history changes.

Undo and redo use `SessionRequest` with identity/schema/expected revision. They restore semantic documents while incrementing the live revision, preventing revision ABA within a session. A transaction creates one undo entry. Failed edits preserve redo; a new committed edit clears it. History defaults to 100 entries, is configurable (including zero), uses copy-on-write document snapshots and is not persisted. Command IDs identify requests; they are not durable deduplication/idempotency keys.

## Persistence and recovery boundary

`ProjectFile.SaveAsync` captures one coherent immutable snapshot and stores it as a required `valencia.project` module through **DeliveranceService.SaveAsync**. The canonical `.valencia` file uses Deliverance container format 2, application/module/document schema 1, SHA-256 payload integrity and a small application JSON serializer (ID 42). Integer timestamps/offsets and decimal properties are serialized directly; derived floating-point seconds are excluded. JSON inside the container is inspectable, but a standalone JSON dump is not the canonical save path.

Deliverance's **FileSaveStore** owns path locking, temporary files, flush-to-disk, replacement and one previous-file backup (`.bak1`). Valencia does not recreate atomic file writing. Saves store references/metadata only, never video, frames, audio samples, proxy/cache or GPU state. Paths are stored relative with `/` separators where the platform can express that relationship; references on another Windows volume may remain absolute. Reopening resolves against the project file's directory, enabling folder relocation. A source reference outside the project folder may contain `..`; source paths are references, not a sandbox. Cross-volume external references still require accessible media.

`LoadAsync` returns a fully parsed/validated `LoadedProject` candidate and immutable missing-media reports. Required-module/schema/application/serializer/integrity checks precede session replacement. Missing media does not invalidate an otherwise valid project. No decoder is invoked to reopen it. Schema 1 has no older format to migrate; future versions can add explicit Deliverance module migrations.

Create a fresh `EditingSession(candidate.Snapshot)` to retain the saved revision on reopening. `LoadIntoAsync` commits into an existing live session only if its expected revision still matches, increments that session revision and clears history. A failed/cancelled load has no session side effects. Save cancellation is honored before atomic replacement; once replacement completes the operation is a success. Concurrent save calls use Deliverance's per-file serialization; the last writer wins, not a file-level optimistic revision protocol.

There is no durable command journal or automatic recovery service. Future autosave can call the existing immutable-snapshot `SaveAsync` overload with a separate recovery path, then present a validated candidate for explicit recovery. Recovery selection, scheduling, retention and media relinking UI remain future work.

## Queries and M1b boundary

`TimelineQueries` captures one immutable `ProjectSnapshot`. It exposes metadata/revision, assets, sequences, tracks, clips, duration, intersecting clips and `Evaluate(sequence, time)`. Evaluation returns stable track order/kind plus active clip, asset, stream and exact mapped `SourcePosition`. Disabled clips, muted audio and hidden video are omitted; locks do not suppress playback. Inspection/range queries still expose disabled clips. Out-of-duration positive times produce an empty evaluation; negative times and nonpositive query ranges are rejected.

This is a logical evaluation plan only. M1b can use the same source mapping for preview and export without putting decoded frames, audio samples, pointer movement or playback clocks into edit history. Queries are linear scans in M1a; no performance claim is made for thousands of clips. Track ordering is explicit; compositing/mixing policies belong to M1b.

## Executed validation

```powershell
.\scripts\Build.ps1
dotnet run --project samples/Valencia.Headless -c Release -- artifacts/local/m1a-demo
.\scripts\Smoke.ps1 -AppPath artifacts/publish/win-x64/Valencia.App.exe
```

Current-source Release build and Windows publish succeeded with **zero build warnings/errors**. Test results:

| Suite | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Valencia.Core.Tests | 70 | 0 | 0 |
| Valencia.Persistence.Tests | 21 | 0 | 0 |
| Valencia.Tests (M0/media integration) | 16 | 0 | 0 |
| **Total** | **107** | **0** | **0** |

Core tests cover each edit's undo/redo, transaction rollback/grouping, lock scope, identity/references, ordering/gaps/overlaps, stale/concurrent commits, history bounds, redo invalidation, overflow, fractional grids and exact boundary/source mapping. Three seeded sequences execute 100 actual commands each and re-execute those command records through a second dispatcher with the corresponding undo/redo operations; they compare outcomes, revisions and semantic state after every step. This validates deterministic execution of these in-memory command sequences, not a durable replay/log implementation.

Persistence tests exercise exact round trips (including a huge source epoch and one-tick offset), relative paths/relocation, missing media, unsupported schemas/serializer, malformed references/revisions, corrupted/truncated containers, cancelled load/save, stale replacement and backup reopening. A pre-cancelled save is tested through `ProjectFile`; cancellation and I/O failure after writing temporary-file bytes are tested against the same real Deliverance `FileSaveStore` used by that adapter. Previous save bytes remain identical and reopenable, and temporary files are cleaned up. Additional seeded edits round-trip through real Deliverance saves.

The executable demonstration passes the complete definition-of-done workflow at revision **6**, with identical semantic content after undo/redo and reopening, zero missing media, and the expected active clip/source position. Its exact mapped source offset at sequence second 5 is **1,016,064,000,001 ticks** (four seconds plus one tick). It writes `demonstration.valencia`, `semantic-debug.json` and `result.json` under its chosen output directory. These are metadata-only fixtures; actual media decoding remains covered separately by M0 tests.

Both published M0 smoke runs passed on **NVIDIA GeForce RTX 3070 + NAudio 3 WaveOut**: real MP4/MKV decode/presentation, pause, seek, resize, EOF and GPU pixel readback agreement. Maximum sampled video/audio-device-clock differences were approximately **32.4 ms / 32.9 ms**. No unavailable hardware test was counted as passing. Short synthetic playback is not long-OBS or perceptual audio qualification.

Raw test results/build log/smoke outputs are under ignored `artifacts/local/`; compact current-run results are retained in `docs/validation/m1a.json` and `docs/validation/m1a-demo.json`.

## Remaining limits

No timeline UI, edited preview/export, linked selections, transitions, nested sequences, retiming, effects evaluation, keyframes, waveform generation, scripting or agents. Opacity/gain are document metadata for M1b, not active M0 playback effects. Import records stream metadata without proving decoder support or indexing VFR frames. Unknown bounds remain permissive until better metadata/indexing is available. Project files are portable at the reference level; equivalent native playback across operating systems is not qualified. There is no cross-process document lock, durable command log, autosave scheduler or automatic relinking. Source files are never modified by editing or persistence.
