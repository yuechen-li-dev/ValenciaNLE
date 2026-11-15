# M2a — Programmable editing engine

Current follow-up: [native Int64 and audio seeking](INT64_AUDIO_SEEK_FIX.md) supersedes the historical CLR Int64 limitation below. Canonical decimal strings remain the wire representation; native scripts now also use checked `long` values.

M2a implements a headless Copeland → C# → Roslyn → managed assembly → Valencia workflow, typed declarative plans, CLI, optional local MCP, and a small native script entry point. No Node.js, JavaScript runtime, browser, new interpreter, or agent framework is involved.

## Ownership and project graph

```text
App (native UI) ── Scripting ── Engine ── Core
                         │         ├── Persistence ── Deliverance
CLI ─────────────────────┘         └── Media (existing evaluation/export)
Scripting ── Copeland.TS.Backend.CSharp ── Copeland.TS + Roslyn
Engine ── Copeland.TS (compiler-owned type/TSON contracts)
```

`Valencia.Engine/EditingEngine.cs` is the v1 facade. It owns one `EditingSession`, persistence context and bounded export-job registry. `Query()` returns the existing immutable `TimelineQueries`. `Execute(EditCommand/EditTransaction)`, undo/redo and project replacement use M1a's dispatcher. UI scripts receive that same live session; they do not copy its editing document. The native UI continues using its existing dispatcher/gesture code. Shared media insertion mapping moved to `InsertionCommands.Build`, consumed by both the UI and CLI bootstrap.

Core still has no UI, graphics, codec, persistence, or compiler dependency. The only Core change adds transaction preview and ensures preview rejects revision overflow just as commit does. No new reducer or event-sourcing mechanism was introduced.

## Engine API v1

| Surface | Operations |
|---|---|
| Project | `Create`, `OpenAsync`, validated `OpenIntoAsync`, `SaveAsync`, project path and saved-document context, `MissingMedia` |
| Import | `ImportAsync` delegates to the existing FFmpeg metadata probe, then an expected-revision `AddAsset` command |
| Queries | Snapshot, assets, sequences, tracks, clips, duration, range intersections, active clips and rational source positions through `TimelineQueries` |
| Commands | Existing typed Core operations, including create sequence/add asset/add track, insert/move/trim/split/delete/ripple/track state/relink |
| Transactions | Existing atomic `EditTransaction`, expected revision, single undo entry |
| Plans | `Plan(plan, commit: false/true)`; compiler schema validation, Core preconditions and structured semantic diff |
| History | `Undo`/`Redo` with expected revision |
| Export jobs | Start/status/wait/cancel; immutable project snapshot captured at start |

`EngineJson` supplies strict camelCase DTO serialization: unknown fields, missing constructor fields, invalid enum values and noncanonical Int64 data are rejected. `EditPlan.SchemaVersion` and result `ApiVersion` are 1. Native clients retain typed Core DTOs and immutable collections. The CLR script projection uses supported primitive signatures plus typed native `ScriptApi`/`PlanBuilder` objects. Returned inspection data is serialized by the same contracts.

Commands, queries and jobs stay separate. No frame, sample, playhead gesture or device-clock data is recorded in command history. Export captures a snapshot and runs independently of subsequent editing. One export runs per engine, with at most 32 retained job records. It preserves M1b's bounded evaluator/pipes, cancellation cleanup and atomic output publication.

## Copeland `type` profile

General-purpose upstream changes are in sibling Copeland:

- `SourceFileProfile.cs`: accepts the explicitly selected `type` profile.
- `Parser.cs` / `SyntaxTree.cs`: reuse the existing type-alias, structural-object, optional-field, array and union AST nodes. Structural alias unions are enabled only for the schema parse/profile; ordinary nominal-union behavior is retained.
- `TsonSchema.cs`: extends the existing TSON catalog with alias shapes, references, optional fields, string literals, structural objects and alternatives.
- `Tson/TypeProfileCompiler.cs`: binds and validates finite aliases, projects literal `.obj.ts` data, and extracts a canonical metadata artifact.
- `SourceProfileChecks.cs`: gives `COPE-TYPE-0001` if schema source is sent to executable MIR compilation.

