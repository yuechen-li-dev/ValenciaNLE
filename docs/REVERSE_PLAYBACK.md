# Reverse playback and scrubbing

In the editor, **J** plays backward at 1× with audio, **K** pauses, and **L**
plays forward. Reverse stops at the beginning of the active monitor. The
timeline keeps its own position while the source monitor plays.

The playhead tracks the latest snapped pointer position during a drag. Video
preview uses one decode in flight and one latest target. Releasing the pointer
seeks the final position. The playhead overlay renders through Aurelian on each
frame using the prepared timeline geometry. Its pointed handle, progress rule,
hover guide and drag color are separate from the periodic layout refresh.

FFmpeg decodes inter-frame codecs forward from a usable keyframe. The paused
and reverse preview paths retain up to 64 native pictures or 64 MiB of
referenced picture buffers per active video cursor. A backward request within
that history selects the picture by source PTS and converts only the selected
picture. A miss seeks the existing decoder and refills the history.

Reverse transport reads bounded intervals through the normal timeline mixer
and reverses stereo sample pairs within each block. The number of samples
played on the audio device drives the descending sequence clock. Video uses
the same timeline evaluator as forward playback and export. At an exact cut,
the first reverse picture comes from the interval immediately before the cut.

Native smokes ran on a Ryzen 7700X and RTX 3070 with NVIDIA driver 610.88 and
.NET 11. The four-second reverse run on the 1080p60 H.264/AAC MKV (120-frame
GOPs) displayed 229 distinct pictures, with 46.5 ms maximum measured A/V skew.
Both fixtures passed reverse pause, resume, and the switch back to forward.
Both reported zero reverse audio underruns and zero Vulkan validation errors.

| H.264/AAC fixture | Broad seeks p95 before | Broad seeks p95 after | Nearby backward drag median |
|---|---:|---:|---:|
| 640×360 at 30 fps MP4 | 64.2 ms | 54.4 ms | 22.0 ms |
| 1920×1080 at 60 fps MKV | 94.4 ms | 93.5 ms | 31.7 ms |

Each broad-seek run used 24 ruler drags. The nearby-drag result measures 12
single-frame steps while holding the same drag. These are editor-loop timings
through preview upload, excluding physical display latency. The smokes drive
the native editor's coordinate and pointer-capture handlers with scripted input.

The Release solution ran 362 tests with zero failures or skips. The FFmpeg
fixture tests compare retained pictures with fresh source selection
and compare backward stereo PCM against the forward mix across cuts and gaps.
The history test verified that 14 nearby backward requests required no extra
picture decodes after the initial request. Long 4K recordings remain outside
this hardware qualification.

The native editor smoke also passed. Independent FFmpeg decoding of its
9.667-second H.264/AAC export verified source order, the cut, black frames and
silence in the gap, and audible signal outside it.

```powershell
dotnet test Valencia.slnx -c Release -m:1 -nr:false -p:UseSharedCompilation=false
dotnet publish src/Valencia.App -c Release -r win-x64 --self-contained false -o artifacts/local/reverse-host -m:1 -nr:false -p:UseSharedCompilation=false
./artifacts/local/reverse-host/Valencia.App.exe --scrub-smoke artifacts/local/fixtures/av.mp4 --output artifacts/local/reverse-scrub-final
./artifacts/local/reverse-host/Valencia.App.exe --editor-smoke artifacts/local/fixtures/av.mp4 --output artifacts/local/reverse-editor
./scripts/VerifyEditorExport.ps1 -Directory artifacts/local/reverse-editor -SourcePath artifacts/local/fixtures/av.mp4
```
