"""Exercise an installed or extracted Valencia on an isolated Windows host environment."""
import argparse
import hashlib
import json
import os
import pathlib
import subprocess
import tempfile
import time
import uuid
from fractions import Fraction

CLOCK = 254_016_000_000


def dump(path, value):
    pathlib.Path(path).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def wait_for(test, timeout=40):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = test()
        if result:
            return result
        time.sleep(.02)
    raise TimeoutError("Expected qualification state did not appear.")


class App:
    def __init__(self, bundle, work, compat=None):
        self.bundle = bundle.resolve()
        self.work = work.resolve()
        self.work.mkdir(parents=True, exist_ok=False)
        self.cli = self.bundle / "Valencia.Cli.exe"
        self.desktop = self.bundle / "Valencia.App.exe"
        self.ffmpeg = self.bundle / "native/ffmpeg.exe"
        self.ffprobe = self.bundle / "native/ffprobe.exe"
        keep = {"SYSTEMROOT", "WINDIR", "SYSTEMDRIVE", "TEMP", "TMP", "COMSPEC", "PATHEXT",
                "USERPROFILE", "APPDATA", "LOCALAPPDATA", "PROGRAMDATA", "USERNAME", "NUMBER_OF_PROCESSORS"}
        self.env = {key: value for key, value in os.environ.items() if key.upper() in keep}
        windows = pathlib.Path(self.env.get("SYSTEMROOT", "C:/Windows"))
        self.env["PATH"] = os.pathsep.join((str(windows / "System32"), str(windows)))
        self.env["VALENCIA_DATA_DIR"] = str(self.work / "user-data")
        self.env["VALENCIA_AUTOSAVE_SECONDS"] = "5"
        if compat:
            self.env["VALENCIA_COMPAT_FFMPEG_EXE"] = str(compat.resolve())

    def run(self, exe, *args, timeout=160, check=True):
        return subprocess.run([str(exe), *map(str, args)], env=self.env, cwd=self.work,
                              capture_output=True, timeout=timeout, check=check)

    def cmd(self, *args, timeout=160):
        result = self.run(self.cli, *args, timeout=timeout, check=False)
        assert result.returncode == 0, (args, result.returncode, result.stdout.decode(errors="replace"), result.stderr.decode(errors="replace"))
        return json.loads(result.stdout)

    def encode_fixture(self, path):
        self.run(self.ffmpeg, "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30000/1001:d=6",
                 "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=6",
                 "-f", "lavfi", "-i", "sine=frequency=330:sample_rate=48000:duration=6",
                 "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "mpeg4", "-q:v", "2",
                 "-c:a", "pcm_s16le", path)

    def decode_frame(self, path, seconds):
        return self.run(self.ffmpeg, "-v", "error", "-ss", seconds, "-i", path, "-map", "0:v:0",
                        "-frames:v", "1", "-pix_fmt", "gray", "-f", "rawvideo", "-").stdout

    def independent(self, path, codec, audio, width, height, source=None):
        probe = json.loads(self.run(self.ffprobe, "-v", "error", "-show_entries",
            "format=duration,format_name:stream=codec_type,codec_name,width,height,avg_frame_rate,sample_rate", "-of", "json", path).stdout)
        video, sound = probe["streams"]
        assert (video["codec_name"], sound["codec_name"], video["width"], video["height"]) == (codec, audio, width, height), probe
        pcm = self.run(self.ffmpeg, "-v", "error", "-ss", "0.25", "-i", path, "-map", "0:a:0",
                       "-t", "0.25", "-ac", "2", "-ar", "48000", "-f", "s16le", "-").stdout
        assert len(pcm) >= 47000 and len(set(pcm)) > 5
        frame = self.decode_frame(path, "0.3")
        assert len(frame) == width * height and max(frame) > 160
        evidence = dict(probe=probe, pcmBytes=len(pcm), frameSha256=hashlib.sha256(frame).hexdigest())
        if source:
            black = self.decode_frame(path, "1.2")
            assert black and max(black) <= 20, max(black)
            original = self.decode_frame(source, "0.3")
            assert len(original) == len(frame), (len(original), len(frame))
            mae = sum(abs(a-b) for a, b in zip(original, frame)) / len(frame)
            assert mae < 14, mae
            evidence.update(gapMaxLuma=max(black), sourceFrameLumaMae=mae)
        return evidence


