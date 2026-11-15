# Claude review — 2026-11-15

Run on the RTX 3070 desktop: build clean; 261 tests pass (Core 73, Persistence 21, Media/UI 82, Programming 85); `Smoke.ps1` and `EditorSmoke.ps1` pass.

## Changed (frontend, composed from existing Core operations; no reducer or schema change)

| Area | Change |
|---|---|
| `EditorEdits.cs` (new) | Linked-clip inference (same asset, different stream, overlapping, equal source-minus-sequence intercept); edit-point navigation; Q/W ripple trim; lift/extract; ripple insert and overwrite |
| `EditorDocument.cs` | Transient `MarkIn`/`MarkOut`; Delete/Shift+Delete act on the linked group. Ripple refuses when another unlocked track has unrelated media in the range (lock that track to ripple without it) instead of silently desyncing |
| `EditorKeymap.cs` | I, O, Ctrl+Shift+X, Q, W, Up, Down, Home, End, comma, period, semicolon, apostrophe, matched on key-down outside InputMan's key enum |
| `EditorWindow.cs`, `EditorView.cs` | Dispatch for the above; In/Out range drawn on the ruler. The Insert button keeps its overlap-rejecting behavior |
| `tests/Valencia.Tests/PremiereEditTests.cs` | 8 tests covering the above |

Locked tracks count as untargeted. Linked inference is not persisted; a Core link ID would be the durable form.

## Backend asks (Codex)

1. **Audio seek on long files** (3–13 s per three-track seek on 2 h) is the main blocker for the streamer use case. Persist qualified sample anchors per asset.
2. **J/K/L shuttle**: reverse and >1× playback need decoder/playback support; J currently pauses.
3. **Linked clips in Core**: a persisted link group would replace UI inference and enable linked drag/trim.
4. **Codec dependency**: users must supply FFmpeg 7.1 shared DLLs with exact ABI plus `ffmpeg.exe` with libx264. That's fine for development, but a streamer can't install it. The spec route was OS decode/encode (Media Foundation/NVENC) plus built-in AV1/VP9.
5. **A/V skew metric**: M0 smoke reports 79–86 ms absolute skew against a 150 ms gate. Report it signed, since audio-leading is noticeable from about 45 ms, and tighten the gate.
6. Ripple insert (`,`) shifts all unlocked tracks; source-monitor In/Out for 3-point edits is still absent.
