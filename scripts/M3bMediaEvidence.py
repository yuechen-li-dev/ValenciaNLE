"""Measure the encoded mixed-tone fixture's timeline gap independently."""
import argparse
import array
import json
import math
import pathlib
import re
import subprocess

from M3bReleaseProof import dump


def measure(ffmpeg, path):
    audio = subprocess.run([str(ffmpeg), "-v", "error", "-i", str(path), "-map", "0:a:0",
                            "-ac", "1", "-ar", "48000", "-f", "s16le", "-"],
                           capture_output=True, check=True, timeout=60).stdout
    samples = array.array("h", audio)
    assert abs(len(samples) / 48000 - 6.006) < .05, (path, len(samples))
    chunk = 240
    rms = [math.sqrt(sum(x*x for x in samples[index:index+chunk]) / chunk) / 32768
           for index in range(0, len(samples)-chunk, chunk)]
    threshold = max(rms[150:190]) * .08
    start = next(index for index in range(160, 240) if rms[index] < threshold)
    end = next(index for index in range(270, 340) if rms[index] >= threshold)
    assert max(rms[230:275]) < threshold and min(rms[150:180]) > threshold
    video = subprocess.run([str(ffmpeg), "-hide_banner", "-nostats", "-i", str(path), "-an",
                            "-vf", "blackdetect=d=0.4:pix_th=0.1", "-f", "null", "-"],
                           capture_output=True, check=True, timeout=60).stderr.decode(errors="replace")
    timestamps = re.search(r"black_start:([\d.]+) black_end:([\d.]+)", video)
    assert timestamps, path
    picture_gap = [float(timestamps[1]), float(timestamps[2])]
    sound_gap = [start * chunk / 48000, end * chunk / 48000]
    assert max(abs(sound - picture) for sound, picture in zip(sound_gap, picture_gap)) < .05
    segment = samples[4800:14400]

    def tone(frequency):
        step = math.tau * frequency / 48000
        real = sum(value * math.cos(index*step) for index, value in enumerate(segment))
        imaginary = sum(value * math.sin(index*step) for index, value in enumerate(segment))
        return 2 * math.hypot(real, imaginary) / (len(segment) * 32768)

    tones = {hz: tone(hz) for hz in [330, 440, 690]}
    assert min(tones[330], tones[440]) > max(.005, 8 * tones[690]), (path, tones)
    return dict(output=str(path), audioSamples=len(samples), pictureGapSeconds=picture_gap,
                soundGapSeconds=sound_gap, toneAmplitudes=tones)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--ffmpeg", type=pathlib.Path, required=True)
    parser.add_argument("--report", type=pathlib.Path, required=True)
    parser.add_argument("workflow", type=pathlib.Path)
    args = parser.parse_args()
    workflow = json.loads(args.workflow.read_text(encoding="utf-8"))
    dump(args.report, {item["encoder"]: measure(args.ffmpeg.resolve(), pathlib.Path(item["job"]["output"]))
                       for item in workflow["renders"]})
