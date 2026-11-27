"""Exercise a deep long-source edit and registered editorial templates on the packaged hosts."""
import argparse
import hashlib
import os
import pathlib
import subprocess
import time

from M3bReleaseProof import App, Session, dump


def run(args):
    app = App(args.bundle, args.work, args.compat)
    app.env.update(VALENCIA_VIDEO_DECODER="d3d11va", VALENCIA_D3D11_VENDOR_ID="0x10de", VALENCIA_EFFECTS_BACKEND="vulkan")
    for source, alias in [(args.long, "three-hours.mkv"), (args.other, "second.mp4")]:
        os.link(source.resolve(), app.work / alias)
    fixture_env = dict(app.env, VALENCIA_FFMPEG_DIR=str(app.bundle / "native"))
    fixture_env["PATH"] = str(app.bundle / "native") + os.pathsep + fixture_env["PATH"]
    subprocess.run([str(args.fixture_builder.resolve()), str(app.work / "three-hours.mkv"), str(app.work),
                    "--slice-project", str(app.work / "second.mp4")], env=fixture_env, check=True, timeout=60)
    original = app.work / "source.valencia"
    before = hashlib.sha256(original.read_bytes()).hexdigest()
    session = Session(app, original, mcp=True)
    try:
        capability = session.call("export_options", projectId=session.project, probe=True)["videoDecode"]
        assert capability["deviceAvailable"], capability
        prepared = session.product("editorial_workflow", action="compose", steps=[
            dict(workflow="lower-third", settings=dict(start="0.6s", end="2.5s", text="Valencia", subtitle="Long recording")),
            dict(workflow="make-short", settings=dict(start="0s", end="4s", width=1080, height=1920)),
        ])
        candidate = prepared["preparation"]["candidate"]
        review = session.product("observe", mode="paired", candidate=candidate, at=["0.9s", "1.5s", "3.5s", "6.25s", "7s"],
                                 beforeOutput="before.png", output="after.png")
        assert review["details"]["after"]["samples"]
        session.revision = session.product("agent_plan", action="commit", candidate=candidate)["resultingRevision"]
        session.product("agent_history", action="save", output="edited.valencia")
        assert hashlib.sha256(original.read_bytes()).hexdigest() == before
    finally:
        session.close()

    saved = app.work / "edited.valencia"
    sequence = app.cmd("project", "info", saved)["project"]["document"]["sequences"][0]
    short = next(profile for profile in sequence["creator"]["profiles"] if profile["height"] > profile["width"])
    reopened = Session(app, saved, mcp=True)
    started = time.monotonic()
    try:
        queued = reopened.product("render_queue", action="batch", items=[
            dict(sequence=reopened.sequence, encoder="h264-nvenc", output="landscape.mp4"),
            dict(sequence=reopened.sequence, encoder="h264-nvenc", profile=short["id"], output="short.mp4"),
        ])
        renders = [reopened.finish(job["jobId"]) for job in queued["jobs"]]
    finally:
        reopened.close()
    independently = [app.independent(app.work / "landscape.mp4", "h264", "aac", 1920, 1080),
                     app.independent(app.work / "short.mp4", "h264", "aac", 1080, 1920)]
    gap = app.decode_frame(app.work / "landscape.mp4", "6.25")
    second = app.decode_frame(app.work / "landscape.mp4", "7")
    assert len(gap) == len(second) == 1920 * 1080 and max(gap) <= 20 and max(second) > 160
    dump(args.report, dict(outcome="passed", capability=capability, revision=session.revision, edited=str(saved),
                           applied=prepared["planSha256"], renders=renders, independently=independently,
                           gapMaximum=max(gap), secondSourceFrameSha256=hashlib.sha256(second).hexdigest(),
                           wallSeconds=time.monotonic() - started, workspace=str(app.work)))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for option in ["bundle", "work", "compat", "long", "other", "fixture-builder", "report"]:
        parser.add_argument("--" + option, type=pathlib.Path, required=True)
    run(parser.parse_args())
