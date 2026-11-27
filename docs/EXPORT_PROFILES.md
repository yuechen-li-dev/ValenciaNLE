# Valencia export profiles

| Preset | Geometry | Default encoder | Container and sound |
|---|---|---|---|
| landscape-1080p60 | 1920×1080, 60 fps | libvpx VP9 | WebM, Opus |
| landscape-1440p60 | 2560×1440, 60 fps | libaom AV1 | WebM, Opus |
| vertical-1080x1920 | 1080×1920, 60 fps | libvpx VP9 | WebM, Opus |
| review-360p | 640×360, 30 fps | libvpx VP9 | WebM, Opus |
| intermediate-1080p | 1920×1080, 30 fps | FFV1 | Matroska, FLAC |

These are version-1 Valencia presets. A project output profile can also pin the output range, framing and caption baseline. Use an explicit encoder ID to select an alternative. `export_options` with `probe:true` actually attempts brief encodes and identifies the selected implementation and any runtime refusal. H.264/AAC and HEVC/AAC MP4 can use an explicitly configured `VALENCIA_COMPAT_FFMPEG_EXE` provider. Available hardware is determined by executed probes.

`render_queue` accepts `enqueue` with one `item`, or `batch` with up to four `items`. Each item takes a sequence handle, new destination, optional project profile, explicit encoder, built-in `preset` or version-1 `customPreset`, optional quality, caption mode and finishing settings. Use `list`, `status`, `cancel`, `retry` and `lint` to manage the captured jobs. Queue jobs preserve the snapshot and media fingerprints captured on acceptance. In a new process, interrupted jobs can be restarted from that snapshot. A retry uses a new job ID.

Finishing applies the selected BS.1770/EBU R128 loudnorm policy after the existing timeline audio mix, ducking, cleanup, envelopes and sample limiter. Delivery targets follow the destination (`LoudnessTargets`): −14 LUFS for video platforms and vertical shorts (YouTube, Shorts, TikTok, Reels), −16 LUFS for profiles named as podcast or spoken-word, both with −1 dBTP and an 11 LU range. Built-in presets and creator profile exports pick the target per output. The M3b/M3c measurements below were taken at the earlier −16 LUFS default. The filter reserves 0.5 dB of headroom for Opus. AAC uses a 2 dB limiter margin with 0.65 dB post-filter makeup. The FFV1 preset preserves the mix. Lint checks the encoded file's loudness and true peak. The limits are ±1 LU for integrated loudness and +0.1 dB relative to the peak ceiling. Stream or duration mismatches can fail a job. Potential black or frozen ranges and word-boundary edits are review findings. [FFmpeg loudnorm](https://ffmpeg.org/ffmpeg-filters.html#loudnorm)

Render duration is rounded up to one output frame and then expressed at the delivery sample rate. Lint allows the greater of 50 ms or one and a half output frames for container duration. Clip indicators are compared against 0 dBTP. The independent fractional-rate proof checks decoded audio duration within 50 ms and the sound/picture gap boundaries within 50 ms. [Qualification](M3B.md)
