"""Analyze the full high-resolution fixture and qualify its registered creator workflow."""
import argparse
import hashlib
import json
import os
import pathlib
import subprocess
import time

from M3bReleaseProof import App, Session, dump


def run(args):
    app = App(args.bundle, args.work, args.compat)
    app.env.update(VALENCIA_MODEL_DIR=str(args.models.resolve()), VALENCIA_VIDEO_DECODER="d3d11va",
                   VALENCIA_D3D11_VENDOR_ID="0x10de")
    media = app.work / "three-hours.mkv"
    os.link(args.source.resolve(), media)
    fixture_env = dict(app.env, VALENCIA_FFMPEG_DIR=str(app.bundle / "native"))
    fixture_env["PATH"] = str(app.bundle / "native") + os.pathsep + fixture_env["PATH"]
    subprocess.run([str(args.fixture_builder.resolve()), str(media), str(app.work), "--project-only"],
                   env=fixture_env, check=True, timeout=60)
    original = app.work / "source.valencia"
    fingerprint = hashlib.sha256(original.read_bytes()).hexdigest()
    asset = app.cmd("project", "info", original)["project"]["document"]["assets"][0]
    session = Session(app, original, mcp=True)
    started = time.monotonic()
    try:
        artifacts = {}
        for kind in ["audio", "transcript"]:
            request = dict(assetId=asset["id"], streamIndex=1, kind=kind)
            if kind == "transcript":
                request["transcription"] = dict(alignment="dtw")
            job = session.call("start_analysis", projectId=session.project, request=request)["jobId"]
            deadline = time.monotonic() + 240
            while True:
                status = session.call("analysis_status", projectId=session.project, jobId=job)
                if status["state"] != "running":
                    break
                if time.monotonic() >= deadline:
                    session.call("cancel_analysis", projectId=session.project, jobId=job)
                    raise TimeoutError(f"{kind} exceeded its qualification time budget")
                time.sleep(2)
            assert status["state"] == "completed", status
            artifacts[kind] = status
            print(kind, json.dumps(status), flush=True)

        settings = dict(audio=artifacts["audio"]["key"], transcript=artifacts["transcript"]["key"],
                        ranges=[dict(start="7201s", end="7211s", score=1, reason="Controlled speech"),
                                dict(start="7213s", end="7225s", score=.9, reason="Second controlled speech passage")],
                        target="18s", minimum="3s", maximum="12s", shorts=1, shortDuration="5s",
                        width=1920, height=1080, musicStreams=[2])
        approval = session.product("editorial_workflow", action="prepare", workflow="creator", settings=settings)
        candidate = approval["preparation"]["candidate"]
        review = session.product("observe", mode="paired", candidate=candidate, at=["1s", "4s", "7s"],
                                 beforeOutput="before.png", output="after.png")
        assert review["details"]["after"]["samples"]
        session.revision = session.product("agent_plan", action="commit", candidate=candidate)["resultingRevision"]
        session.product("agent_history", action="save", output="creator.valencia")
        assert hashlib.sha256(original.read_bytes()).hexdigest() == fingerprint
    finally:
        session.close()

    saved = app.work / "creator.valencia"
    sequence = app.cmd("project", "info", saved)["project"]["document"]["sequences"][0]
    creator = sequence["creator"]
    words = len(creator["words"])
    profiles = creator["profiles"]
    assert words > 0 and len(profiles) == 2
    reopened = Session(app, saved, mcp=True)
    try:
        items = [
            dict(sequence=reopened.sequence, encoder="h264-nvenc", profile=profile["id"], output=f"creator-{index}.mp4")
            for index, profile in enumerate(profiles)
        ]
        queued = reopened.product("render_queue", action="batch", items=items)
        assert queued["complete"], queued
        renders = [reopened.finish(job["jobId"], timeout=180) for job in queued["jobs"]]
    finally:
        reopened.close()

    independently = [app.independent(app.work / item["output"], "h264", "aac", profile["width"], profile["height"])
                     for item, profile in zip(items, profiles)]
    dump(args.report, dict(outcome="passed", workspace=str(app.work), analyzedSourceSeconds=10800, words=words,
                           planSha256=approval["planSha256"], artifacts=artifacts, settings=settings,
                           renders=renders, independently=independently, wallSeconds=time.monotonic() - started))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for option in ["bundle", "work", "compat", "source", "models", "fixture-builder", "report"]:
        parser.add_argument("--" + option, type=pathlib.Path, required=True)
    run(parser.parse_args())
