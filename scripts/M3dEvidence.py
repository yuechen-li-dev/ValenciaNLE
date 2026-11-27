"""Summarize M3d raw logs and independently check the matched encoded files."""
import argparse
import hashlib
import json
import math
import pathlib
import re
import subprocess
from fractions import Fraction


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def distribution(items):
    values = sorted(items)
    return dict(samples=len(values), p50=values[math.ceil(.50 * len(values)) - 1],
                p95=values[math.ceil(.95 * len(values)) - 1], p99=values[math.ceil(.99 * len(values)) - 1],
                maximum=values[-1])


def ffprobe(path, *args):
    result = subprocess.run([str(path), "-v", "error", *map(str, args), "-of", "json"],
                            check=True, capture_output=True, timeout=60)
    return json.loads(result.stdout)


def check_seek(probe, trial):
    stream = next(stream for stream in trial["asset"]["streams"] if stream["kind"] == "video")
    checked = []
    for row in trial["measurements"][1::10]:
        actual = row["sourceTimestamps"][0]
        ticks = int(row["ticks"])
        timebase = Fraction(int(actual["numerator"]), int(actual["denominator"]))
        wanted = Fraction(ticks, 254016000000) + int(stream["start"]["value"]) * timebase
        lower = max(0, float(wanted) - 5)
        frames = ffprobe(probe, "-select_streams", "v:0", "-read_intervals", f"{lower:.6f}%+12", "-show_frames",
                         "-show_entries", "frame=best_effort_timestamp", trial["source"])["frames"]
        eligible = [int(frame["best_effort_timestamp"]) for frame in frames
                    if "best_effort_timestamp" in frame and int(frame["best_effort_timestamp"]) * timebase <= wanted]
        assert max(eligible) == int(actual["value"]), (wanted, max(eligible), actual)
        checked.append(dict(requestedTicks=ticks, independentPts=max(eligible)))
    return checked