class Session:
    def __init__(self, app, project, mcp=False):
        self.mcp = mcp
        self.id = 0
        args = ["mcp", "--root", str(app.work), "--project", project.name] if mcp else ["agent", "run", str(project)]
        self.log = (app.work / ("mcp.stderr" if mcp else f"agent-{uuid.uuid4().hex[:6]}.stderr")).open("wb")
        self.process = subprocess.Popen([str(app.cli), *args], cwd=app.work, env=app.env,
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.log)
        if mcp:
            init = self.rpc("initialize", {"protocolVersion": "2025-11-25", "clientInfo": {"name": "M3b proof", "version": "1"}, "capabilities": {}})
            assert init["result"]["protocolVersion"] == "2025-11-25"
            self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        info = self.call("session_info")
        self.project = info["projectId"]
        self.revision = info["revision"]
        self.sequence = info["activeSequence"]

    def send(self, value):
        self.process.stdin.write(json.dumps(value).encode() + b"\n")
        self.process.stdin.flush()

    def read(self):
        data = self.process.stdout.readline()
        if not data:
            raise RuntimeError("Valencia CLI ended before replying.")
        return json.loads(data)

    def rpc(self, method, fields):
        self.id += 1
        self.send({"jsonrpc": "2.0", "id": self.id, "method": method, "params": fields})
        response = self.read()
        assert response["id"] == self.id and "error" not in response, response
        return response

    def call(self, method, **request):
        if self.mcp:
            response = self.rpc("tools/call", {"name": method, "arguments": request})["result"]
            reply = response["structuredContent"]["result"]
            if "agentVersion" not in reply:
                assert not response.get("isError"), (method, response)
                return reply
        else:
            self.send({"method": method, "request": request})
            reply = self.read()
        assert reply["ok"], (method, reply)
        return reply["result"]

    def product(self, method, **fields):
        return self.call(method, projectId=self.project, revision=self.revision, **fields)

    def status(self, job):
        return self.product("render_queue", action="status", job=job)

    def finish(self, job, timeout=240):
        result = wait_for(lambda: (s if (s := self.status(job))["state"] not in ("queued", "running", "checking") else None), timeout)
        assert result["state"] == "completed", result
        return result

    def close(self, kill=False):
        if kill and self.process.poll() is None:
            self.process.kill()
        self.process.stdin.close()
        try:
            self.process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        self.log.close()


