"""Deterministic Valencia qualification driver; no model calls.
Run from repository root, against the local M2d three-hour fixture/cache or another
fixture with the same editorial predicates. See docs/M3A.md for preparation.
"""
import argparse
import base64
import json
import pathlib
import subprocess
import time
import uuid

CLOCK = 254_016_000_000
TRANSCRIPT = "BE1BFE895B08805C03FD09E50EFF1F35DC11201B9CB72B7DA4C58A4A44BA6CAB"
AUDIO = "F273084D37D28387C31952503EA62B05E39902E21C1F1FFAB7E138215CEC52CB"
SCENE = "65F2086C26EB1082F0C017F7078E678FA9D91BEE244843D42F2299AF96595039"
CLI = pathlib.Path("src/Valencia.Cli/bin/Release/net10.0/Valencia.Cli.dll")


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


class Mcp:
    def __init__(self, cli, root, project, stderr):
        self.calls = []
        self.log = stderr.open("wb")
        self.process = subprocess.Popen(
            ["dotnet", str(cli), "mcp", "--root", str(root), "--project", project],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.log,
        )
        self.rpc("initialize", {"protocolVersion": "2025-11-25", "clientInfo": {"name": "M3a proof", "version": "1"}, "capabilities": {}})
        self.rpc("notifications/initialized", {}, notify=True)

    def rpc(self, method, params, label=None, notify=False, phase="workflow"):
        request = dict(jsonrpc="2.0", method=method, params=params)
        if not notify:
            request["id"] = len(self.calls) + 1
        line = (json.dumps(request, separators=(",", ":")) + "\n").encode()
        t = time.perf_counter()
        self.process.stdin.write(line)
        self.process.stdin.flush()
        if notify:
            return
        response = self.process.stdout.readline()
        if not response:
            raise RuntimeError(f"MCP closed while calling {label or method}")
        data = json.loads(response)
        result = data.get("result", {})
        failed = "error" in data or result.get("isError", False)
        self.calls.append(dict(call=label or method, phase=phase, requestBytes=len(line), responseBytes=len(response),
                               ms=round((time.perf_counter() - t) * 1000, 2), failed=failed))
        return data

    def call(self, tool, expected_error=None, phase="workflow", **arguments):
        data = self.rpc("tools/call", dict(name=tool, arguments=arguments), tool, phase=phase)
        result = data.get("result", {})
        reply = result.get("structuredContent", {}).get("result")
        if expected_error:
            assert result.get("isError") and reply["issue"]["code"] == expected_error, data
            return reply["issue"]
        assert not result.get("isError") and reply is not None, data
        if arguments.get("inlineImage"):
            images = [item for item in result["content"] if item["type"] == "image"]
            assert images and all(image["mimeType"] == "image/png" and
                                  base64.b64decode(image["data"]).startswith(b"\x89PNG\r\n\x1a\n") for image in images)
        return reply["result"] if isinstance(reply, dict) and "agentVersion" in reply else reply

    def wait(self, method, project_id, job, timeout=180):
        start = time.perf_counter()
        while time.perf_counter() - start < timeout:
            reply = self.call(method, phase="poll", projectId=project_id, jobId=job)
            status = reply.get("status", reply)
            if status["state"] not in ("running", "queued"):
                assert status["state"] == "completed", status
                return status
            time.sleep(.5)
        raise TimeoutError(f"{method}: {job}")

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        self.log.close()


