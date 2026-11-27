# M3c — Editorial intelligence and Copeland workflows

The editorial catalog plans against a private sequence. It returns ordinary Valencia edit operations that the existing candidate review, commit, undo, checkpoint and render queue can consume. A catalog entry is selected by ID and version. Copeland CLR executes the editorial composition. The creative parts for titles, captions, vertical outputs and layouts use `template <...>` declarations. [Editorial workflows](EDITORIAL_WORKFLOWS.md)

## Footage observations

| Observation | Provider and behavior |
|---|---|
| Topics | Local lexical changes and aligned speech gaps over existing transcript segments. Queryable alongside explicitly submitted external topic labels. |
| On-screen text | Optional Tesseract CLI, decoded sampled frames, actual source PTS, normalized line boxes, confidence and repeated-text suppression. |
| Visual descriptions and annotations | Validated timestamped external artifacts, with provider, version, configuration and the caller's declared evidence type. |
| Subjects and faces | Validated external tracks and boxes. Vertical profiles can follow one track with a deadband, speed limit and fallback through gaps. |
| Speakers and beats | Validated external observations, including separate anonymous labels or overlapping intervals. Local inference is unavailable in this candidate. |

The standard release uses the existing Whisper provider for transcripts and existing media analysis for silence, energy and scenes. Tesseract and its language data are optional. The external observation interface consumes data supplied by the caller. The timeline renderer applies the stored framing and caption settings in preview and export. [Component inventory](THIRD_PARTY_COMPONENTS.md)

## Qualification

The packaged Copeland, CLI and MCP paths produced the same 52-operation plan from the existing three-hour artifact set. The proposal produced a ten-minute landscape edit and three 10.93-second vertical Shorts. All four outputs were independently decoded. Qualification checked 61 source frames and ten timed caption positions across the landscape and vertical outputs. The long-form VP9/Opus output measured −16.7 LUFS and −1.4 dBTP. The three AV1/Opus Shorts measured −16.4 LUFS and −1.5 dBTP. The batch took 99.5 seconds and the renderer process peaked at 293 MB of working set, excluding child encoders.

One matched MCP benchmark compared sending that existing canonical plan with asking the registered workflow to build it. Both prepared an identical candidate in one call. The request decreased from 506,976 to 575 UTF-8 bytes. The workflow returned more evidence and took 34.06 seconds including plan construction and audio analysis. The already-authored plan submission took 0.234 seconds. These are measured calls and wire bytes. The benchmark did not observe LLM token usage or manual authoring time.

The native editor smoke exercised a registered lower third through preview, explicit Apply and one Undo. The optional Dominatus sample prepared the same kind of workflow, generated a paired review and reached `AwaitingSelection`. Release tests passed for 431 Valencia cases.

The OCR fixture yielded two expected headings, boxes, actual timestamps and stable-text deduplication. On authentic NASA narration, the topic workflow retained a 26.8-second explanation of the Lagrange 1 orbit. OCR read several actual overlay labels and also returned imperfect words. Technical verification covers the retained project, encoded streams and OCR output. Editorial quality still needs human review. [NASA source](https://svs.gsfc.nasa.gov/12312/) · [Validation record](validation/m3c.json)

The unsigned 0.3.0-rc.2 installer and portable ZIP were qualified on one Windows 11 workstation with a system-only PATH. Upgrade from 0.3.0-rc.1 and uninstall preserved fixture settings. Optional detector availability and broader Windows qualification remain limited to the evidence recorded here.
