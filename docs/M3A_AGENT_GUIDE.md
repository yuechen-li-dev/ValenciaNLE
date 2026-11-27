# Operating Valencia from an agent

## Start and inspect

Build with `dotnet build Valencia.slnx -c Release`. The examples below use `src/Valencia.Cli/bin/Release/net10.0/Valencia.Cli.dll` (FFmpeg 7.1 shared libraries and external FFmpeg are required for media operations).

```powershell
$cli = 'src/Valencia.Cli/bin/Release/net10.0/Valencia.Cli.dll'
dotnet $cli agent call session_info --project 'C:/Editing/project.valencia'
dotnet $cli schema export
dotnet $cli mcp --root 'C:/Editing' --project 'project.valencia'
```

MCP uses protocol `2025-11-25` over stdio. Send `initialize`, then `notifications/initialized`. Call `session_info` with `{}` before discovering the project. `tools/list` describes arguments and provides generated types. New calls return an `AgentReply` in `structuredContent.result`; read its exact-string `revision`, `ok`, and `result` or structured `issue`. In the examples below, replace names in angle brackets with values from the running session.

| Tool | Example parameters beyond projectId/revision |
|---|---|
| `edit_objects` | `{"kind":"track","sequence":"<S-handle>","role":"mic"}` or `{"kind":"clip","at":"12.5s","limit":8}` |
| `footage_query` | `{"kind":"summary"}`, then `{"kind":"matches","asset":"<A-handle>","stream":1,"transcriptContains":"let's go","nearActivity":"2s","minimumSeparation":"8s","limit":10}` |
| `observe` source | `{"mode":"source","asset":"<A-handle>","stream":0,"at":["f0","1s"],"output":"source.png","inlineImage":true}` |
| `observe` range | `{"mode":"sequence","start":"5s","end":"11s","cadence":"2s","maxFrames":3,"columns":3,"output":"sequence.png"}` |
| `observe` audio | `{"mode":"sourceAudio","asset":"<A-handle>","stream":1,"start":"5s","end":"10s","output":"audio.png"}` or `{"mode":"mixAudio","start":"5s","end":"10s","output":"mix.png"}` |
| `agent_checkpoint` | `{"action":"create","name":"Before cleanup"}` |
| `agent_history` | `{"action":"undo"}`, `{"action":"redo"}`, or `{"action":"save","output":"edited.valencia"}` |

Return to `edit_objects` with `kind:"clip", handle:"<C-handle>"` to look up a rendered clip label. `footage_query` returns exact `total` and `nextOffset`; send `artifactSet` and the same filters when continuing (for summary pages it is under `context`). Source range input uses the artifact container clock. A cached audio/scene/transcript key may be pinned in `artifacts`. Use `start_analysis` and its job status if the necessary artifact doesn't exist.

## Prepare, review, commit or share

Example `agent_plan` prepare request (all new object IDs must be fresh):

```json
{
  "projectId": "<project GUID>",
  "revision": "<revision>",
  "action": "prepare",
  "plan": {
    "sequence": "<S-handle>",
    "note": "Put a cut one second after Opening",
    "operations": [
      {
        "operation": "split",
        "clipId": "<C-handle>",
        "at": "marker:\"Opening\"+1s",
        "rightClipId": "f9a8a434-4da0-4f89-8819-ed21bba977ea"
      }
    ]
  }
}
```

If the split is invalid, follow the operation index, subject and exact bounds from the issue. Never change the requested edit silently. For an alternative prepare another candidate on the same revision. Compare with `{"action":"compare","candidate":"<P-first>","other":"<P-other>"}`. To inspect and bring over an existing specialized creator plan, call `start_creator`, poll `creator_status`, then prepare with `creatorKey` instead of `plan`. An existing canonical v1 edit plan can be supplied as `canonicalPlan`.