def smoke(mcp, root, out, project_path, cli):
    tools = mcp.rpc("tools/list", {})["result"]["tools"]
    assert len(tools) == 35 and all("Valencia engine v1:" not in t["description"] for t in tools)
    overview = mcp.call("session_info")
    pid, rev, sequence = overview["projectId"], overview["revision"], overview["activeSequence"]
    common = dict(projectId=pid, revision=rev)
    summary = mcp.call("footage_query", **common, kind="summary")
    asset = next(i["asset"] for i in summary["items"] if any(a["key"] == TRANSCRIPT for a in i["analyses"]))
    matches = mcp.call("footage_query", **common, kind="matches", asset=asset, stream=1,
                       transcriptContains="important announcement", nearActivity="2s",
                       artifacts=[TRANSCRIPT, AUDIO], limit=5)
    assert matches["total"] > 0
    clips = mcp.call("edit_objects", **common, kind="clip", sequence=sequence, at="0s")
    clip = clips["items"][0]["handle"]
    right = str(uuid.uuid5(uuid.NAMESPACE_URL, "valencia-m3a-split"))
    def plan(at):
        return dict(sequence=sequence, operations=[dict(operation="split", clipId=clip, at=at, rightClipId=right)])
    timer = time.perf_counter()
    issue = mcp.call("agent_plan", **common, action="prepare", plan=plan("0s"), expected_error="SPLIT_OUTSIDE_CLIP")
    assert issue["subject"] == clip and issue["constraints"]["endExclusive"]["ticks"]
    proposed = mcp.call("agent_plan", **common, action="prepare", plan=plan("1s"))
    construction = round((time.perf_counter() - timer) * 1000, 2)
    candidate = proposed["candidate"]
    assert proposed["summary"]["cuts"]["added"] == 1
    def output(name): return (out / name).relative_to(root).as_posix()
    candidate_review = mcp.call("observe", **common, mode="candidate", candidate=candidate,
                                at=["0s", "1s"], maxEdge=320, output=output("candidate.png"))
    assert candidate_review["details"]["after"]["samples"][0]["metadata"]["layers"][0]["clip"] == clip
    committed = mcp.call("agent_plan", **common, action="commit", candidate=candidate)
    assert committed["committed"]
    comparable_calls = len(mcp.calls)
    current = dict(projectId=pid, revision=committed["resultingRevision"])
    undone = mcp.call("agent_history", **current, action="undo", phase="extension")
    redone = mcp.call("agent_history", projectId=pid, revision=undone["resultingRevision"], action="redo", phase="extension")
    current["revision"] = redone["resultingRevision"]
    mcp.call("agent_plan", **current, action="commit", candidate=candidate, expected_error="STALE_REVISION", phase="extension")
    mcp.call("agent_checkpoint", **current, action="create", name="After split", phase="extension")
    tracks = mcp.call("edit_objects", **current, kind="track", sequence=sequence, phase="extension")["items"]
    def proposed_roles(role):
        ops = [dict(operation="trackRole", trackId=t["handle"], role=r)
               for t, r in zip(tracks, ["game", "mic", role, "broll"])]
        return mcp.call("agent_plan", **current, action="prepare", plan=dict(sequence=sequence, operations=ops), phase="extension")["candidate"]
    a, b = proposed_roles("music"), proposed_roles("discord")
    mcp.call("agent_plan", **current, action="compare", candidate=a, other=b, phase="extension")
    mcp.call("observe", **current, mode="paired", candidate=a, at=["0s", "1s"],
             beforeOutput=output("pair-before.png"), output=output("pair-after.png"), phase="extension")
    source = mcp.call("observe", **current, mode="source", asset=asset, stream=0, at=["1s", "2s"], inlineImage=True,
                      output=output("source.png"), phase="extension")
    assert "decodedPts" in source["details"]["samples"][0]["metadata"]
    audio = mcp.call("observe", **current, mode="sourceAudio", asset=asset, stream=1, sequence=sequence,
                     artifacts=[AUDIO, TRANSCRIPT], start="0s", end="12s", output=output("source-audio.png"), phase="extension")
    assert audio["details"]["waveformBins"] > 0
    mix = mcp.call("observe", **current, mode="mixAudio", sequence=sequence, start="0s", end="2s",
                   output=output("mix-audio.png"), phase="extension")
    assert mix["details"]["space"] == "sequence"
    role_commit = mcp.call("agent_plan", **current, action="commit", candidate=a, phase="extension")
    current["revision"] = role_commit["resultingRevision"]
    assert mcp.call("edit_objects", **current, kind="track", sequence=sequence, role="mic", phase="extension")["total"] == 1
    mcp.call("agent_history", **current, action="save", output=output("edited.valencia"), phase="extension")
    restored = mcp.call("agent_checkpoint", **current, action="restore", name="After split", phase="extension")
    assert int(restored["resultingRevision"]) > int(current["revision"])
    reopened = subprocess.run(["dotnet", str(cli), "agent", "call", "session_info", "--project", str(out / "edited.valencia")],
                              check=True, capture_output=True, text=True)
    assert json.loads(reopened.stdout)["revision"] == current["revision"]
    copeland = subprocess.run(["dotnet", str(cli), "script", "run", "examples/agent-time.ts", "--project", str(root / project_path)],
                              check=True, capture_output=True, text=True)
    result = dict(comparableCalls=comparable_calls, callerArithmetic=[], planConstructionAndValidationIncludingCorrectionMs=construction,
                  matchCount=matches["total"], clip=clip, candidate=candidate, copeland=json.loads(copeland.stdout),
                  reopenedRevision=current["revision"], sourceSamples=source["details"]["samples"], diagnostic=issue)
    write_json(out / "dominatus-review.json", dict(revision=rev, plan=plan("1s"), observation=dict(at=["0s", "1s"],
               beforeOutput=str(out / "dominatus-before.png"), output=str(out / "dominatus-after.png"))))
    write_json(out / "desktop.agent.json", dict(common, action="prepare", plan=plan("1s")))
    return result