def summarize(args):
    root = args.evidence
    decode = {}
    for case in ["final-1440", "final-long", "hevc-sdr10-2997.mp4", "av1-final", "invalid-adapter",
                 "unsupported-chroma", "amd-adapter"]:
        trial = read(root / f"decode/{case}/decode-comparison.json")
        rows = trial["measurements"]
        decode[case] = dict(softwareMilliseconds=distribution([row["softwareMs"] for row in rows]),
                            requestedD3d11Milliseconds=distribution([row["hardwareMs"] for row in rows]),
                            actualBackend=rows[0]["primaryDecode"]["backend"],
                            observedPlanes=rows[0]["primaryDecode"]["planeFormat"],
                            fallback=rows[0]["primaryDecode"].get("fallback"),
                            downloadMilliseconds=distribution([row["primaryDecode"]["downloadMilliseconds"] for row in rows]),
                            normalizeMilliseconds=distribution([row["primaryDecode"]["normalizeMilliseconds"] for row in rows]),
                            selectedPictures=len(rows), maximumRgbDelta=max(row["maximumRgbDelta"] for row in rows))
        if case in {"final-1440", "final-long", "av1-final", "hevc-sdr10-2997.mp4"}:
            decode[case]["independentFfprobeSeeks"] = check_seek(args.ffprobe, trial)

    audio_rows = {run: read(root / f"audio/{run}/audio-seeks.json")["measurements"]
                  for run in ["baseline", "cold", "reopened"]}
    assert [row["pcmSha256"] for row in audio_rows["cold"]] == [row["pcmSha256"] for row in audio_rows["reopened"]]
    audio = {case: dict(firstPassMilliseconds=distribution([r["elapsedMs"] for r in rows if r["pass"] == 0]),
                        warmMilliseconds=distribution([r["elapsedMs"] for r in rows if r["pass"] == 1]),
                        firstSeek5952Ms=next(r["elapsedMs"] for r in rows if r["pass"] == 0 and r["second"] == 5952))
             for case, rows in audio_rows.items()}
    audio["pcmHashesMatchAcrossProcesses"] = True
    index_cases = {name: read(root / f"audio/{name}/audio-index.json")
                   for name in ["index-new", "index-reopened", "index-after-corruption"]}
    survived = [row["index"]["reusedPersistentAnchors"] for row in index_cases["index-after-corruption"]]
    assert sorted(survived) == [False, True]
    audio["indexChecksumRecovery"] = index_cases

    proxy = read(root / "bench/proxy-1440/proxy-comparison.json")
    proxy = dict(buildMs=proxy["buildMs"], reuseMs=proxy["reuseMs"], profile=proxy["proxy"]["codec"],
                 bytes=int(proxy["proxy"]["bytes"]),
                 originalSeekMilliseconds=distribution([row["originalMs"] for row in proxy["measurements"]]),
                 proxySeekMilliseconds=distribution([row["proxyMs"] for row in proxy["measurements"]]))

    exports = []
    ffmpeg = args.ffprobe.with_name("ffmpeg.exe")
    for measurement in read(root / "bench/export/export-comparison.json")["measurements"]:
        result = measurement["result"]
        metadata = ffprobe(args.ffprobe, "-count_frames", "-show_entries",
                           "format=duration:stream=codec_name,codec_type,width,height,avg_frame_rate,nb_read_frames", result["path"])
        video, audio_stream = metadata["streams"]
        assert (video["codec_name"], video["width"], video["height"], video["avg_frame_rate"], video["nb_read_frames"]) == (
            "h264", 2560, 1440, "60/1", "480"), metadata
        assert audio_stream["codec_name"] == "aac" and abs(float(metadata["format"]["duration"]) - 8) < .02, metadata
        pcm = subprocess.run([str(ffmpeg), "-v", "error", "-i", result["path"], "-map", "0:a:0", "-f", "f32le", "-"],
                             capture_output=True, check=True, timeout=60).stdout
        assert len(pcm) >= 8 * 48000 * 2 * 4 and any(pcm)
        exports.append(dict(encoder=result["encoder"], run=measurement["pass"], seconds=result["elapsedSeconds"],
                            frames=int(result["frames"]), bytes=int(result["bytes"]), ffprobe=metadata,
                            decodedPcmSha256=hashlib.sha256(pcm).hexdigest()))
    assert len({result["decodedPcmSha256"] for result in exports}) == 1

    native = {}
    for directory in sorted((root / "native").iterdir()):
        path = directory / "result.json"
        if not path.exists():
            continue
        trial = read(path)
        totals = {key: value for key, value in trial.items() if key not in
                  {"cadenceMilliseconds", "requestToDisplayedFrameMs", "backwardDragMs", "source"}}
        if "cadenceMilliseconds" in trial:
            totals["cadenceMilliseconds"] = distribution(trial["cadenceMilliseconds"])
        if "requestToDisplayedFrameMs" in trial:
            totals["requestToDisplayedFrameMs"] = distribution(trial["requestToDisplayedFrameMs"])
            totals["backwardDragMs"] = distribution(trial["backwardDragMs"])
        resources = path.with_name("resources.json")
        if resources.exists():
            totals["resources"] = read(resources)
            samples = totals["resources"].pop("workingSetSamples", [])
            if samples:
                midpoint = len(samples) // 2
                totals["steadyWorkingSetBytes"] = distribution([r["workingSetBytes"] for r in samples[midpoint:]])
        native[directory.name] = totals

    baseline = read(root / "baseline/1080-native/result.json")
    baseline = dict(seconds=baseline["playbackSeconds"], selectedFps=baseline["previewFps"],
                    maximumSkewSeconds=baseline["presentationSkewSeconds"], underrunFrames=baseline["playbackUnderrunFrames"],
                    drawLoopMilliseconds=distribution(baseline["playbackFrameMilliseconds"]))
    trace = read(root / "native/1440-d3d11va-trace.json")
    staged = [frame for frame in trace["frames"] if (frame.get("mediaFrame") or {}).get("PrimaryDecode")
              and (frame.get("upload") or {}).get("GpuInterval")]
    transfer = dict(traceFrames=trace["observedFrames"], omitted=trace["droppedTraceFrames"],
                    downloadedPlaneBytes=staged[0]["mediaFrame"]["PrimaryDecode"]["VisiblePlaneBytes"],
                    hostDownloadMilliseconds=distribution([frame["mediaFrame"]["PrimaryDecode"]["DownloadMilliseconds"]
                                                           for frame in staged]),
                    uploadHostMilliseconds=distribution([frame["cpu"]["textureUploadMs"] for frame in staged]),
                    uploadGpuMilliseconds=distribution([frame["upload"]["GpuInterval"]["ElapsedMilliseconds"]
                                                        for frame in staged]))
    report = dict(milestone="M3d", version="0.3.0-rc.3", validationDate="2026-11-27",
                  scope="Single Windows 11 workstation, RTX 3070 driver 610.88, Ryzen 7 7700X, FFmpeg 7.1.4 staging. See MEDIA_QUALIFICATION.md.",
                  baselineNative1080=baseline, exactAudio=audio, decode=decode, proxy=proxy, nativeTransfer1440=transfer,
                  native=native, matchedExports=exports, creator=read(root / "creator-proof.json"))
    if args.package:
        report["release"] = read(args.package / "package-evidence.json")
    if args.lifecycle:
        report["lifecycle"] = read(args.lifecycle / "lifecycle.json")
    if (root / "analyzed-creator-final.json").exists():
        report["analyzedCreator"] = read(root / "analyzed-creator-final.json")
    if (root / "mp4-transcript-diagnosis/result.json").exists():
        report["pureToneTranscriptControl"] = read(root / "mp4-transcript-diagnosis/result.json")
    if args.package and (root / "native/packaged-long-cancel-proxy-proxy-data").exists():
        proxy_output = root / "native/packaged-long-cancel-proxy-proxy-data"
        report["cancelledProxyFiles"] = sorted(path.name for path in proxy_output.iterdir())
    if args.tests:
        log = args.tests.read_text(encoding="utf-8-sig")
        report["releaseTests"] = []
        for part in log.split("Test run for ")[1:]:
            project = re.search(r"Valencia\.\w+(?:\.Tests)?\.dll", part.splitlines()[0])
            result = re.search(r"Passed:\s*(\d+)", part)
            if project and result:
                report["releaseTests"].append(dict(project=project.group(0), passed=int(result.group(1))))
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for option in ["evidence", "ffprobe", "output", "package", "lifecycle", "tests"]:
        parser.add_argument("--" + option, type=pathlib.Path, required=option in {"evidence", "ffprobe", "output"})
    summarize(parser.parse_args())
