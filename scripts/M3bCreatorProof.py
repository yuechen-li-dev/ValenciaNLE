"""Qualify the packaged creator workflow using the controlled M2d source."""
import argparse
import ctypes
import json
import pathlib
import re
import shutil
import tempfile
import time
import uuid

from M3aAgentProof import AUDIO, SCENE, TRANSCRIPT
from M3bReleaseProof import App, CLOCK, Session, dump, wait_for


def renderer_resources(process):
    counters = (ctypes.c_size_t * 9)()
    ctypes.cast(counters, ctypes.POINTER(ctypes.c_uint32))[0] = ctypes.sizeof(counters)
    times = (ctypes.c_uint64 * 4)()
    handle = ctypes.c_void_p(int(process._handle))
    memory = ctypes.windll.psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters[0])
    cpu = ctypes.windll.kernel32.GetProcessTimes(handle, *(ctypes.byref(times, index * 8) for index in range(4)))
    return dict(rendererPeakWorkingSetBytes=counters[1] if memory else None,
                rendererCpuSeconds=(times[2] + times[3]) / 10_000_000 if cpu else None)


def reference_plan(sequence):
    operations = []
    for track in sequence["tracks"]:
        for clip in track["clips"]:
            def position(name):
                point = clip[name]
                return dict(point["timestamp"], offsetTicks=point["offset"]["ticks"])
            operations.append(dict(operation="insert", trackId=track["id"], clip=dict(
                streamIndex=clip["streamIndex"], start=clip["start"]["ticks"],
                sourceIn=position("sourceIn"), sourceOut=position("sourceOut"))))
    operations.append(dict(operation="creator", settings=sequence["creator"]))
    return dict(operations=operations)


def measure(app, profile, job):
    output = pathlib.Path(job["output"])
    codec = {"av1": "av1", "vp9": "vp9", "h264": "h264"}[job["encoder"]]
    audio = "aac" if codec == "h264" else "opus"
    basic = app.independent(output, codec, audio, profile["width"], profile["height"])
    probe = basic["probe"]["format"]
    duration = (int(profile["end"]["ticks"]) - int(profile["start"]["ticks"])) / CLOCK
    assert abs(float(probe["duration"]) - duration) < .08, (profile, probe)
    decoded = app.run(app.ffmpeg, "-hide_banner", "-nostats", "-i", output, "-map", "0:a:0",
                      "-af", "ebur128=peak=true", "-f", "null", "-", timeout=240).stderr.decode(errors="replace")
    summary = decoded.rsplit("Summary:", 1)[-1]
    lufs = float(re.search(r"I:\s*([\-\d.]+) LUFS", summary)[1])
    peak = float(re.search(r"Peak:\s*([\-\d.]+) dBFS", summary)[1])
    assert abs(lufs + 16) <= 1 and peak <= -1, (output, lufs, peak)
    return dict(profile=profile["name"], expectedSeconds=duration, job=job, independent=basic,
                outsideEngineIntegratedLufs=lufs, outsideEngineTruePeakDb=peak)