def creator(mcp, root, out, project_path, cli):
    overview = mcp.call("session_info")
    pid, rev, sequence_handle = overview["projectId"], overview["revision"], overview["activeSequence"]
    common = dict(projectId=pid, revision=rev)
    sequence = mcp.call("edit_objects", **common, kind="sequence", handle=sequence_handle)["items"][0]["id"]
    footage = mcp.call("footage_query", **common, kind="summary", artifacts=[TRANSCRIPT, AUDIO, SCENE])
    asset = next(r["asset"] for r in footage["items"] if r["analysisCount"] > 0)
    matches = mcp.call("footage_query", **common, asset=asset, stream=1, kind="matches",
                       transcriptContains="important announcement", nearActivity="2s", artifacts=[TRANSCRIPT, AUDIO], limit=8)
    assert matches["total"] > 0
    def output(name): return (out / name).relative_to(root).as_posix()
    mcp.call("observe", **common, mode="source", asset=asset, stream=0, at=["4s", "8s"], output=output("source.png"))
    track_info = mcp.call("edit_objects", **common, kind="track", sequence=sequence_handle)["items"]
    roles = ["game", "mic", "music"]
    role_plan = dict(sequence=sequence_handle, operations=[dict(operation="trackRole", trackId=t["handle"], role=r)
                                                           for t, r in zip(track_info, roles)])
    role_candidate = mcp.call("agent_plan", **common, action="prepare", plan=role_plan)["candidate"]
    common["revision"] = mcp.call("agent_plan", **common, action="commit", candidate=role_candidate)["resultingRevision"]
    assert mcp.call("edit_objects", **common, kind="track", role="mic", sequence=sequence_handle)["total"] == 1
    policy = dict(sequenceId=sequence, transcriptKey=TRANSCRIPT, audioKey=AUDIO, sceneKey=SCENE,
                  keywords=["important announcement"], musicStreamIndices=[2], width=320, height=180, shorts=3)
    master = mcp.call("start_creator", **common, request=policy)
    master_status = mcp.wait("creator_status", pid, master["jobId"], 480)
    key = master_status["planKey"]
    primary = mcp.call("agent_plan", **common, action="prepare", creatorKey=key)
    alternate = mcp.call("start_creator", **common, request=dict(policy, normalize=False, musicGainDb=-18))
    alternate_status = mcp.wait("creator_status", pid, alternate["jobId"], 480)
    other_key = alternate_status["planKey"]
    other = mcp.call("agent_plan", **common, action="prepare", creatorKey=other_key)
    candidate = primary["candidate"]
    mcp.call("agent_plan", **common, action="compare", candidate=candidate, other=other["candidate"])
    visual = mcp.call("observe", **common, mode="paired", candidate=candidate, at=["0s", "4s", "8s"],
                      beforeOutput=output("creator-before.png"), output=output("creator-after.png"))
    assert visual["details"]["after"]["samples"]
    mcp.call("agent_checkpoint", **common, action="create", name="Before creator cut")
    # Export verification consumes the full canonical plan via the normal creator CLI, because MCP bounds large responses.
    committed = mcp.call("agent_plan", **common, action="commit", candidate=candidate)
    history = mcp.call("agent_history", projectId=pid, revision=committed["resultingRevision"], action="undo")
    redo = mcp.call("agent_history", projectId=pid, revision=history["resultingRevision"], action="redo")
    common["revision"] = redo["resultingRevision"]
    mcp.call("agent_history", **common, action="save", output=output("creator.valencia"))
    disk = json.loads(subprocess.run(["dotnet", str(cli), "project", "info", str(out / "creator.valencia")],
                                     capture_output=True, text=True, check=True).stdout)["project"]
    assert disk["revision"] == common["revision"]
    actual = disk["document"]["sequences"][0]
    assert len(actual["creator"]["profiles"]) == 4 and actual["creator"]["words"]
    assert float(actual["creator"]["masterGainDb"]) != 0 and any(a["envelope"] for a in actual["creator"]["audio"])
    jobs = []
    for profile in actual["creator"]["profiles"]:
        name = profile["name"] + ".mp4"
        job = mcp.call("run_export", **common, sequenceId=sequence, profileId=profile["id"], output=output(name))
        jobs.append(mcp.wait("export_status", pid, job["jobId"], 360))
    # Reconstruct the verifier's expected saved field representation from the actual persisted choices.
    operations = []
    for track in actual["tracks"]:
        for clip in track["clips"]:
            def pos(field):
                t = clip[field]["timestamp"]
                return dict(value=t["value"], numerator=t["numerator"], denominator=t["denominator"],
                            offsetTicks=clip[field]["offset"]["ticks"])
            operations.append(dict(operation="insert", trackId=track["id"],
                                   clip=dict(streamIndex=clip["streamIndex"], start=clip["start"]["ticks"],
                                             sourceIn=pos("sourceIn"), sourceOut=pos("sourceOut"), properties=clip["properties"])))
    operations.append(dict(operation="creator", settings=actual["creator"]))
    write_json(out / "creator-plan.json", dict(operations=operations))
    return dict(project=pid, reopenedRevision=disk["revision"], candidate=candidate, comparison=other["candidate"], summary=primary["summary"],
                profiles=actual["creator"]["profiles"], captionWords=len(actual["creator"]["words"]),
                masterGainDb=actual["creator"]["masterGainDb"], jobs=jobs)


