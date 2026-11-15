# Valencia native frontend brief

Valencia is a native, programmable video editor that makes creator workflows fast and controllable by humans and AI agents. It should feel professional, compact and distinctive. Use familiar source/program, track, trim, playhead and bin concepts without copying Adobe's visual skin.

Use C# Machina.UI, existing Aurelian native rendering, InputMan and authoritative Core/Engine operations. Keep the custom GPU timeline and visible-range culling. No browser frontend, React, Electron, Node runtime or new UI framework. Retain the three explicit roots: desktop/landscape tablet, portrait tablet, portrait phone. Preserve shared MSDF font metrics and measured wrapping; do not reinstate a stretched UI bitmap. Portrait Windows captures do not imply mobile-platform support.

Prioritize navigation/editing, media/transcript inspection, inspectable scripts/plans, recording-to-export clarity, OBS, captions/Shorts, keyboard use, then polish. Automated proposals must remain understandable and reversible: show source evidence, track scope, revision, marker uncertainty and proposed duration, with explicit Apply/Cancel. A valid proposal is not automatic approval.

Record, Edit, Transcript, Automate and Export are workflow concepts, not a mandate for five windows/tabs. Reuse Machina components/layout primitives; avoid speculative docking/customization. Show job cancellation, errors, missing media and recording continuity honestly. OBS may continue after Valencia disconnects. Stop acknowledgment is not media completion. Microphone, game/music and communications need understandable roles.

The backend supports text cuts, heuristic creator proposals, audio finishing, captions and long/vertical profiles. The UI exposes only part of this. Do not imply semantic highlight understanding, arbitrary speech accuracy, sample-accurate OBS markers, a script sandbox, linked A/V gestures or completed mobile support. Long exact audio seeking remains a measured backend performance problem and needs candid loading/cancellation behavior.

Read [the handoff/API map](CLAUDE_FRONTEND_HANDOFF.md), [qualification](M2E_REAL_WORLD_QUALIFICATION.md) and [M2e](M2E.md). Codex owns semantic/media correctness and executable qualification; Claude owns native presentation/interaction against those contracts. Request narrow semantic extensions when necessary; do not create a second editor in UI state. Deliver a usable creator workflow, not a static visual mockup.
