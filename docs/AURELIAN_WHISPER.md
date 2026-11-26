# Aurelian Whisper in Valencia

Valencia uses Aurelian Whisper for the Speech panel, CLI, scripts and MCP jobs. Local speech currently supports the pinned `tiny.en` English checkpoint. Install it with `scripts/DownloadWhisperModels.ps1 -Model tiny.en` and set `VALENCIA_MODEL_DIR` to its directory. Inference uses a Vulkan device with subgroup cooperative matrices that accept FP16 inputs and accumulate in FP32. Aurelian.Inference embeds the precompiled SPIR-V modules. DXC and `spirv-val` are needed to regenerate the shader pack during development.

Valencia keeps its existing FFmpeg decode, mono 16 kHz resampling, source timebase, discontinuity handling, bounded chunks and overlap ownership. Aurelian computes log-mel features, runs the packed-resident encoder and decoder, and aligns the decoded tokens using selected cross-attention heads from a batched causal decoder replay. The `native` and `dtw` alignment settings both select this provider's DTW alignment. Transcript artifacts retain the original DTW range and the PCM-trimmed range. A collapsed boundary is marked unusable for text edits.

Device, model and runtime errors fail the transcription job explicitly. Aurelian is the only inference provider. The v2 algorithm ID gives full transcripts and chunk entries new cache keys. When decoding stops before the end of a window, Aurelian can continue from a closing timestamp or the next opening timestamp. It aligns the accepted text through the closing timestamp and offsets the continuation's word ranges back to the source. The runtime tries up to three additional windows while each boundary advances and the remaining audio contains at least three active 20 ms frames. Existing artifacts remain readable by key.

The output and editor workflow are under pre-release qualification. Shipping requires word-boundary checks on real editorial footage, long speech and silence, playback contention, and testing on each supported GPU configuration.

## Local qualification

The Windows RTX 3070 run passed all 396 Valencia tests and the published desktop transcript smoke. The smoke recognized 19 words, navigated to the source, previewed and cancelled a cut without editing the project, then applied and exported the cut with undo and redo. The generated speech fixture had zero word errors and 77 ms mean word-start error across 15 uniquely matched words. The repeated-speech check recovered all 13 announcements in 120 seconds, and the two-hour sparse-speech fixture preserved the words near the end through 258 audio chunks.

The published desktop passed the transcript smoke with the Vulkan SDK tools off PATH and DXC disabled. On the same RTX 3070, the PCM-to-word benchmark measured 75.51 ms warm for generated Valencia speech and 78.06 ms for JFK. Batched replay preserved every word and sample range from the earlier runtime on both clips and on a 30-second repeated-speech window. Creating the model session and completing the first short request took about 0.87 s with embedded shaders, down from 3.8 s with runtime compilation.

The fixture suite covers source-clock mapping and failure/cancellation behavior. Accuracy and playback contention on real editorial media are still release checks. Whisper.cpp remains a benchmark reference.

## Tail recovery on human narration

The 30-second repeated-speech fixture previously ended after the third complete repetition. The decoder closed that segment at 27.52 s, opened the next segment at 28.40 s, and emitted end-of-text. Resuming at 28.40 s recovered the three spoken words at 28.53 to 29.68 s. The first 57 words matched the baseline. A regression test exercises this through Valencia's normal chunking path.

The published stdio MCP processed the complete 166.90 s NASA Goddard narration and produced a valid caption plan. The test imported a local H.264/PCM Matroska conversion of the video. On the same input the baseline had 34 word errors and the v2 runtime had 19 against 375 case-folded alphanumeric tokens in NASA's captions, or 9.1% and 5.1%. The fixed artifact had 366 words, 365 with usable timing. The baseline had 344 words, 343 timed. [NASA narration and captions](https://svs.gsfc.nasa.gov/12312/)

The Apollo 11 radio call hit the Tiny.en token limit in both builds. The same failure occurred with the original Ogg and with H.264/PCM Matroska. The headless job reported the decoder limit. [NASA Apollo 11 call](https://commons.wikimedia.org/wiki/File:President_Nixon_telephones_the_Apollo_11_crew_on_the_Moon.ogv)

Raw footage comparisons and MCP evidence are under `artifacts/local/whisper-tail/`.