def proof(app, kind, no_crash=False):
    work = app.work
    doctor = app.cmd("doctor")
    assert doctor["packaged"] and pathlib.Path(doctor["runtimePath"]).parent == app.bundle, doctor
    assert all(check["available"] for check in doctor["checks"] if check["essential"]), doctor
    source = work / "generated.mkv"
    app.encode_fixture(source)
    draft = work / "fixture.valencia"
    initial = app.cmd("project", "create", draft, "--media", source, "--width", "320", "--height", "180",
                      "--fps-num", "30000", "--fps-den", "1001")
    asset = initial["document"]["assets"][0]["id"]
    cached = app.cmd("analyze", "audio", draft, "--asset", asset, "--stream", "1")
    copeland = app.cmd("script", "run", app.bundle / "agent-time.ts", "--project", draft)
    assert copeland["success"] and json.loads(copeland["returnValue"])["ticks"] == "9007453270740993", copeland

    mcp = Session(app, draft, mcp=True)
    try:
        names = [t["name"] for t in mcp.rpc("tools/list", {})["result"]["tools"]]
        assert {"render_queue", "export_options", "recovery", "model_options"}.issubset(names)
        supported = mcp.call("model_options")["models"]
        assert int(supported[0]["downloadBytes"]) == 77_704_715
        capabilities = mcp.call("export_options", projectId=mcp.project, probe=True)["encoders"]
        assert all(x["available"] for x in capabilities if x["id"] in ("av1", "vp9", "archive"))
        if "VALENCIA_COMPAT_FFMPEG_EXE" not in app.env:
            assert not next(x for x in capabilities if x["id"] == "h264")["available"]
    finally:
        mcp.close()

    session = Session(app, draft)
    saved = work / "edited.valencia"
    matrix = []
    try:
        objects = session.product("edit_objects", kind="clip", sequence=session.sequence, at="0s")
        operations = []
        for ix, obj in enumerate(objects["items"]):
            clip = obj["handle"]
            middle, right = str(uuid.uuid4()), str(uuid.uuid4())
            operations += [
                dict(operation="split", clipId=clip, at="f30", rightClipId=middle),
                dict(operation="split", clipId=middle, at="f45", rightClipId=right),
                dict(operation="delete", clipId=middle),
            ]
            if ix == 0:
                operations += [dict(operation="color", clipId=right, color=dict(saturation=.8))]
        staged = session.product("agent_plan", action="prepare", plan=dict(sequence=session.sequence, operations=operations))
        session.product("observe", mode="paired", candidate=staged["candidate"], at=["f10", "f37", "f55"],
                        beforeOutput=str(work/"before.png"), output=str(work/"after.png"))
        checkpoint = session.product("agent_checkpoint", action="create", durable=True, name="Before the gap")
        session.revision = session.product("agent_plan", action="commit", candidate=staged["candidate"])["resultingRevision"]
        session.product("agent_history", action="save", output=str(saved))
        assert app.cmd("project", "info", saved)["project"]["revision"] == session.revision
        encoders = [("vp9", "vp9", "opus", "webm"), ("av1", "av1", "opus", "webm"),
                    ("archive", "ffv1", "flac", "mkv")]
        if "VALENCIA_COMPAT_FFMPEG_EXE" in app.env:
            standard = {entry[0] for entry in encoders}
            for item in capabilities:
                if item["available"] and item["id"] not in standard:
                    encoders.append((item["id"], item["codec"], item["audioCodec"], item["container"]))
        requested = []
        for encoder, codec, audio, extension in encoders:
            target = work / f"{encoder}.{extension}"
            item = dict(sequence=session.sequence, output=str(target), encoder=encoder)
            if encoder != "archive":
                item["finish"] = dict(targetLufs=-16, truePeakDb=-1)
            requested.append((item, codec, audio, target))
        jobs = []
        for pos in range(0, len(requested), 4):
            chunk = requested[pos:pos+4]
            queued = session.product("render_queue", action="batch", items=[r[0] for r in chunk])
            assert queued["complete"], queued
            jobs.extend(queued["jobs"])
            for status, (item, codec, audio, target) in zip(queued["jobs"], chunk):
                report = session.finish(status["jobId"])
                independent = app.independent(target, codec, audio, 320, 180, source)
                matrix.append(dict(encoder=item["encoder"], job=report, independent=independent))
    finally:
        session.close()

    reopened = Session(app, saved)
    try:
        listing = reopened.product("render_queue", action="list")
        assert len(listing) == len(matrix)
        assert reopened.product("agent_checkpoint", action="inspect", durable=True, name="Before the gap")["integrity"] == checkpoint["integrity"]
        assert reopened.product("render_queue", action="lint", job=matrix[0]["job"]["jobId"])["measured"]["videoCodec"] == "vp9"
    finally:
        reopened.close()

    crash = None
    export_crash = None
    if not no_crash:
        interrupted = Session(app, saved)
        failed_output = work / "interrupted.webm"
        try:
            job = interrupted.product("render_queue", action="enqueue", item=dict(
                sequence=interrupted.sequence, output=str(failed_output), preset="landscape-1440p60"))
            partial = work / (".valencia-export-" + job["jobId"].replace("-", "") + ".partial")
            wait_for(partial.exists, timeout=80)
            assert not failed_output.exists()
        finally:
            interrupted.close(kill=True)
        restarted = Session(app, saved)
        try:
            previous = restarted.status(job["jobId"])
            assert previous["state"] == "interrupted" and not failed_output.exists(), previous
            retry_path = work / "retried.webm"
            retry = restarted.product("render_queue", action="retry", job=job["jobId"], output=str(retry_path))
            cancelled = restarted.product("render_queue", action="cancel", job=retry["jobId"])
            terminal = wait_for(lambda: (s if (s := restarted.status(retry["jobId"]))["state"] == "cancelled" else None), 60)
            assert not retry_path.exists() and retry["revision"] == previous["revision"]
            export_crash = dict(killedPid=interrupted.process.pid, previous=previous, retry=retry,
                                cancelAcknowledged=cancelled["state"], cancelled=terminal)
        finally:
            restarted.close()

        appLog = (work/"desktop-process.log").open("wb")
        desktop = subprocess.Popen([str(app.desktop), str(source)], cwd=work, env=app.env, stdout=appLog, stderr=appLog)
        recovery_dir = pathlib.Path(app.env["VALENCIA_DATA_DIR"]) / "Recovery"
        try:
            recovery_file = wait_for(lambda: next(iter(recovery_dir.glob("*/generation-*.valencia")), None), 30)
            assert desktop.poll() is None
        finally:
            if desktop.poll() is None:
                desktop.kill()
            desktop.wait(timeout=10)
            appLog.close()
        records = app.cmd("recovery", "list")
        entry = next(r for r in records if pathlib.Path(r["file"]) == recovery_file)
        assert entry["integrity"] and "originalPath" not in entry["metadata"], entry
        recovered = work / "recovered.valencia"
        requested = app.cmd("recovery", "recover", entry["id"], "--output", recovered)
        assert app.cmd("project", "info", recovered)["project"]["revision"] == requested["revision"]
        crash = dict(killedPid=desktop.pid, exitCode=desktop.returncode, recovery=entry, recovered=requested)

    return dict(kind=kind, doctor=doctor, tools=names, capabilities=capabilities, copeland=copeland,
                analysisKind=cached.get("artifactKind", cached.get("kind", "audio")), renders=matrix, recoveryCrash=crash,
                exportCrash=export_crash, savedProject=str(saved), source=str(source))


