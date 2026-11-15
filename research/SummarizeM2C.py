"""Standard-library report generation only; not part of Valencia or inference execution."""
import collections
import datetime
import hashlib
import json
from pathlib import Path
import statistics
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "docs/validation"
RAW = ROOT / "artifacts/local/m2c"
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def write(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def suite(name):
    path = ROOT / "artifacts/local/tests" / name
    xml = ET.parse(path)
    counters = xml.find(".//t:Counters", NS).attrib
    return {"trx": str(path.relative_to(ROOT)), "sha256": digest(path), **counters,
            "cases": [{"name": test.attrib["testName"], "outcome": test.attrib["outcome"], "duration": test.attrib.get("duration")}
                      for test in xml.findall(".//t:UnitTestResult", NS)]}


def run():
    OUT.mkdir(exist_ok=True)
    metrics = ["wallMs", "loadMs", "firstPassMs", "gpuComputeMs", "gpuTransferMs", "overlapMs", "schedulerDecisionMs",
               "ringCpuRecordingMs", "ringCpuSubmissionMs", "ringCpuCompletionWaitMs", "ringWeightPreparationMs",
               "outerStageOverheadUpperBoundMs", "residentSelectedWeightBytes", "streamingSlotBytes",
               "hostToDeviceWeightBytes", "additionalInputUploadBytes", "deviceToHostBytes", "dispatches",
               "submissions", "completionWaits", "allocatedManagedBytes", "maximumError", "rmsError"]
    configurations = []
    traces = []
    for file in ("gpu-tiny.json", "gpu-large.json", "gpu-single-row.json"):
        data = read(RAW / file)
        groups = collections.defaultdict(list)
        for row in data["results"]:
            groups[(row["part"], row["mode"], row["rows"], row["tileRows"])].append(row)
        summaries = []
        for (part, mode, rows, tile_rows), trials in groups.items():
            summary = {"part": part, "mode": mode, "rows": rows, "tileRows": tile_rows, "trials": len(trials),
                       "allCorrect": all(t["correct"] for t in trials),
                       "median": {metric: statistics.median(t[metric] for t in trials) for metric in metrics},
                       "wallMinMs": min(t["wallMs"] for t in trials), "wallMaxMs": max(t["wallMs"] for t in trials),
                       "wallP95Ms": max(t["wallMs"] for t in trials),
                       "allocatorHighWaterBytes": max(t["memory"]["highWaterLiveBytes"] for t in trials)}
            summaries.append(summary)
            if rows == 32 and tile_rows == 256 and mode == "R1-stream-dominatus":
                traces.append({"model": data["model"]["id"], "part": part, "scope": data["scope"],
                               "trial": 0, "events": trials[0]["traces"]})
        configurations.append({**{key: value for key, value in data.items() if key != "results"},
                               "raw": str((RAW / file).relative_to(ROOT)), "rawSha256": digest(RAW / file), "summaries": summaries})
    shaders = ROOT.parent / "Copeland/src/Aurelian/Aurelian.Shaders/Assets/ComputeMath"
    source_paths = list((ROOT / "research/Valencia.WhisperGpu").glob("*.cs"))
    source_paths += list(shaders.glob("*.v.ts"))
    source_paths += [ROOT.parent / "Copeland/src/Aurelian/Aurelian.Graphics/Vulkan/ComputeMath" / file for file in
                     ["IComputeStreamController.cs", "VulkanComputeMath.Streaming.cs", "VulkanComputeMath.SubmissionRing.cs"]]
    evidence = {"milestone": "M2c GPU research", "outcome": "qualified representative subgraphs; full Aurelian Whisper incomplete",
                "recordedAt": datetime.datetime.now().astimezone().isoformat(),
                "machine": read(RAW / "machine.json"),
                "copelandHead": subprocess.check_output(["git", "-C", str(ROOT.parent / "Copeland"), "rev-parse", "HEAD"], text=True).strip(),
                "warmupPassesPerConfiguration": 1, "measuredTrials": 5,
                "sourceHashes": [{"path": str(path.relative_to(ROOT) if path.is_relative_to(ROOT) else path), "sha256": digest(path)} for path in source_paths],
                "referenceTiny": read(RAW / "result.json")["cold"],
                "referenceLarge": read(RAW / "reference-large.json"),
                "comparisonWarning": "CPU reference performs full transcription; Aurelian performs block subgraphs. No end-to-end speed ratio is valid.",
                "unavailable": ["Full resident/streaming Whisper inference", "Decoder cross attention, KV cache, autoregressive tokens/logits",
                                "Full-model VRAM-pressure baseline", "Actual dedicated-queue asynchronous prefetch", "Full-model token latency and Aurelian WER"],
                "configurations": configurations}
    write(OUT / "m2c-gpu.json", evidence)
    write(OUT / "m2c-gpu-traces.json", traces)
    lines = ["# Measured Whisper block subgraphs", "", "Five warm trials after one warmup. All times in milliseconds; memory in MiB. These are not full-model transcription measurements.", ""]
    for config in configurations:
        lines += [f"## {config['model']['id']}, activation rows {config['summaries'][0]['rows']}", "",
                  "| Part | Tile rows | Mode | Weight budget/residency MiB | Wall median [min,max] | GPU compute | GPU weight copy | Policy CPU | Ring record CPU | Ring submit CPU | Ring wait CPU | Overlap | Dispatches |", "|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]
        for summary in config["summaries"]:
            m = summary["median"]
            weight_bytes = m["residentSelectedWeightBytes"] if summary["mode"].startswith("R0") else m["streamingSlotBytes"]
            lines.append(f"| {summary['part']} | {summary['tileRows']} | {summary['mode']} | {weight_bytes/1048576:.3f} | {m['wallMs']:.2f} [{summary['wallMinMs']:.2f},{summary['wallMaxMs']:.2f}] | {m['gpuComputeMs']:.2f} | {m['gpuTransferMs']:.2f} | {m['schedulerDecisionMs']:.3f} | {m['ringCpuRecordingMs']:.2f} | {m['ringCpuSubmissionMs']:.2f} | {m['ringCpuCompletionWaitMs']:.2f} | {m['overlapMs']:.3f} | {m['dispatches']} |")
        lines += [""]
    (OUT / "m2c-gpu-tables.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    suites = [suite(name + ".trx") for name in ["Valencia.Core.Tests", "Valencia.Persistence.Tests", "Valencia.Tests", "Valencia.Programming.Tests"]]
    transcript_xml = ET.parse(ROOT / "artifacts/local/tests/m2c-transcription.trx")
    observations = {}
    for stdout in transcript_xml.findall(".//t:StdOut", NS):
        for line in (stdout.text or "").splitlines():
            if line.startswith("M2C_"):
                key, value = line.split("=", 1)
                observations[key] = json.loads(value)
    write(OUT / "m2c.json", {"milestone": "M2c", "productOutcome": "working local reference transcription and transcript-driven editing; approximate model word timing",
          "gpuOutcome": evidence["outcome"], "recordedAt": evidence["recordedAt"], "machine": evidence["machine"],
          "tests": suites, "totalValenciaTests": sum(int(s["passed"]) for s in suites),
          "copeland": [suite("m2c-copeland.trx"), suite("m2c-aurelian-inference.trx"), suite("m2c-aurelian-graphics.trx")],
          "observations": observations, "headless": read(RAW / "result.json"),
          "native": read(ROOT / "artifacts/local/m2c-native/result.json"),
          "regressions": {"playerMp4": read(ROOT / "artifacts/local/smoke-mp4/smoke.json"), "playerMkv": read(ROOT / "artifacts/local/smoke-mkv/smoke.json"),
                          "editor": read(ROOT / "artifacts/local/m1b-smoke/result.json"), "programming": read(ROOT / "artifacts/local/m2a-demo/result.json"),
                          "analysis": read(ROOT / "artifacts/local/m2b-demo/result.json"), "waveformUi": read(ROOT / "artifacts/local/m2b-native/result.json")},
          "binaries": [{"path": path, "sha256": digest(ROOT / path)} for path in ["artifacts/publish/win-x64/Valencia.App.dll", "artifacts/publish/cli-win-x64/Valencia.Cli.dll", "artifacts/publish/win-x64/Valencia.Analysis.dll"]],
          "sourceHashes": [{"path": str(path.relative_to(ROOT)), "sha256": digest(path)} for path in (ROOT / "src").rglob("*.cs")
                           if "obj" not in path.parts and "bin" not in path.parts],
          "build": {"command": "scripts/Build.ps1", "log": "artifacts/local/m2c-build.log", "logSha256": digest(ROOT / "artifacts/local/m2c-build.log"),
                    "finalDesktopPublishLog": "artifacts/local/m2c-final-publish.log", "finalDesktopPublishLogSha256": digest(ROOT / "artifacts/local/m2c-final-publish.log"),
                    "valenciaHasGitMetadata": (ROOT / ".git").exists(), "copelandHead": evidence["copelandHead"]},
          "limitations": ["Experimental native word timing, not forced alignment; unsupported word times cannot anchor edits.",
                          "English generated speech qualification is not general transcription accuracy.",
                          "Two-hour fixture is sparse speech, not two hours of continuous model inference.",
                          "Full Aurelian Whisper and whole-model VRAM research remain incomplete."]})
    print(str(OUT / "m2c.json"))


if __name__ == "__main__":
    run()
