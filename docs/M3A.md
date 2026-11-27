# M3a — Agent ergonomics and semantic editing

M3a adds an agent-facing interface to the same immutable Valencia editing session used by the native editor. It does not add a second editing engine, a scripting runtime, a network service, or an LLM dependency. The headless integration is exercised over actual MCP stdio, the CLI and compiled Copeland. The optional Dominatus sample orchestrates those same engine methods through a typed transition table.

- [Executable API and agent guide](M3A_AGENT_GUIDE.md)
- [Machine-readable validation evidence](validation/m3a.json)
- [Native frontend handoff](CLAUDE_FRONTEND_HANDOFF.md#m3a--agent-proposals-and-editing-authority)

## Editorial interface

| Capability | Engine path | What it guarantees |
|---|---|---|
| Discover | `session_info`, `edit_objects` | Headless project and exact revision discovery without knowing a GUID. Select a sequence explicitly if there is no unique default. Packed handles, roles, stream/decoder information, FFmpeg encoder availability, jobs and supported observation modes. |
| Search | `footage_query` | Logical query over independent existing cache entries; transcript words, segments and phrase matches, silence, activity, waveform overview, loudness, scene candidates, sequence and attached OBS markers, existing creator highlights. Bounded summary/details and deterministic continuation on the same revision and artifact set. |
| Author | `agent_plan` | Readable time and typed handles normalize to a canonical edit plan, with identical v1 validation and source preconditions. Prepare, inspect, compare and discard do not mutate the document. Commit is explicit and rejects stale input. |
| Observe | `observe` | Labelled source, current-composite, and candidate-composite PNGs; paired position/content comparison; cached source-audio and rendered stereo-mix strips. Exact rendered/evaluated sample metadata distinguishes source PTS from requested time. |
| Recover | `agent_history`, `agent_checkpoint` | Authoritative Undo/Redo and explicit Save As. Named checkpoints hold immutable documents, not duplicated media; restore is one undoable transaction. |
| Hand off | Native `*.agent.json` preview; trusted Copeland; optional Dominatus sample | Apply or reject on the actual desktop session with revision validation. Stdio is a separate headless session, not a remote control for the desktop. |

## Time and identity contracts

- Canonical sequence values are signed Int64 at **254,016,000,000 ticks/second**. JSON carries them as exact decimal strings. Source positions retain the original rational timebase plus an exact offset. Copeland `PositionTicks` returns a checked native `long`.
- Positions accept `00:01:02:15`, `00:01:02;15` (29.97 or 59.94 drop-frame clocks only), `00:01:02.500`, `62.5s`, `f1875`, exact tick strings, and sequence markers. The semicolon selects drop-frame numbering, not a different playback cadence. Invalid dropped labels are rejected. Hours don't wrap at 24.
- Durations accept ticks, frame counts, `ns`, `us`, `ms`, `s`, `m`, `h`, and colon-separated elapsed-clock values, not SMPTE labels. Decimal digits are parsed as integers, then rounded to the nearest tick with ties away from zero. Frame conversion is exact or fails. Context-free frame references fail.
- Marker names use JSON quotes, case-insensitive exact matching, and reject ambiguity with the matching handles. Use `marker:<handle>` to disambiguate. Explicit signed offsets can follow either, e.g. `marker:"Opening"+250ms`. Markers in a plan resolve against its base snapshot. No automatic clamp, ripple, or edit-boundary snapping is performed for authored edits.
- Source observation and authored source trim/insert strings are **stream-start offsets**. Footage analysis reports **container-start offsets** plus the artifact origin; precise rational boundaries are also emitted if conversion to ticks requires outward rounding. Don't use a container offset as a stream offset without checking the origins.
- Compact values carry exact ticks and one readable label. Observation detail can add decimal display seconds, nominal frame-grid index, timecode, clock and coordinate space. A displayed frame index is not a decoder ordinal or a promise that VFR frames are uniformly spaced. For a composed time that is not on a frame boundary, it is the preceding frame label.
- Handles are kind-prefixed, case-sensitive, reversible 128-bit GUID encodings: `S-`, `A-`, `T-`, `C-`, `M-`, `O-`, `P-`. A 24-character handle is shorter than a GUID without introducing collisions or a mutable alias registry. Handles are resolved in the current project/sequence or candidate session. Moving, trimming, inserting other objects, saving, reopening or undoing does not reassign them. New object IDs remain caller-supplied GUIDs.
- Existing project GUIDs acquire handles without a migration write. Roles are optional persisted metadata; old projects open with no roles. Assign/clear roles with `trackRole` and `assetRole`; omitting `role` clears it. Roles are not unique; queries return all matches. Files with populated roles require an M3a reader because older readers reject unknown fields. Role-less files retain the previous file representation.
- Legacy v1 edit, review, export and analysis arguments retain their existing exact meaning. The ergonomic layer is additive, identified as `agentVersion:1`; Copeland `type` declarations in the existing edit-plan catalog govern both parsing and published schemas.

## Review and analysis semantics

- Sequence sheets use the existing export/preview compositor. Source sheets use the actual source decoder and label the decoded frame, not the requested timestamp. Sequence samples show evaluated position, visible clip and track handles (top two burned in, full metadata), and decoded source timestamps where applicable.
- Paired sequence mode compares equal timeline positions and shows unavailable tiles beyond either output window. Content mode maps a caller-named anchor clip's source point into the candidate; it reports removal or multiple retained occurrences rather than arbitrarily choosing a match. Candidate-vs-candidate review requires the same sequence and current base; neither preview is committed.
- Source audio uses cached waveform pyramid levels, source silence, activity, and aligned transcript marks when available. A cached integrated LUFS reading describes the whole source stream. Mix mode reads up to 30 seconds through the normal audio evaluator after gain, envelopes and limiting. The plotted RMS dBFS is not LUFS, and neither plot establishes perceptual quality.
- The cache remains separately generated, invalidated and evictable. Lightweight metadata discovery does not decode or trust payloads; actual queries read through checksum and source-fingerprint validation. On continuation, send the returned `artifactSet` and the same filters/revision. If the set changes, restart at zero. Source phrase proximity can join cross-stream scene evidence; scores are heuristic.
- The semantic summary counts moved/trimmed/created/deleted clips, true abutting clip cut boundaries, track/asset edits, title changes, caption records and output profiles. It separately reports sequence duration change and the union of source-stream ticks no longer referenced. It does not infer A/V sync or editorial intent from clip counts. Stored caption records and projected placement are distinct; paired review reports the actual caption line in each rendered sample where present.

## Measured ergonomics

The baseline used the untouched `3f65435` CLI and Copeland against the **same** representative fixture: two assets, two populated picture tracks with B-roll gaps, distinct microphone and music streams, 16 primary retained segments per linked track, a marker, 1,143 caption words and four profiles. The main source is the controlled three-hour generated VOD from M2d; B-roll came from the local NASA narration qualification fixture. These are deterministic subprocess measurements, not LLM sessions. The comparable path ends at successful commit of the same split and includes one deliberately rejected boundary edit. Both first read the tool catalog. Unacknowledged initialization notifications are not counted.

| Measured on this workstation | Before | M3a |
|---|---:|---:|
| MCP request/response calls | 13 | 10 |
| Calls returning errors | 2 | 1 |
| Serialized request bytes | 3,752 | 2,389 |
| Serialized response bytes | 231,715 | 133,892 |
| Calls for analysis discovery and evidence retrieval | 4 | 2 |
| Additional call to correct the invalid split | 1 | 1 |
| Plan construction + failed and valid validation | 53.65 ms | 91.57 ms |
| Candidate sheet | 254.91 ms | 183.42 ms |

The M3a run eliminates caller tick arithmetic and full track inventory, and joins phrase evidence with activity while the baseline fetched transcript and silence separately. The M3a dry-run did more work (authored-reference resolution, stable diagnostics, retained candidate and semantic summary) and was slower in this single run. Wire sizes are actual UTF-8 stdio bytes, including MCP text plus structured payload. No tokenizer or human-intervention count was used. Checkpoint creation was 1.65 ms; comparing two already-prepared candidates 0.70 ms; paired visual review 218.64 ms. See per-call data for latency and output-size limits; do not generalize this one timing sample to other machines or recordings.

## Qualification and limits

- The tested creator run discovered and queried the original controlled recording, inspected labelled source frames, assigned roles, proposed and compared two cuts, reviewed the selected candidate without committing, created a checkpoint, committed, undid/redid, saved/reopened and exported four configured profiles. Output: **600.000 s** landscape and **three 10.933… s** vertical clips. Captions (1,143 stored words), noise reduction, a music ducking envelope and +2.17 dB master normalization gain were retained. Independent FFmpeg verification checked **61 video frames** including cuts/edges, and **7 audio windows** against explicit decoded source selection and mix arithmetic. The independently measured long-form export was **−18.47 LUFS / −0.85 dBTP**: it did not reach the −16 LUFS target, and lossy encoding did not preserve the sample limiter's −1 dB ceiling as a true-peak ceiling. No coherent storytelling or perceptual quality claim is made from generated footage.
- Automated tests exercised exact timecode, roles/handles across native history and persistence, source PTS versus requested time, content correspondence, waveform/cut alignment, stale candidates and analysis drift, declared MCP schemas and real stdio, compiled Copeland Int64 and actual CLI execution. The native executable smoke also exercised review/reject/apply/undo and desktop checkpoint status.
- No automatic agent-to-desktop IPC, rebasing, durable session-history journal, multi-project concurrency lock, or voice/visual understanding model was added. For handoff across processes, send the authored/canonical plan with its base identity and revision, not a process-local candidate handle. Native checkpoint creation/listing is intentionally small; custom naming, inspection, restore and deletion are available through the agent API in the owning session. Native role labels and handles are visible; role assignment uses the same plans rather than a new inspector editor.
- Source/audio observations depend on available analyses. OBS output-duration markers remain unaligned and come from the currently attached OBS workflow; there is no cross-session manifest index. Existing creator scores are only available for retained proposals in the current process. Contiguous alignment for all real VFR/container timebases was not exhaustively qualified.
- The brief fixed **.NET 10**. The baseline had migrated to .NET 11 and used a C# 15 union in editor recovery. M3a uses the .NET 10 SDK, C# 14 and a closed result wrapper with the same success/failure/uninitialized behavior; the existing editor layout, recovery and interaction paths remain in place.
- The November 26 wish list informed prioritization. Its installer, codec migration, hosted model service and export-lint roadmap were not added to this implementation scope.
