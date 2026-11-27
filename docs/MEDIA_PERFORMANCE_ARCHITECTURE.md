# Media performance architecture

## Decode and transfer

The timeline selects media against actual FFmpeg presentation timestamps in the stream rational timebase. The decoder fetches the preceding keyframe and decodes forward, with a ref-counted picture and one lookahead to determine the interval. A D3D11 picture establishes hardware decode when it has a hardware frames context and a texture. The product doctor reports device initialization separately from codec configuration and source negotiation. A negotiation miss keeps software output. D3D11 start, receive, submit and transfer failures reopen a software cursor at the original requested source position. `VALENCIA_D3D11_VENDOR_ID` and `VALENCIA_D3D11_ADAPTER` select an adapter for an explicit trial. [FFmpeg hardware decode example](https://ffmpeg.org/doxygen/7.1/hw__decode_8c_source.html)

D3D11 decoding holds only the selected and lookahead surfaces outside the codec. FFmpeg reuses a D3D11 staging texture. The selected NV12/P010 planes copy from mapped staging to reusable host planes, then unpack to reusable planar storage. The planar conversion keeps the software chroma sampler, range and matrix. It eliminated edge discrepancies seen when swscale converted NV12 directly. Final RGBA is independently owned by the returned sequence frame. Queued generations can retire without retaining a decoder texture. [FFmpeg 7.1 D3D11 transfer implementation](https://www.ffmpeg.org/doxygen/7.1/hwcontext__d3d11va_8c.html)

At 1920×1080, visible NV12 is 3,110,400 bytes and RGBA8 is 8,294,400. At 2560×1440, those are 5,529,600 and 14,745,600 bytes. A surface copy and Map synchronize the CPU download. The independent Vulkan presenter uploads RGBA into a persistent texture and can hold the last host frame for resize recovery. Plain one-layer identity-size forward playback and export transfer the decoded RGBA array to the result, avoiding a second array, alpha initialization and composition copy. A repeated picture rematerializes from its retained native frame on this path.

FFmpeg's default D3D11 decoder texture pool consists of decoder-bound texture subresources. The current pool lacks the shared handles and the Vulkan external memory and fence ownership required for direct import. Aurelian creates its Vulkan device independently. The candidate uses bounded host staging, including when decoded and presented by the same GPU. Decode-engine counters and pictures reaching the Vulkan presenter are separate measurements. [FFmpeg D3D11 frame and device contracts](https://ffmpeg.org/doxygen/7.1/hwcontext__d3d11va_8h_source.html)

## Composition and export

Vulkan effect passes reuse their mapped buffers. Consecutive passes keep one target between dispatches. The evaluator flushes before a CPU geometry, title or caption pass, before dissolve target switches and before returning pixels. The boundary waits on one fence per pass. A failed submission retires the effects evaluator before another dispatch can reuse unresolved storage. Titles, transitions and the encoder still require CPU-backed buffers. The native Vulkan presenter composites the uploaded result with the editor UI.

Software and NVENC exports use the same exact compositor and mix. The encoder child reads bounded RGBA and PCM pipes and runs below normal Windows priority. Originals remain authoritative for offline export. Encoder success, independent media checks and frame throughput are reported separately.

## Indexing, proxies and foreground work

The audio seek cache stores original decoder sample counts on roughly two-second anchors, including forward clock epochs. Identity uses file size, modification time, sampled contents, stream, and FFmpeg decoder ABI policy. Durable files are checksummed, capped at 24 entries of at most one MiB each, and atomically replaced. Interrupted construction can reuse a committed prefix. Background indexing yields between 120-second source spans. Import and paused pictures proceed without full audio indexing. Video uses container cues and decoder preroll.

Automatic preview proxies are considered when high-resolution or long video is inserted. Always enables the same path at lower resolutions. Never selects originals. The native preparation job uses the existing analysis queue, can be cancelled, and switches at the next new preview cursor. The bundled VP9 profile has a 640-pixel width limit and a 12-picture GOP. It verifies the complete video packet presentation clock with a bounded reorder window before publication. Old explicitly attached H.264 proxy entries remain readable. Audio and final exports use originals. Generated proxies have a four-GiB cache budget.

One native preview opening worker owns at most one latest pending drag position in addition to its active request. Seek generations reject obsolete results, and reverse uses its bounded software history. Export stays pinned to an immutable revision. Dominatus can coordinate jobs at the existing application boundary.