def baseline(mcp, root, out, project_path, cli):
    legacy = json.loads(subprocess.check_output(["dotnet", str(cli), "project", "info", str(root / project_path)]))["project"]
    pid, rev = legacy["document"]["id"], legacy["revision"]
    mcp.rpc("tools/list", {})
    def raw(name, **fields):
        return mcp.rpc("tools/call", dict(name=name, arguments=dict(projectId=pid, **fields)), name)
    def value(response):
        return response["result"]["structuredContent"]["result"]
    assert raw("project_info")["result"]["isError"]  # Exceeds existing compact-query boundary.
    info = value(raw("project_info", compact=True))
    sequence = info["sequences"][0]["id"]
    tracks = value(raw("timeline_query", sequenceId=sequence))
    clip = tracks[0]["clips"][0]
    raw("analysis_result", key=TRANSCRIPT)
    raw("analysis_result", key=AUDIO)
    match = value(raw("transcript_query", key=TRANSCRIPT, query="important announcement", limit=5))
    raw("analysis_result", key=AUDIO, kind="silence", limit=5)
    t = time.perf_counter()
    start = int(clip["start"]["ticks"])
    cut = str(start + CLOCK)
    op = dict(operation="split", clipId=clip["id"], at=cut,
              rightClipId=str(uuid.uuid5(uuid.NAMESPACE_URL, "valencia-m3a-split")))
    plan = dict(schemaVersion=1, projectId=pid, projectRevision=rev, sequenceId=sequence, operations=[op])
    rejected = raw("validate_edit_plan", revision=rev,
                   plan=dict(plan, operations=[dict(op, at=str(start))]))
    assert rejected["result"]["isError"]
    assert value(raw("validate_edit_plan", revision=rev, plan=plan))["valid"]
    construction = round((time.perf_counter() - t) * 1000, 2)
    review = value(raw("review_timeline", revision=rev, sequenceId=sequence, plan=plan,
                       selection=dict(kind="contactSheet", atTicks=[str(start), cut], maxEdge=320),
                       output=(out / "baseline.png").relative_to(root).as_posix()))
    assert value(raw("apply_edit_plan", revision=rev, plan=plan))["committed"]
    legacy_script = out / "legacy-edit.ts"
    legacy_script.write_text(
        'using Valencia.Scripting; function run(api: ScriptApi): string { const s = api.FirstSequence(); '
        'const p = api.Plan(s); p.Split(api.FirstClip(s), api.Frames(30L, 30, 1), api.NewId()); '
        'api.Apply(p); api.Undo(); return api.Redo(); }', encoding="utf-8")
    replay = subprocess.run(["dotnet", str(cli), "script", "run", str(legacy_script), "--project", str(root / project_path)],
                            capture_output=True, text=True, check=True)
    return dict(comparableCalls=len(mcp.calls), planConstructionAndValidationIncludingCorrectionMs=construction,
                matchCount=len(match), callerArithmetic=["Read raw start ticks", "Add 254016000000 ticks for one second", "Serialize exact tick strings"],
                assetCount=info["assetCount"], trackClipCounts=[len(t["clips"]) for t in tracks], copeland=json.loads(replay.stdout),
                reviewKind=review["candidate"])


def main():
    p = argparse.ArgumentParser()
    p.add_argument("mode", choices=["baseline", "smoke", "creator"])
    p.add_argument("--cli", type=pathlib.Path, default=CLI)
    p.add_argument("--root", type=pathlib.Path, default=pathlib.Path("artifacts/local"))
    p.add_argument("--project", required=True)
    p.add_argument("--output", required=True, type=pathlib.Path, help="New directory inside --root")
    args = p.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    mcp = Mcp(args.cli, args.root, args.project, args.output / "stderr.log")
    try:
        details = dict(baseline=baseline, smoke=smoke, creator=creator)[args.mode](mcp, args.root, args.output, args.project, args.cli)
        write_json(args.output / "evidence.json", dict(harness="deterministic; no model or human intervention counts",
                   mode=args.mode, calls=mcp.calls, **details))
    finally:
        write_json(args.output / "calls.json", mcp.calls)
        mcp.close()


if __name__ == "__main__":
    main()
