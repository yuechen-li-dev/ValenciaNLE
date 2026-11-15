# FilmCraft reference notes

Reference implementation: [storytold/filmcraft](https://github.com/storytold/filmcraft), pinned tree `1ec744c2ecbd15e31484caf62510f4bd9e570c21`. Source was inspected read-only during M2d; no code was copied and Valencia does not depend on FilmCraft or Rust.

| Inspected source | Useful concept | Valencia implementation decision |
|---|---|---|
| [project/caption.rs](https://github.com/storytold/filmcraft/blob/1ec744c2ecbd15e31484caf62510f4bd9e570c21/crates/project/src/caption.rs) | Captions belong to the semantic project | Source-aligned immutable `CaptionWord` and versioned `CreatorSettings` persist through Deliverance; edits move source placements |
| [engine/captions.rs](https://github.com/storytold/filmcraft/blob/1ec744c2ecbd15e31484caf62510f4bd9e570c21/crates/engine/src/captions.rs) | Caption operations use the editing boundary | Existing Core commands and atomic edit plans configure captions; no renderer-owned document |
| [audio-dsp/ducking.rs](https://github.com/storytold/filmcraft/blob/1ec744c2ecbd15e31484caf62510f4bd9e570c21/crates/audio-dsp/src/ducking.rs) | Derive deterministic gain envelopes offline | Consume existing streaming activity artifacts; avoid full-recording PCM/prefix arrays; evaluate linear dB keys in preview/export |
| [audio-dsp/loudness.rs](https://github.com/storytold/filmcraft/blob/1ec744c2ecbd15e31484caf62510f4bd9e570c21/crates/audio-dsp/src/loudness.rs) | Gated integrated loudness is distinct from peak normalization | Reuse FFmpeg loudnorm rather than port DSP; report peak-constrained target shortfalls |

This is a record of concepts adopted and implementation tradeoffs, not a comparative performance or product-quality claim. FilmCraft has paid for useful design exploration; Valencia still has to demonstrate its own correctness.