Example schema authoring:

```typescript
type CopelandProfile = "type";
type Time = string;
type Range = { start: Time; end: Time; };
type Trim = { operation: "trim"; range: Range; note?: string; };
type Delete = { operation: "delete"; clipId: string; };
type Plan = { operations: (Trim | Delete)[]; };
```

`TypeProfileCompiler.Compile(source, path, schemaIdentity, selectedProfile: "type")` also selects the profile explicitly without a file prologue. The filename is not authoritative. Aliases are declarations, never synthesized runtime variables. `CanonicalArtifact(catalog)` emits deterministic `copeland.type.v1` metadata ordered by type and field name; field identities include their owning type/path. Source locations are retained in schema diagnostics. Invalid references, duplicate declarations/fields, recursive aliases, unsupported types and excessive source/depth/node counts fail.

This is a bounded structural schema subset: string/number/boolean, optional fields, arrays, nested objects, reusable finite named types, string-literal and object unions. Generics, recursive schemas, computed types, executable initializers and general reflection are excluded. The existing binary TSON encoder has not gained structural-union encoding; plans use the compiler's TSON catalog for authoring/validation and JSON for wire data. Existing nominal TSON, `$schema` readers and executable aliases remain on their original paths.

## Typed edit plans

The authoritative application schema is the embedded `src/Valencia.Engine/Contracts/edit-plan.obj.ts`, compiled into a TSON catalog. `Valencia.Cli schema export` prints its canonical metadata. `docs/validation/m2a-schema.json` records the extracted artifact. JSON Schema is used only as derived MCP wire metadata, generated from this catalog, not as the schema authoring language.

A data instance can be `.obj.ts`:

```typescript
const $value: EditPlan = {
    schemaVersion: 1,
    projectId: "<project-guid>",
    projectRevision: "3",
    sequenceId: "<sequence-guid>",
    operations: [
        { operation: "split", clipId: "<clip-guid>", at: "508032000000", rightClipId: "<new-guid>" }
    ]
};
```

The data reader parses this through Copeland's existing AST against the selected external catalog. It accepts exactly one typed `$value` literal object/array tree; it never executes the input. Ordinary JSON instances use the same catalog and DTO validation. `$value` is instance data, not a schema declaration. Limit: 1 MiB source and 1–500 ordered operations per plan, plus TSON nesting/value limits.

Supported discriminants: `insert`, `move`, `trim`, `split`, `delete`, `rippleDelete`, `trackState`, `addTrack`. Insert/trim preserve the rational source timestamp value/timebase and exact residual offset. Ripple supplies explicit tracks and split IDs according to M1a semantics. All created object IDs come from the caller's plan.

Dry-run calls **the same** `EditingSession.Apply` path with commit disabled. It neither replaces the document nor pushes history. Commit executes one atomic transaction with the expected project/revision. Results include validity, committed status, expected/predicted/resulting/current revisions, affected IDs, created/deleted/moved/updated clip before/after records, resulting duration, diagnostics and current commit eligibility. Track changes appear in affected IDs. `CanCommit` is a point-in-time observation, not a reservation; intervening commits cause revision rejection. Commit increments revision once; undo/redo each increment once. Failures do not consume history.

## Exact time and CLR interoperability

The current CLR projection explicitly rejects `System.Int64` in `Binder.TryProjectClrType`. Adding a primitive through binding, MIR and every affected backend was not needed for this milestone. The working fallback is **canonical signed decimal Int64 strings**, never binary64 conversion. `EngineJson.Int64` parses checked values and rejects `01`, `+1`, `-0` and overflow. Core retains `long` ticks at **254,016,000,000 ticks/second**, with original rational source timestamps and M1a rounding policies unchanged.

