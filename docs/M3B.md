# M3b — Product hardening, distribution and export

Valencia 0.3.0-rc.1 packages the native editor, CLI, stdio MCP host, .NET runtime, fonts, shaders and FFmpeg 7.1.4 for Windows x64. The local candidate includes an unsigned per-user installer, a portable ZIP, corresponding source archives, a file inventory and SHA-256 checksums. It uses approximately 225 MB installed. The portable ZIP is about 87 MB and the installer is about 77 MB. See the validation record for exact sizes. [Distribution and build](DISTRIBUTION.md) · [Component inventory](THIRD_PARTY_COMPONENTS.md)

## Protecting the project

The editor captures committed snapshots after two seconds of quiet or at the configurable interval during sustained edits. The default interval is 30 seconds. Deliverance publishes these snapshots in the background and retains five complete generations per run. Startup recovery offers the captured edit, the last explicit save, retention for later or discard. Saving a recovered edit is an explicit Save As. Named checkpoints have their own bounded retention and restore through the editing session.

A desktop editor holds a project writer lease while a file is open for editing. Guarded writes also check the saved bytes that the writer opened. The headless agent's save operation requires a new destination. Undo, redo, plans and checkpoint restoration use the same revision checks as ordinary edits. [Frontend handoff](CLAUDE_FRONTEND_HANDOFF.md)

## Export and finishing

The durable FIFO queue stores the captured project revision, render settings and source fingerprints. It records progress and encoded-output findings. Interrupted attempts keep their pinned configuration and can restart from the captured revision. Finished output is published atomically to a new path.

The earlier loudness shortfall reproduced at −18.48 LUFS and −0.70 dBTP in the legacy H.264/AAC path. A static gain increase would exceed the peak ceiling. M3b applies FFmpeg loudnorm dynamics after the existing mix, with codec-specific margins. It fixes the duration at the rendered frame window after the dynamics filter flushes. The ten-minute H.264/AAC render of the same revision measured −16.67 LUFS and −1.66 dBTP on independent decode. The VP9/Opus render measured −16.7 LUFS and −1.4 dBTP. [Export profiles and quality checks](EXPORT_PROFILES.md)

| Edited output | Video / audio | Execution | Qualification |
|---|---|---|---|
| Fractional landscape and landscape 1080p60 | VP9 / Opus WebM | Bundled libvpx software | Independently decoded |
| Fractional landscape, 1440p60 and three 20-second vertical Shorts | AV1 / Opus WebM | Bundled libaom software | Independently decoded |
| Fractional and 1080p intermediate | FFV1 / FLAC Matroska | Bundled software | Independently decoded |
| Fractional landscape | AV1 / Opus WebM | Optional SVT-AV1 software | Independently decoded |
| Fractional landscape and creator compatibility output | H.264 / AAC MP4 | Optional libx264 software | Independently decoded |
| Fractional landscape | HEVC / AAC MP4 | Optional libx265 software | Independently decoded |
| Fractional landscape | H.264 / AAC MP4 | Optional Media Foundation | Independently decoded |
| Fractional landscape | H.264 and HEVC / AAC MP4 | Optional NVIDIA NVENC | Independently decoded |

The 1080×1920 VP9 and 360p review presets also passed actual encodes. Independent checks verified stream metadata, decoded picture and audio, both synthetic input tones, and the sound/picture gap boundaries. The nine tested encoder configurations kept both gap boundaries within 9 ms on the fractional test timeline. Media Foundation HEVC and NVIDIA, AMD and Intel AV1 hardware encoding were unavailable on the test workstation. [Machine-readable qualification](validation/m3b.json)

## Release qualification

- Installed and extracted the release on Windows 11 Pro build 26300. Ran both outside the repository with a system-only PATH and verified they loaded the bundled .NET runtime. The workstation had a developer SDK, NVIDIA and AMD drivers and optional external FFmpeg installed. The standard run used only the bundled media stack.
- Verified the Start menu shortcut, an upgrade from a locally generated rc.0 installer, and uninstall. Upgrade and uninstall preserved the test settings. The native Vulkan layout smoke reported zero validation errors on an RTX 3070.
- Forced termination during editing, explicit save, autosave and render. The primary file remained byte-identical after a killed save, recovery selected the last complete generation, and the interrupted render left no final output. Observed the native startup recovery prompt. Also checked read-only writes, stale independent writers, and fallback after corruption. Tested explicit download, digest verification and removal of the optional 77,704,715-byte tiny.en model.
- Recomputed transcript, audio and scene analysis on the controlled three-hour fixture. Reviewed, checkpointed, committed, undid, redid, saved revision 6 and rendered a 599.43-second landscape edit plus three 20-second vertical Shorts. The batch took 144.75 seconds. The renderer peaked at 810,323,968 bytes of working set and used 130.63 CPU seconds through the run, excluding child encoders. Independent checks covered decoded audio, 55 source frames at edit boundaries and profile windows, and five timed caption positions with a caption-free control export.
- Release tests passed for Valencia (427), Copeland C# backend (277) and Aurelian Graphics (351). The qualification folder retains raw results and the actual output files.

The candidate was tested on one isolated Windows workstation. Broader Windows builds, a fresh VM, real human footage, distribution signing, Store publication and commercial codec clearance remain unqualified. The controlled fixture validates the editing and render path. Subjective editorial usefulness requires human footage and review.

## Programmable workflows

Use the revision-pinned agent tools to prepare, review and commit edits, save a project, build a render batch and poll job state. A Dominatus workflow can use the existing M3a adapter pattern and persist the project revision, candidate and render IDs as transitions complete. The terminal render statuses and lint findings provide explicit branches for review, retry and recovery. [Agent guide](M3A_AGENT_GUIDE.md) · [Render contracts](EXPORT_PROFILES.md)