def preset_proof(app):
    source = app.work / "preset-source.mkv"
    app.run(app.ffmpeg, "-v", "error", "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30000/1001:d=1.5",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1.5",
            "-c:v", "mpeg4", "-q:v", "2", "-c:a", "pcm_s16le", source)
    project = app.work / "presets.valencia"
    app.cmd("project", "create", project, "--media", source, "--width", "320", "--height", "180",
            "--fps-num", "30000", "--fps-den", "1001")
    session = Session(app, project)
    results = []
    try:
        presets = session.call("export_options", projectId=session.project)["presets"]
        assert len(presets) == 5
        for preset in presets:
            codec, audio, extension = {"av1": ("av1", "opus", "webm"), "vp9": ("vp9", "opus", "webm"),
                                       "archive": ("ffv1", "flac", "mkv")}[preset["encoder"]]
            output = app.work / f"{preset['id']}.{extension}"
            job = session.product("render_queue", action="enqueue", item=dict(
                sequence=session.sequence, output=str(output), preset=preset["id"]))
            status = session.finish(job["jobId"])
            independent = app.independent(output, codec, audio, preset["width"], preset["height"])
            video = independent["probe"]["streams"][0]
            assert Fraction(video["avg_frame_rate"]) == Fraction(preset["frameRate"]["numerator"], preset["frameRate"]["denominator"]), video
            results.append(dict(preset=preset, job=status, independent=independent))
    finally:
        session.close()
    return results


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle", type=pathlib.Path, required=True)
    parser.add_argument("--work", type=pathlib.Path, default=None)
    parser.add_argument("--report", type=pathlib.Path, required=True)
    parser.add_argument("--compat", type=pathlib.Path)
    parser.add_argument("--no-crash", action="store_true")
    parser.add_argument("--presets", action="store_true")
    parser.add_argument("--kind", choices=["installed", "portable"], default="portable")
    args = parser.parse_args()
    work = args.work or pathlib.Path(tempfile.gettempdir()) / ("Valencia-proof-" + uuid.uuid4().hex)
    app = App(args.bundle, work, args.compat)
    print(f"Isolated workspace {app.work}", flush=True)
    dump(args.report, preset_proof(app) if args.presets else proof(app, args.kind, args.no_crash))