For the visual proof, send `observe` with `mode:"paired"`, `candidate`, `beforeOutput`, `output` and a bounded `at` list. Add `other` to compare different candidates, or `correspondence:"content",anchorClip:"<C-handle>"` to follow retained original content. In regular position mode an out-of-window frame is visibly unavailable, never replaced by unrelated content. Use new output paths. MCP only embeds images when explicitly requested and when their total is at most 512 KiB; otherwise it returns filenames. Structured replies have a 128 KiB limit.

Create a checkpoint and then send `agent_plan` with `action:"commit"` and the selected `candidate`; use the new revision from that response. Undo, redo and restore all require the current revision. The in-memory session is independent of the saved project. Use Save As to persist; reopen with another CLI or desktop session to verify the result. Checkpoint/candidate handles do not follow across processes or restarts.

A human can open the authored prepare request above as `proposal.agent.json` with **Script** in the editor. It renders a native proposal card with explicit **Apply** or **Cancel**, not an executable script; it must match the editor's project ID and base revision. When the source is a prepared canonical proposal, request `action:"inspect", includePlan:true` and use its `canonicalPlan` field in the file, subject to the transport limit. For a large creator plan, use the trusted CLI plan file instead of trying to send the full diff through MCP.

## CLI, Copeland, and Dominatus

- `agent call <tool> --project <file> --request <request.json>` performs one call. It starts a new session each time. For candidates, history and checkpoints, use `agent run <project> [--requests <jsonl>]` and exchange `{"method":"agent_plan","request":{...}}` lines in one process. Agent requests/responses use the same engine contract; this isn't an MCP transport.
- Run `dotnet $cli script run examples/agent-time.ts --project <project>` to compile and execute the native Int64 example. A trusted script can call `api.Agent(method, requestJson)` and can use `PositionTicks` / `DescribePosition`. Direct scripts can perform observations, explicit save, checkpoints and history operations; staged workflows keep their existing I/O restrictions. Copeland runs with local CLR privileges. It is not a sandbox for untrusted scripts.
- Run `dotnet run --project samples/DominatusAgent -c Release -- <project> <review.json>` for a real Dominatus transition-table run. The JSON contains `revision`, `plan` (as in the prepare request), and `observation` (including `beforeOutput`, `output` and `at`). It prints a machine-readable rule/engine trace and stops at `AwaitingSelection`. Pass `--apply <new.valencia>` to explicitly select the freshly reviewed candidate and save; use new PNG paths for that run. The project pins the expected revision from the input, even on a second invocation. Build with a sibling Dominatus checkout or pass `-p:DominatusRoot=<path>`.

## Reproduce the evidence

The executable driver `scripts/M3aAgentProof.py` uses Python 3's standard library and actual Valencia processes. It uses the M2d three-hour source and pinned analysis artifacts from the existing qualification workspace. Copy the three referenced `.vca` cache files from the trusted analysis cache to `artifacts/local/.valencia-analysis` if needed. The representative split fixture is the M2d edited project plus the secondary NASA asset, two noncontiguous B-roll clips, and an Opening marker; the original-source creator qualification uses the unedited three-hour project.

```powershell
python scripts/M3aAgentProof.py smoke --project m3a/fixture-broll.valencia --output artifacts/local/m3a/repro-smoke
python scripts/M3aAgentProof.py creator --project m2d/three-hour.valencia --output artifacts/local/m3a/repro-creator/outputs
scripts/VerifyCreator.ps1 -Directory artifacts/local/m3a/repro-creator -SourcePath artifacts/local/m2d/three-hour.mkv
scripts/VerifyCreatorAudio.ps1 -Directory artifacts/local/m3a/repro-creator -SourcePath artifacts/local/m2d/three-hour.mkv
```

For the before measurement use `baseline` instead of `smoke` and `--cli` pointing at the Release CLI from `3f65435`. A compiled copy of that CLI can be pointed at the same role-less fixture without changing the current checkout. All output directories must be new. The driver emits per-call wire sizes/latencies and evidence plus the input for the native and Dominatus examples. Benchmark timings are illustrative, not CI gates.