def run(app, fixture, cache):
    started = time.monotonic()
    project = app.work / "three-hour.valencia"
    shutil.copy2(fixture / project.name, project)
    shutil.copy2(fixture / "three-hour.mkv", app.work / "three-hour.mkv")
    keys = [TRANSCRIPT, AUDIO, SCENE]
    if cache:
        mirror = app.work / ".valencia-analysis"
        mirror.mkdir()
        for key in keys:
            shutil.copy2(cache / (key + ".vca"), mirror)
    session = Session(app, project, mcp=True)
    output = app.work / "creator.valencia"
    try:
        fresh = []
        if not cache:
            asset_id = app.cmd("project", "info", project)["project"]["document"]["assets"][0]["id"]
            requests = [dict(kind="transcript", streamIndex=1, transcription=dict(alignment="dtw")),
                        dict(kind="audio", streamIndex=1),
                        dict(kind="scenes", streamIndex=0, scenes=dict(everyNthFrame=150))]
            for request in requests:
                timer = time.monotonic()
                job = session.call("start_analysis", projectId=session.project, request=dict(request, assetId=asset_id))

                def analysis_ready():
                    status = session.call("analysis_status", projectId=session.project, jobId=job["jobId"])
                    return status if status["state"] not in ("running", "queued") else None

                status = wait_for(analysis_ready, 900)
                assert status["state"] == "completed", status
                fresh.append(dict(kind=request["kind"], seconds=time.monotonic() - timer, status=status))
            keys = [item["status"]["key"] for item in fresh]
        transcript, audio, scenes = keys
        footage = session.product("footage_query", kind="summary", artifacts=keys)
        asset = next(item["asset"] for item in footage["items"] if item["analysisCount"] >= 3)
        words = session.product("footage_query", asset=asset, stream=1, kind="matches",
                                transcriptContains="important announcement", artifacts=[transcript, audio], limit=8)
        assert words["total"] > 0, words
        sequence = session.product("edit_objects", kind="sequence", handle=session.sequence)["items"][0]["id"]
        initial = session.product("start_creator", request=dict(sequenceId=sequence, transcriptKey=transcript,
            audioKey=audio, sceneKey=scenes, keywords=["important announcement"], musicStreamIndices=[2],
            width=320, height=180, shorts=3))

        def ready():
            reply = session.call("creator_status", projectId=session.project, jobId=initial["jobId"])
            status = reply.get("status", reply)
            return status if status["state"] not in ("running", "queued") else None

        status = wait_for(ready, 480)
        assert status["state"] == "completed", status
        candidate = session.product("agent_plan", action="prepare", creatorKey=status["planKey"])
        review = session.product("observe", mode="paired", candidate=candidate["candidate"], at=["0s", "4s", "8s"],
                                 beforeOutput="creator-before.png", output="creator-after.png")
        assert review["details"]["after"]["samples"]
        checkpoint = session.product("agent_checkpoint", action="create", durable=True, name="Before creator cut")
        session.revision = session.product("agent_plan", action="commit", candidate=candidate["candidate"])["resultingRevision"]
        session.revision = session.product("agent_history", action="undo")["resultingRevision"]
        session.revision = session.product("agent_history", action="redo")["resultingRevision"]
        session.product("agent_history", action="save", output=output.name)
        disk = app.cmd("project", "info", output)["project"]
        assert disk["revision"] == session.revision
        actual = disk["document"]["sequences"][0]
        dump(app.work / "creator-plan.json", reference_plan(actual))
        settings = actual["creator"]
        profiles = settings["profiles"]
        assert len(profiles) == 4 and len(settings["words"]) > 0 and any(a.get("envelope") for a in settings["audio"])
        render_start = time.monotonic()
        items = [dict(sequence=session.sequence, profile=profile["id"],
                      output=profile["name"] + ".webm", encoder="vp9" if ix == 0 else "av1",
                      finish=dict(targetLufs=-16, truePeakDb=-1)) for ix, profile in enumerate(profiles)]
        queued = session.product("render_queue", action="batch", items=items)
        assert queued["complete"], queued
        jobs = [session.finish(job["jobId"], timeout=1800) for job in queued["jobs"]]
        batch_seconds = time.monotonic() - render_start
        compatibility = session.call("export_options", projectId=session.project, probe=True)["encoders"]
        if next(item for item in compatibility if item["id"] == "h264")["available"]:
            job = session.product("render_queue", action="enqueue", item=dict(items[-1], output="short-3.mp4", encoder="h264"))
            jobs.append(session.finish(job["jobId"]))
        resources = renderer_resources(session.process)
    finally:
        session.close()
    evidence = [measure(app, profile, job) for profile, job in zip(profiles + [profiles[-1]], jobs)]
    reopened = Session(app, output, mcp=True)
    try:
        assert len(reopened.product("render_queue", action="list")) == len(jobs)
        assert reopened.product("agent_checkpoint", action="inspect", durable=True,
                                name="Before creator cut")["integrity"] == checkpoint["integrity"]
    finally:
        reopened.close()
    return dict(source=str(app.work / "three-hour.mkv"), sourceSeconds=10800,
                analysis="reused verified M2d artifacts" if cache else fresh,
                transcriptsMatched=words["total"], savedProject=str(output), revision=disk["revision"],
                creatorBefore=str(app.work / "creator-before.png"), creatorAfter=str(app.work / "creator-after.png"),
                captionWords=len(settings["words"]), renders=evidence, batchSeconds=batch_seconds,
                elapsedSeconds=time.monotonic() - started, resources=resources, compatibility=compatibility)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle", type=pathlib.Path, required=True)
    parser.add_argument("--fixture", type=pathlib.Path, required=True)
    starting = parser.add_mutually_exclusive_group(required=True)
    starting.add_argument("--cache", type=pathlib.Path)
    starting.add_argument("--model-dir", type=pathlib.Path)
    parser.add_argument("--report", type=pathlib.Path, required=True)
    parser.add_argument("--compat", type=pathlib.Path)
    args = parser.parse_args()
    work = pathlib.Path(tempfile.gettempdir()) / ("Valencia-creator-" + uuid.uuid4().hex)
    app = App(args.bundle, work, args.compat)
    if args.model_dir:
        app.env["VALENCIA_MODEL_DIR"] = str(args.model_dir.resolve())
    print(f"Isolated creator workspace {work}", flush=True)
    dump(args.report, run(app, args.fixture, args.cache))
