"""Kill test-owned writers while real Deliverance temporary files are present."""
import argparse
import hashlib
import pathlib
import shutil
import stat
import subprocess
import tempfile
import time
import uuid

from M3bReleaseProof import App, Session, dump, wait_for


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def exercise(app, probe, seed, mode):
    run = app.work / mode
    run.mkdir()
    project = run / "primary.valencia"
    shutil.copy2(seed, project)
    shutil.copy2(seed.parent / "sample.mkv", run / "sample.mkv")
    previous = sha(project)
    original = app.cmd("project", "info", project)["project"]["revision"]
    recovery = app.work / "user-data/Recovery"
    child = subprocess.Popen(["dotnet", str(probe), mode, str(project), str(recovery)],
                             cwd=run, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    observed = None
    try:
        deadline = time.monotonic() + 30
        while child.poll() is None and time.monotonic() < deadline:
            candidates = (run.glob("primary.valencia.tmp-*") if mode == "save" else
                          recovery.glob("*/generation-000000000002.valencia.tmp-*"))
            observed = next(iter(candidates), None)
            if observed:
                child.kill()
                break
            time.sleep(.001)
    finally:
        if child.poll() is None:
            child.kill()
        child.wait(timeout=10)
    assert observed, child.stderr.read().decode(errors="replace")
    assert sha(project) == previous
    assert app.cmd("project", "info", project)["project"]["revision"] == original
    record = dict(killedPid=child.pid, exitCode=child.returncode, observedTemporary=str(observed),
                  primarySha256=previous, unmodifiedRevision=original)
    if mode == "autosave":
        snapshots = app.cmd("recovery", "list")
        assert len(snapshots) == 1 and snapshots[0]["integrity"] and not snapshots[0]["missingMedia"], snapshots
        assert int(snapshots[0]["metadata"]["generation"]) == 1, snapshots
        restored = run / "restored.valencia"
        result = app.cmd("recovery", "recover", snapshots[0]["id"], "--output", restored)
        assert app.cmd("project", "info", restored)["project"]["revision"] == original, result
        record["lastGoodRecovery"] = snapshots[0]
    return record


def checked_writers(app, probe, project):
    original = sha(project)
    project.chmod(stat.S_IREAD)
    try:
        denied = subprocess.run(["dotnet", str(probe), "save", str(project), str(app.work / "unused")],
                                cwd=app.work, capture_output=True, timeout=30)
        assert denied.returncode != 0 and b"Atomic write failed" in denied.stderr, denied.stderr
        assert sha(project) == original
    finally:
        project.chmod(stat.S_IREAD | stat.S_IWRITE)
    previous_agent = Session(app, project)
    control = app.work / "writer"
    stale = subprocess.Popen(["dotnet", str(probe), "stale", str(project), str(control)],
                             cwd=app.work, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        wait_for(pathlib.Path(str(control) + ".ready").exists, timeout=10)
        subprocess.run(["dotnet", str(probe), "advance", str(project), str(control)],
                       cwd=app.work, capture_output=True, timeout=15, check=True)
        newer = sha(project)
        assert newer != original
        pathlib.Path(str(control) + ".go").touch()
        _, error = stale.communicate(timeout=15)
        assert stale.returncode != 0 and b"ProjectConflictException" in error, error
        previous_agent.send(dict(method="agent_history", request=dict(projectId=previous_agent.project,
                            revision=previous_agent.revision, action="save", output=str(project))))
        refused = previous_agent.read()
        assert not refused["ok"] and refused["issue"]["code"] == "OUTPUT_EXISTS", refused
        assert sha(project) == newer
        return dict(deniedWriteExitCode=denied.returncode, priorSha256=original,
                    retainedRevision=app.cmd("project", "info", project)["project"]["revision"], retainedSha256=newer,
                    staleWriter="ProjectConflictException", originalAgentSave=refused["issue"])
    finally:
        previous_agent.close()
        if stale.poll() is None:
            stale.kill()
            stale.wait(timeout=10)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle", type=pathlib.Path, required=True)
    parser.add_argument("--probe", type=pathlib.Path, required=True)
    parser.add_argument("--report", type=pathlib.Path, required=True)
    args = parser.parse_args()
    work = pathlib.Path(tempfile.gettempdir()) / ("Valencia-storage-" + uuid.uuid4().hex)
    app = App(args.bundle, work)
    sample = work / "sample.mkv"
    app.encode_fixture(sample)
    source = work / "seed.valencia"
    app.cmd("project", "create", source, "--media", sample, "--width", "320", "--height", "180")
    probe = args.probe.resolve()
    records = {mode: exercise(app, probe, source, mode) for mode in ("save", "autosave")}
    records["writeProtection"] = checked_writers(app, probe, source)
    dump(args.report, records)
