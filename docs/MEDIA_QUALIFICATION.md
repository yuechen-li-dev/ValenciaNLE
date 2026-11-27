# Media qualification

## Test environment and fixtures

Windows 11 Pro build 26300.10075 on a Ryzen 7 7700X, NVIDIA GeForce RTX 3070 with driver 610.88 and AMD Radeon integrated graphics. Valencia used .NET SDK 10.0.404, runtime 10.0.15 and pinned FFmpeg 7.1.4, with a separately supplied FFmpeg 7.1.1 for compatibility encoding. Hardware trials selected NVIDIA vendor ID `0x10de`. These are single-workstation results.

The 1080p60 and 1440p60 OBS recordings from M2e provide actual short OBS sessions. The two-hour OBS-derived fixture repeats its motion video and has three independently indexed stereo AAC tracks. The three-hour controlled fixture repeats a 1080p60 H.264 seed with continuous AAC tracks from the existing three-hour creator fixture. Generated 1440p60 motion, 10-bit 30000/1001 HEVC, and offset full-range VFR AV1 complement the corpus. These fixtures measure media mechanics. Editorial quality and independent production hardware require separate evidence.

## Measurements

The rc.2 desktop passed ten seconds of actual 1080p60 OBS footage at 59.46 selected pictures/s, 65.4 ms maximum A/V skew, and zero underrun frames. The newer timed probe counts distinct pictures after native present. It ran the generated 1440p60 project for 25 seconds while an NVENC render ran. Software playback presented 59.64 pictures/s with 47.1 ms maximum skew. D3D11VA presented 59.98 pictures/s with 49.2 ms skew. Both had zero underruns. WDDM captured positive video decode utilization in 49 D3D11VA samples and zero software samples. The child export took 2.51 s during software playback and 3.56 s during D3D11VA playback. These wall measurements include contention and do not establish encoder-engine isolation.

The three-hour fixture is 20,282,470,928 bytes. The native trial decoded 3,542 distinct pictures in 60.00 seconds from 1:55:10, while an uncached audio analysis job completed in 10.01 seconds. There were zero underrun frames and 125 ms maximum skew. Peak process working set was 882 MB, with 255 MB dedicated and 91 MB shared GPU memory sampled by WDDM. This run bounds the observed window only. Its 15.1-second cold time to distant playback included project open and progressive audio prebuffering. Stream-copy looping AAC in the first synthetic fixture produced repeat-boundary discontinuities that the exact mixer rejected. The qualified fixture uses continuous originals for those audio tracks.

The final portable package replayed the same distant range while an uncached full-source Whisper job ran. It presented 2,398 D3D11VA pictures over 40 seconds, with 63.9 ms maximum skew and zero underruns. The Whisper job completed in 23.0 seconds. A second package run cancelled a full-length proxy job after the eight-second playback interval. Joining the worker left no proxy video, manifest or temporary media file. A generated pure-tone MP4 instead reached Whisper's end-of-text limit, which the analysis job reported as a failure. Native playback of that MP4 still passed. The final package also played the actual 1440p60 OBS recording for ten seconds with 34.8 ms maximum skew and zero underruns.

The 41 paired seek requests per source matched PTS and RGB for 1080p60 H.264, 1440p60 H.264, 10-bit HEVC and full-range offset VFR AV1 on the tested driver. Independent FFprobe frame scans checked four requests per source. H.264 4:4:4 negotiated software, and an invalid adapter reported device creation failure with software fallback. The AMD integrated adapter on the same workstation also returned D3D11 NV12 surfaces with matching pixels and PTS for 41 H.264 requests. In the long H.264 comparison, software random-seek p95 was 205 ms and requested D3D11VA p95 was 328 ms. Host-observed download p95 was 31 ms. The 1440p60 headless seek trial had additional workstation activity. Its raw timings establish a staging cost, while the native trial gives the better realtime comparison.

The RTX 3070 returned D3D11 AV1 surfaces. The separate AV1 NVENC encoder probe returned “No capable devices found.” The bundled software AV1 encoder passed the distribution checks.

The 36-second 1440p60 source proxy encoded and validated in 7.60 s, occupied 4,167,044 bytes and reopened in 7.6 ms. Its 40 headless seeks returned the same timestamps as the originals. Before the final scaling optimization, paired original and proxy p95 were 443.1 and 33.8 ms. After the optimization, the native 24-trial proxy scrub measured 53.3 ms p95, including a distinct native draw. The original-only case measured 160.7 ms p95 and exceeded the 150 ms A/V contract during reverse. Proxy-assisted reverse passed at 30.1 ms maximum skew. This particular original GOP required repeated decode-forward work as each reverse window exhausted the 64 MiB picture history.

