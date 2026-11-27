"""Run the M3c creator workflow through the bundled Copeland, CLI and MCP paths."""
import argparse
import hashlib
import json
import pathlib
import shutil
import tempfile
import time
import uuid

from M3aAgentProof import AUDIO, SCENE, TRANSCRIPT
from M3bCreatorProof import measure, reference_plan, renderer_resources
from M3bReleaseProof import App, Session, dump


def run(app, fixture, cache, report):
    original = app.work / "three-hour.valencia"
    shutil.copy2(fixture / original.name, original)
    shutil.copy2(fixture / "three-hour.mkv", app.work / "three-hour.mkv")
    for destination in [app.work / ".valencia-analysis", app.work / "user-data/Analysis-v1"]:
        destination.mkdir(parents=True)
        for key in [AUDIO, SCENE, TRANSCRIPT]:
            shutil.copy2(cache / (key + ".vca"), destination)
    settings = dict(audio=AUDIO, transcript=TRANSCRIPT, scenes=SCENE, topics=["important announcement"],
                    width=320, height=180, shorts=3, musicStreams=[2])
    settings_path = app.work / "creator.settings.obj.ts"
    settings_path.write_text("const $value: EditorialSettings = " + json.dumps(settings, indent=2) + ";\n", encoding="utf-8")
    plan_file = app.work / "canonical-plan.json"
    cli_started = time.monotonic()
    cli = app.cmd("workflow", "prepare", "creator", "--project", original,
                  "--settings", settings_path, "--plan-out", plan_file)
    cli_seconds = time.monotonic() - cli_started
    canonical = json.loads(plan_file.read_text(encoding="utf-8"))
    assert cli["valid"] and len(canonical["operations"]) <= 500

    # Invoke the same policy from an ordinary trusted, compiled Copeland script.
    source = app.work / "creator-policy.workflow.ts"
    source.write_text("using Valencia.Scripting;\nfunction run(api: ScriptApi): string {\n"
                      "    return api.BuildEditorialPlan(\"creator\", api.Argument(\"settings\", \"{}\"));\n}\n", encoding="utf-8")
    compiled = app.cmd("workflow", "preview", source, "--project", original, "--args", json.dumps(dict(settings=json.dumps(settings))))
    assert compiled["success"] and json.loads(compiled["script"]["returnValue"]) == canonical
    source_hash = hashlib.sha256(original.read_bytes()).hexdigest()
    saved = app.work / "creator.valencia"
    session = Session(app, original, mcp=True)
    try:
        tools = session.rpc("tools/list", {})["result"]["tools"]
        assert "editorial_workflow" in {tool["name"] for tool in tools}
        topics = session.product("footage_query", kind="topics", artifacts=[TRANSCRIPT], limit=8)
        assert topics["items"]
        starting = session.revision

        def timed(call):
            initial = session.request_bytes, session.response_bytes, session.id
            timepoint = time.monotonic()
            result = call()
            return result, dict(requestBytes=session.request_bytes-initial[0], responseBytes=session.response_bytes-initial[1],
                                calls=session.id-initial[2], seconds=time.monotonic()-timepoint)

        baseline, baseline_stats = timed(lambda: session.product("agent_plan", action="prepare", canonicalPlan=canonical))
        prepared, workflow_stats = timed(lambda: session.product("editorial_workflow", action="prepare", workflow="creator", settings=settings))
        assert prepared["preparation"]["summary"] == baseline["summary"]
        assert prepared["planSha256"].lower() == hashlib.sha256(plan_file.read_bytes()).hexdigest()
        alternate = session.product("editorial_workflow", action="prepare", workflow="creator",
                                    settings=dict(settings, captionSize=54, captionBackdrop="shadow"))
        primary, other = [row["preparation"]["candidate"] for row in (prepared, alternate)]
        session.product("agent_plan", action="compare", candidate=primary, other=other)
        review, review_stats = timed(lambda: session.product("observe", mode="paired", candidate=primary, at=["0s", "4s", "8s"],
                                                                beforeOutput="creator-before.png", output="creator-after.png"))
        assert review["details"]["after"]["samples"]
        checkpoint = session.product("agent_checkpoint", action="create", durable=True, name="Before editorial workflow")
        dump(app.work / "creator.editorial.json", dict(action="prepare", projectId=session.project, revision=starting,
                                                      workflow="creator", settings=settings))
        session.revision = session.product("agent_plan", action="commit", candidate=primary)["resultingRevision"]
        session.revision = session.product("agent_history", action="undo")["resultingRevision"]
        session.revision = session.product("agent_history", action="redo")["resultingRevision"]
        session.product("agent_history", action="save", output=saved.name)
        assert hashlib.sha256(original.read_bytes()).hexdigest() == source_hash
        actual = app.cmd("project", "info", saved)["project"]["document"]["sequences"][0]
        profiles = actual["creator"]["profiles"]
        assert len(profiles) == 4 and actual["creator"]["words"]
        dump(app.work / "creator-plan.json", reference_plan(actual))
        render_started = time.monotonic()
        items = [dict(sequence=session.sequence, profile=profile["id"], output=profile["name"] + ".webm",
                      encoder="vp9" if i == 0 else "av1", finish=dict(targetLufs=-16, truePeakDb=-1))
                 for i, profile in enumerate(profiles)]
        queued = session.product("render_queue", action="batch", items=items)
        assert queued["complete"], queued
        renders = [session.finish(job["jobId"], timeout=1200) for job in queued["jobs"]]
        batch_seconds = time.monotonic() - render_started
        resources = renderer_resources(session.process)
    finally:
        session.close()
    reopened = Session(app, saved, mcp=True)
    try:
        assert len(reopened.product("render_queue", action="list")) == 4
        restored = reopened.product("agent_checkpoint", action="inspect", durable=True, name="Before editorial workflow")
        assert restored["integrity"] == checkpoint["integrity"]
    finally:
        reopened.close()
    media = [measure(app, profile, job) for profile, job in zip(profiles, renders)]
    dump(report, dict(workspace=str(app.work), sourceSeconds=10800, planOperations=len(canonical["operations"]),
                      compiledCopelandCliMcpEqual=True, cachedAnalysis=[AUDIO, TRANSCRIPT, SCENE], topics=topics["total"],
                      beforeRevision=starting, savedRevision=session.revision, cliPlanSeconds=cli_seconds,
                      wireComparison=dict(canonicalSubmission=baseline_stats, registeredWorkflow=workflow_stats, review=review_stats),
                      renderSeconds=batch_seconds, resources=resources, renders=media, project=str(saved)))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle", type=pathlib.Path, required=True)
    parser.add_argument("--fixture", type=pathlib.Path, required=True)
    parser.add_argument("--cache", type=pathlib.Path, required=True)
    parser.add_argument("--report", type=pathlib.Path, required=True)
    args = parser.parse_args()
    work = pathlib.Path(tempfile.gettempdir()) / ("Valencia-M3c-" + uuid.uuid4().hex)
    print(f"Isolated M3c workspace {work}", flush=True)
    run(App(args.bundle, work), args.fixture, args.cache, args.report)