`ScriptApi.CanonicalTicks`, `AddTicks` and `Frames(frameCount, numerator, denominator)` perform checked native exact arithmetic. The real compiler/CLR test returns `9007199254740994` for `9007199254740993 + 1`; `Int64.MaxValue + 1` produces a structured failure without document mutation. Native Int64 projection was **not** implemented or claimed.

## Trusted script execution

Executable files remain ordinary Copeland `.ts`, preferably `type CopelandProfile = "Dotnet";`. A named entry must have exactly:

```typescript
using Valencia.Scripting;
function run(api: ScriptApi): string { return api.Info(); }
```

`CopelandScriptHost` calls the actual `CopelandCompiler.CompileToMir` with C# / RyuJIT / net10 target and a CLR reference to `ScriptApi`. It emits through `CSharpBackend.Emit`, compiles with Roslyn 4.14.0, loads a collectible managed assembly, validates the reflected entry signature and supplies a scoped facade. Compilation and execution run off the UI thread. There is no subprocess JS execution or alternate interpreter.

Compiler diagnostics retain source path/line/column. Backend errors identify the source; Roslyn errors explicitly identify generated C# paths. Runtime and cancellation failures are structured diagnostics. This does not add full original-source debugging maps for generated CLR code.

Per-host cache: eight artifacts keyed by source/path/entry and compiler/backend/engine/facade assembly MVIDs. Unchanged scripts reuse the managed entry; evicted contexts are unloaded. The cache is process-local, not a persistent assembly store. Trusted script static state can persist while an artifact is cached.

`ScriptApi` exposes project open/save/query/import, timeline IDs/queries, exact arithmetic, typed plan construction, validation/application, undo/redo and export jobs. Builder conveniences cover split/move/delete/add track; `ApplyPlan` accepts all supported operations through the shared typed plan schema. The native facade exposes the complete typed Core command surface. `examples/rough-cut.ts` demonstrates project open, inspection, exact edits, dry-run, apply, undo/redo, save and export.

**Trust:** local scripts have ordinary process/CLR privileges. Type checking and collectible `AssemblyLoadContext` are not sandboxes. Cancellation is cooperative at compilation stages and facade/job boundaries; an arbitrary CLR infinite loop cannot be forcibly stopped safely. A failing script can leave earlier successful transactions committed, but each transaction remains atomic and undoable. Use one plan for all-or-nothing edits. Agents should use validated data plans; MCP has no executable-script tool.

## CLI and MCP

Published CLI: `artifacts/publish/cli-win-x64/Valencia.Cli.exe` (framework-dependent .NET 10).

```text
project create <project> [--media <source>]
project info <project>
timeline list <project>
script run <script.ts> --project <project> [--entry run] [--save-as <project>]
plan validate|apply <plan.json|plan.obj.ts> --project <project> [--save-as <project>]
schema export
export <project> --sequence <id> --output <mp4>
mcp --root <absolute-workspace> --project <relative-project>
```

Successful command output is JSON, except help. Errors also go to stderr. Exit codes: 0 success, 1 request/load failure, 2 plan rejection, 3 script failure, 4 export failure, 130 cancellation. Applying a plan does not save without `--save-as`; scripts may explicitly call `Save`. Export never changes the project. Ctrl+C requests cancellation.

MCP is optional and pinned to [protocol 2025-11-25 stdio](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2025-11-25/basic/transports.mdx), with newline-delimited JSON-RPC 2.0, initialize/initialized, ping, tools/list and tools/call. Only that protocol version is supported; newer 2026 transport semantics are not claimed. Seven tools:

| Tool | Required arguments beyond `projectId` |
|---|---|
| `project_info` | none |
| `timeline_query` | optional sequenceId; atTicks or rangeStart/rangeEnd |
| `validate_edit_plan` / `apply_edit_plan` | revision string and plan; envelope/plan revision must agree |
| `run_export` | revision, sequenceId, workspace-relative output |
| `export_status` / `cancel_export` | jobId |

Tool failures return structured diagnostics with `isError`; results include text and structured content. Export progress is polled through status; this adapter does not send unsolicited progress notifications. Incoming frames are bounded to 1 MiB characters. EOF cancels/drains jobs and exits. Mutations remain in the server's session; there is deliberately no automatic save or arbitrary script tool in this minimal tool set.