The two-layer Vulkan sample compared 24 sequence pictures. It reduced 48 potential target uploads and readbacks to 24 of each, with one maximum channel level of CPU/Vulkan difference and zero validation errors. The two samples had Vulkan medians of 9.6 and 12.8 ms versus 53.0 and 40.7 ms on CPU. Each still used two decoder inputs and two compute submissions. The native effect smoke also presented and captured the samples.

Matched 8-second 1440p60 H.264 exports used the same 12 Mb/s target, 120-picture GOP, software decode and AAC output. NVENC took 2.86–3.01 s and x264 took 4.04–4.24 s in two interleaved runs. FFprobe independently counted 480 video frames in each file, and decoded audio hashes matched. Different rate-control algorithms produced different lossy video output.

The long-source editorial proof selected a range starting at source timeline 2:00:01. It used two source assets, two source audio tracks, an overlay, effects, a lower third, a controlled source-time caption, an explicit gap and a second source after the gap. The packaged MCP host reviewed and applied the lower-third and make-short workflows, saved and reopened the project, then queued 8.5-second landscape and 4-second 1080×1920 outputs. Independent decoding checked active pictures, audio, dimensions, durations and a black gap. The automatic transcript-to-highlights workflow and a complete proxy transcode of the three-hour fixture were outside this executed proof.

A separate run on the final package analyzed the complete three-hour source. Audio analysis took 9.1 s and local Whisper transcription took 21.0 s. The registered `creator` workflow used two explicit source ranges to produce 36 caption words, an 18-second landscape and a five-second vertical Short. The run generated a paired review, committed the candidate, saved, reopened and queued both NVENC exports in 66 s total. FFmpeg and FFprobe independently decoded audio and video and verified 1920×1080 and 1080×1920 at 60 fps. The source project hash remained unchanged. This is a controlled creator trial. It leaves long-form ranking without reviewed ranges and footage selected by an independent editor unqualified.

The final Windows package passed portable and installed host checks, rc.2 upgrade, app registration and shortcut checks, compatibility encoding and uninstall with data preservation. The complete suite passed 432 Release tests. The packaging script emitted matching source archives and SHA-256 checksums. [Machine-readable evidence](validation/m3d.json)

## Reproduction

Use the pinned vendor build. Set `VALENCIA_FFMPEG_DIR` to its `ffmpeg-install/bin` with the accompanying libaom, libvpx, opus and winpthreads DLLs on PATH. Set `VALENCIA_FFMPEG_EXE` and `VALENCIA_FFPROBE_EXE` to the matching executables, `VALENCIA_D3D11_VENDOR_ID=0x10de` for the reference NVIDIA adapter, and a new isolated `VALENCIA_DATA_DIR`. Set `VALENCIA_COMPAT_FFMPEG_EXE` to a separately obtained build for H.264 export measurements.

`scripts/Prepare-M3dCorpus.ps1` recreates the technical corpus from the M2d speech fixture using an external Windows FFmpeg with NVENC and libaom. `scripts/M3dCreatorProof.py` reproduces the edited range against a portable bundle. `scripts/M3dAnalyzedCreatorProof.py` uses the local tiny.en model to reproduce the full-source analysis, registered creator plan and both exports. `scripts/M3dEvidence.py` aggregates the raw trials and independently invokes FFmpeg and FFprobe. The raw machine directory is `artifacts/local/m3d` and the summarized record is `docs/validation/m3d.json`.

```powershell
$sample = 'samples/Valencia.Qualification/bin/Release/net10.0/Valencia.Qualification.exe'
$source = 'artifacts/local/m3d/corpus/motion-1440p60.mp4'
& $sample $source artifacts/local/m3d/reproduce/decode --decode-comparison
& $sample $source artifacts/local/m3d/reproduce/proxy --proxy-comparison
& $sample $source artifacts/local/m3d/reproduce/export --export-comparison
& $sample $source artifacts/local/m3d/reproduce/project --project-only
./scripts/NativeQualification.ps1 -App artifacts/local/m3d/rc3-final/portable/Valencia.App.exe -Project artifacts/local/m3d/reproduce/project/source.valencia -Directory artifacts/local/m3d/reproduce/native -Mode performance -ModeArguments @('--performance-seconds','25','--performance-start','2')
```

Run `--audio-seeks` twice in distinct processes against `artifacts/local/m2e/long-1080-repeated.mkv` using the same isolated data directory. Separate cold and reopened distributions. The sample records a hash of each returned PCM block. `--audio-index` exposes persisted-anchor counts.

The cache validation used two 12-second AAC streams. Both reported six new anchors in the cold process and six reused anchors after reopening. Flipping one checksum byte caused just that stream's cache to rebuild.

`--scrub-smoke` drives native coordinate and capture handlers and verifies the requested picture after a native present. `VALENCIA_RENDER_TRACE` records the selected decoder, source timestamps, texture-upload timing, Vulkan timestamps and CPU phases when available. WDDM samples in `NativeQualification.ps1` include active video decode observations. They exclude child encoder CPU and RAM.
