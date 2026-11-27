# M3d — Media performance and hardware decode

Valencia 0.3.0-rc.3 adds an opt-in Windows D3D11VA decoder, persistent audio seek anchors, a bundled VP9 proxy profile, and native performance diagnostics. Playback, export, proposals and the programmable tools share the original rational timeline evaluator.

| Workload | Observed result on RTX 3070 |
|---|---|
| Two-hour OBS-derived project, seek to 5,952 s | 10.02 s in the previous build, 9.72 s while building anchors in rc.3, and 180 ms after restarting rc.3. Selected PCM blocks matched across rc.3 processes. |
| 1440p60, native playback with a concurrent NVENC render | 59.98 distinct pictures/s over 25 s, 49 ms maximum A/V skew and zero audio underrun frames. WDDM reported active video decoding. |
| Three-hour 1080p60 fixture with concurrent audio analysis | 59.03 distinct pictures/s over 60 s starting at 1:55:10, 125 ms maximum A/V skew and zero underrun frames. |
| 1440p60 native scrub with a cached proxy | 53.3 ms request-to-present p95, 30.1 ms maximum A/V skew and zero underruns. Original-only reverse playback exceeded the 150 ms skew limit. |
| GPU clip effects | 48 dispatches used 24 target uploads and 24 readbacks. Maximum CPU/Vulkan channel delta was one, with zero Vulkan validation errors. |
| Export | Identity-size sequences avoid one managed RGBA allocation and copy per output picture. NVENC consumes CPU-fed frames. |

Software decoding remains the default. On the reference GPU, D3D11VA with CPU download took longer for long-GOP random seeks. Set `VALENCIA_VIDEO_DECODER=d3d11va` before launch to select it for forward playback and export. Paused and reverse playback retain the software picture history. Set `VALENCIA_PREVIEW_PROXY=automatic|always|never` for preview proxy policy.

The final package analyzed the full three-hour 1080p60 source and ran the registered M3c `creator` workflow with two reviewed source ranges. It produced 36 timed words, an 18-second landscape and a five-second vertical Short. A separate layered edit exercised a second source, gaps, an overlay, effects and a lower third. Both proofs reviewed, committed, saved, reopened and independently decoded the outputs.

The unsigned installer and portable ZIP passed on the reference workstation. An rc.2 upgrade and uninstall preserved test user data. All 432 Release tests passed. Wider production qualification still needs original-only long-GOP reverse playback, a completed full-length proxy transcode, an undirected long-form creator trial and independent Windows hardware.

[Architecture](MEDIA_PERFORMANCE_ARCHITECTURE.md) · [Qualification and reproduction](MEDIA_QUALIFICATION.md) · [Windows distribution](DISTRIBUTION.md)