The launcher supplies an explicit workspace root and project. Project assets and export paths must remain inside it; rooted output arguments, traversal and existing reparse-point aliases are rejected. This local path policy is not an OS security sandbox or a defense against concurrent malicious filesystem replacement. Use a dedicated local workspace; CLI/trusted scripts retain normal filesystem privileges.

## Native UI

**Run script** opens a native file dialog, pauses playback, cancels gestures, starts the existing cancellable job path and disables competing UI edits until completion. The scope shares the current project/session/path. It can save, edit and undo/redo; project replacement is disabled in this UI scope. CLI scopes can open projects. Completion or failure refreshes the timeline/preview and displays status. No scripting IDE or new frontend was added.

## Validation

Run `scripts/Build.ps1`, the affected Copeland test filter recorded in `docs/validation/m2a.json`, then `scripts/Smoke.ps1`, `scripts/EditorSmoke.ps1`, `scripts/VerifyEditorExport.ps1` and `scripts/ProgrammingSmoke.ps1`. All builds/tests use current source. Native publish outputs include both executables.

Automated suites: **142 Valencia tests** (70 Core, 21 Persistence, 33 media/UI-model, 18 programming), plus **290 affected Copeland tests** (including six new type-profile cases); zero failed/skipped. Compiler coverage includes legacy `$schema`, nominal TSON/object assets, ordinary aliases/unions, CLR binding and source-profile behavior. The whole Copeland repository suite was not run.

Programming tests spawn real CLI and MCP processes. They exercise initialization/discovery, query, dry-run/application, revision/project/identity failures, path rejection, actual export jobs/status/cancellation/temp cleanup, direct-command/plan semantic equality, exact time, compiler diagnostics, entry rejection, runtime overflow, cache reuse, UI scope restrictions and explicit save/reopen. The MCP test then applies an actual four-operation split/move plan, exports it successfully and independently verifies its selected source frame, black/silent gap, H.264/AAC streams and five-second duration. They do not equate direct method calls with protocol proof.

The headless demonstration generates a four-second numbered H.264/AAC source with a 440 Hz tone, opens a saved project, compiles the actual example, splits both streams at two seconds, moves their tails to three seconds, undoes/redoes, saves/reopens and exports five seconds. Independent FFmpeg decoding checks output time 4 s against source time 3 s, black at 2.5 s, silent gap audio, codecs and duration. Original project bytes remain unchanged. The CLI also validates/applies an equivalent `.obj.ts` plan. Outputs and JSON records are under `artifacts/local/m2a-demo`.

RTX 3070 native qualification reruns M0 MP4/MKV player and M1b editor/export. The editor additionally compiles a script against the live session, commits, undoes/redoes and restores exact semantic state before its existing split/drag/trim/delete/play/save/reopen/export and 5,000-clip checks. The smoke supplies a filename directly to the same native script handler; the file dialog itself was not manually exercised in M2a. Measurements, source characteristics, hashes, test timestamps and independent decode results are in **`docs/validation/m2a.json`**. Observed device-clock/frame skew is a software measurement, not an external physical audio/display latency measurement. Playback smokes are short bounded runs; multi-hour sync, real user footage and arbitrary trusted scripts remain unqualified.

## Remaining limits

- No native Int64 compiler primitive: exact strings are the supported scripting/wire contract.
- Finite type-schema subset and literal `.obj.ts` instances; no recursive/generic schema or general reflection, no new binary structural TSON encoder.
- Trusted scripts only; cooperative cancellation and process-local cache. Runtime failures preserve completed transactions rather than rolling back the entire script.
- MCP supports the pinned 2025 protocol/tool subset and live-session edits; project saving and undo/redo remain CLI/script/native API operations.
- M1b media/platform limitations remain: Windows named-pipe export, CPU composition/software decode, bounded layer counts, SDR only, no long-recording qualification. No M2 transcription/highlights/silence algorithms were added.
