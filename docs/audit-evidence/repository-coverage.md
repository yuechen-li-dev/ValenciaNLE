# Repository discovery coverage

Observed November 7, 2026 (user timezone). Inventory is bounded to the supplied source root and the authenticated GitHub result set. Project counts reflect rg-visible manifests, excluding bin/obj/node_modules; they are not capability counts. Non-C# repositories were screened rather than exhaustively audited.

## Local directories

| Directory | C# projects | Role | Audit coverage |
|---|---:|---|---|
| `Aetheris` | 92 | CAD, geometry and Firmament | Selected source; no reuse recommended |
| `app` | 0 | Unidentified local directory | No matching manifests found |
| `Ariadne` | 4 | Dialogue/compiler and Stride integration | Manifests + media keyword screen; dirty baseline |
| `BenchCAD-main` | 0 | Third-party CAD benchmark corpus/harness | Manifest/domain screen; non-C# outside deep scope |
| `Calculator` | 1 | ASP.NET + JS application sample | Manifests/media keyword screen |
| `chatgpt-vellum` | 0 | Browser extension | Manifest/domain screen; frontend unsuitable |
| `chatgpt-virtualizer` | 0 | Browser virtualization extension | Manifest/domain screen; frontend unsuitable |
| `CodeNativeDesignDemo` | 1 | CAD design workflow example | C# manifests/media screen; no media pipeline found |
| `Concept` | 0 | Native language compiler | Manifest/license/provenance screen; non-C# deep audit deferred |
| `ConsoleApp1` | 1 | C# console sample | Project discovery/media keyword screen |
| `ConsoleApp2` | 1 | C# console sample | Project discovery/media keyword screen |
| `Copeland` | 256 | Compiler, Aurelian, Machina, Oblivion, game integrations | Deep selected C# source/graph + existing binary tests; concurrent uncommitted cache/state additions source-read |
| `Database-Scheduler` | 0 | OctetDB database scheduler | Manifest/domain screen; not media job scheduler qualification |
| `Deliverance` | 4 | C# save containers, schema upgrades and file store | Deep core source + selected binary tests |
| `deps` | 0 | Local dependency/AssemblyProcessor directory | No matching project manifests found by rg |
| `Dominatus` | 77 | AI world, async actuation, agents and adapters | Core source + selected binary tests; optional modules screened |
| `Flexture` | 0 | Named layout directory | No matching manifests in this directory; nested copy inspected by discovery |
| `Godot` | 0 | Engine directory | No matching manifests found; no owned C# NLE library established |
| `GumSole` | 5 | Gum/Stride C# UI integration | Manifest/source screen; dirty baseline; full engine coupling |
| `HeliosCAD` | 3 | React/Three CAD frontend and SolidWorks tools | Manifest/source topology; reject frontend |
| `InputMan` | 14 | Logical input, chords, profiles, rebinding | Core source/integration references; initially dirty, concurrent f9fe1f08 commit and new tests source-read |
| `IrizaUI` | 5 | Experimental game UI adapters | C# manifest/source screen; dirty baseline |
| `kanban` | 0 | Local application directory | No matching manifests found |
| `learn-golang` | 0 | Go learning examples | Inventory only; outside C# deep scope |
| `Leviathan` | 15 | Conversation/document server and Dominatus integration | Manifests + selected dependency boundaries |
| `machina-canvas` | 0 | JS canvas application/modules | Manifest/domain screen; no native runtime reuse |
| `MachinaLayout.JS` | 0 | JS layout/viewport library | Manifest/domain screen; design reference only |
| `MetaVersus` | 0 | Browser/blocker tooling | Manifest/domain screen; unsuitable runtime dependency |
| `NavierStokes` | 0 | Simulation directory | No matching manifests found |
| `New folder` | 0 | Unidentified directory | No matching manifests found |
| `oct` | 3 | Scientific language and Prometheus GPU implementation | Manifest/provenance/source-location screen; non-C# deep audit deferred |
| `octave-modern` | 0 | Scientific-language predecessor directory | No matching manifests in this local directory; remote metadata exists |
| `PCBA` | 0 | PCBA DFM web/Tauri application | Manifest/media keyword screen; no native C# NLE foundation |
| `PersonalWebsite` | 1 | Website/server sample | Manifest/media keyword screen |
| `Project1` | 1 | Local C# sample | Project discovery/media keyword screen |
| `ReactApp1` | 1 | ASP.NET/React application sample | Manifest/media keyword screen; reject JS frontend |
| `Riemann` | 1 | Optimization/compiler research | Manifest/domain screen; dirty baseline; not media engine |
| `roadkill` | 8 | C# web/wiki API stack | Manifest/source/media screen; inappropriate full-stack dependency |
| `RunwayUI` | 0 | UI-named directory | No matching manifests found |
| `SampleRequest` | 1 | Local C# sample | Project discovery/media keyword screen |
| `SMlib_Concept` | 0 | Concept math library research | Inventory only; outside C# deep scope |
| `SSP` | 0 | Unidentified directory | No matching manifests found |
| `StrideTurnBasedRPG` | 0 | Game-named directory | No matching manifests found; not equated with remote tactics repo |
| `Tominal` | 0 | Terminal/tool directory | No matching manifests found; remote TOMinaL-Studio not assumed identical |
| `truck` | 0 | Rust geometry/CAD fork | Manifests/domain screen; outside C# deep scope |
| `tspack` | 4 | Build/compiler integration tooling | Manifest/source-target fixture discovery; dirty baseline |
| `ValenciaNLE` | 0 | Requested audit destination | Initially empty/no Git repository; documentation/evidence only |
| `vitest-tspack` | 0 | JS test tooling fork | Manifests/domain screen; no native media capability credited |
| `WatermarkingExperiment` | 2 | Windows C# image watermarking | Project/media keyword screen; no root license found |
| `YAFSStride` | 0 | Azure game repository placeholder | Remote discovered; no HEAD/project content qualified |
| `yuechen-li-dev` | 5 | Nested NSLab, OctetDB, Flexture and website copies | Nested manifests/licenses discovered; no duplicate runtime selected |
| `yuechen-li-dev.github.io` | 0 | Static website/document outputs | Manifest/domain screen; outside native foundation |

## Authenticated GitHub inventory

Returned 67 repositories; next page empty. `Local remote` means a matching GitHub remote was discovered, not that local HEAD equals the remote default branch. Remote API sizes are inventory hints only.

| Repository | Availability | Role / coverage |
|---|---|---|
| [yuechen-li-dev/learn-golang](https://github.com/yuechen-li-dev/learn-golang) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/roadkill](https://github.com/yuechen-li-dev/roadkill) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/StrideTurnBasedTactics](https://github.com/yuechen-li-dev/StrideTurnBasedTactics) | Remote only | Game application — Metadata only; not source-qualified |
| [yuechen-li-dev/Ariadne](https://github.com/yuechen-li-dev/Ariadne) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/AriadneStrideSandbox](https://github.com/yuechen-li-dev/AriadneStrideSandbox) | Remote only | Dialogue/game integration sample — Metadata only; not source-qualified |
| [yuechen-li-dev/octave-modern](https://github.com/yuechen-li-dev/octave-modern) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/PersonalWebsite](https://github.com/yuechen-li-dev/PersonalWebsite) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/StrideCleanUIPostFXPatch](https://github.com/yuechen-li-dev/StrideCleanUIPostFXPatch) | Remote only | Game UI rendering patch — Metadata only; not source-qualified |
| [yuechen-li-dev/GumSole](https://github.com/yuechen-li-dev/GumSole) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/Gum](https://github.com/yuechen-li-dev/Gum) | Remote only | Upstream game UI fork — Metadata only; C# lead, not source-qualified |
| [yuechen-li-dev/InputMan](https://github.com/yuechen-li-dev/InputMan) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/stride](https://github.com/yuechen-li-dev/stride) | Remote only | Upstream game engine fork — Metadata only; not conflated with Stri-V code |
| [yuechen-li-dev/Deliverance](https://github.com/yuechen-li-dev/Deliverance) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/GumSoleStrideTest](https://github.com/yuechen-li-dev/GumSoleStrideTest) | Remote only | UI/game integration sample — Metadata only; not source-qualified |
| [yuechen-li-dev/SkiaTest2](https://github.com/yuechen-li-dev/SkiaTest2) | Remote only | Native drawing sample lead — Metadata only; not source-qualified |
| [yuechen-li-dev/IrizaUI](https://github.com/yuechen-li-dev/IrizaUI) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/stride-docs](https://github.com/yuechen-li-dev/stride-docs) | Remote only | Game engine documentation — Metadata only |
| [yuechen-li-dev/Dominatus](https://github.com/yuechen-li-dev/Dominatus) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/chatgpt-virtualizer](https://github.com/yuechen-li-dev/chatgpt-virtualizer) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/Flexture](https://github.com/yuechen-li-dev/Flexture) | Nested local copy + remote metadata | Remote tree plus nested local manifest discovery; not preferred over active Machina |
| [yuechen-li-dev/Aetheris](https://github.com/yuechen-li-dev/Aetheris) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/GenerativeCompressionProtocol](https://github.com/yuechen-li-dev/GenerativeCompressionProtocol) | Remote only | Compression research lead — Metadata only |
| [yuechen-li-dev/TheChimeraPrompt](https://github.com/yuechen-li-dev/TheChimeraPrompt) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/NSLab](https://github.com/yuechen-li-dev/NSLab) | Nested local copy + remote metadata | Archived; nested local manifests found |
| [yuechen-li-dev/TOMinaL-Studio](https://github.com/yuechen-li-dev/TOMinaL-Studio) | Remote only | Terminal/tooling lead — Metadata only; local name resemblance not proof of same checkout |
| [yuechen-li-dev/primer](https://github.com/yuechen-li-dev/primer) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/Margaret](https://github.com/yuechen-li-dev/Margaret) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/vulkan-sgemm-bench](https://github.com/yuechen-li-dev/vulkan-sgemm-bench) | Remote only | GPU matrix benchmark — Metadata only; media fit not established |
| [yuechen-li-dev/ClaudesGuide](https://github.com/yuechen-li-dev/ClaudesGuide) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/Refraction](https://github.com/yuechen-li-dev/Refraction) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/RFCompressionReplay](https://github.com/yuechen-li-dev/RFCompressionReplay) | Remote only | Compression/replay experiment lead — Metadata only; no media pipeline credited |
| [yuechen-li-dev/oct](https://github.com/yuechen-li-dev/oct) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/TheoryOfIntelligence](https://github.com/yuechen-li-dev/TheoryOfIntelligence) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/MarionetteSSE](https://github.com/yuechen-li-dev/MarionetteSSE) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/DragonGod](https://github.com/yuechen-li-dev/DragonGod) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/Copeland](https://github.com/yuechen-li-dev/Copeland) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/worst](https://github.com/yuechen-li-dev/worst) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/NopePad](https://github.com/yuechen-li-dev/NopePad) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/MarionetteTests](https://github.com/yuechen-li-dev/MarionetteTests) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/marionette-go](https://github.com/yuechen-li-dev/marionette-go) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/Dreadfang](https://github.com/yuechen-li-dev/Dreadfang) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/chatgpt-vellum](https://github.com/yuechen-li-dev/chatgpt-vellum) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/MachinaLayout.JS](https://github.com/yuechen-li-dev/MachinaLayout.JS) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/stri-v](https://github.com/yuechen-li-dev/stri-v) | Remote only | Stride hardfork graphics/video — Selected remote source/project/license audit; no build; R3 extraction, R4 unchanged media timing |
| [yuechen-li-dev/tspack](https://github.com/yuechen-li-dev/tspack) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/Dunewyrm](https://github.com/yuechen-li-dev/Dunewyrm) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/WyrmCoil](https://github.com/yuechen-li-dev/WyrmCoil) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/Aurelian](https://github.com/yuechen-li-dev/Aurelian) | Remote only | Earlier standalone graphics/engine — Tree/README inspected; prefer active Copeland version |
| [yuechen-li-dev/yuechen-li-dev](https://github.com/yuechen-li-dev/yuechen-li-dev) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/Concept](https://github.com/yuechen-li-dev/Concept) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/yield-return](https://github.com/yuechen-li-dev/yield-return) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/Sibyl](https://github.com/yuechen-li-dev/Sibyl) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/Leviathan](https://github.com/yuechen-li-dev/Leviathan) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/Ariadne.Player](https://github.com/yuechen-li-dev/Ariadne.Player) | Remote only | Dialogue player lead — Metadata only; not source-qualified |
| [yuechen-li-dev/yuechen-li-dev.github.io](https://github.com/yuechen-li-dev/yuechen-li-dev.github.io) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/Machina-Canvas](https://github.com/yuechen-li-dev/Machina-Canvas) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/Odyssey](https://github.com/yuechen-li-dev/Odyssey) | Remote only | Other repository — Metadata only; source not inspected, suitability Unknown |
| [yuechen-li-dev/Riemann](https://github.com/yuechen-li-dev/Riemann) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/MetaVersus](https://github.com/yuechen-li-dev/MetaVersus) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/OctetDB](https://github.com/yuechen-li-dev/OctetDB) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/vitest-tspack](https://github.com/yuechen-li-dev/vitest-tspack) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/visionary](https://github.com/yuechen-li-dev/visionary) | Remote only | Web code-editor/tooling tree — Recursive tree screen; not native frontend |
| [yuechen-li-dev/agentic-control-theory](https://github.com/yuechen-li-dev/agentic-control-theory) | Remote only | Agent research — Metadata only; no runtime capability credited |
| [yuechen-li-dev/HeliosCAD](https://github.com/yuechen-li-dev/HeliosCAD) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/CodeNativeDesignDemo](https://github.com/yuechen-li-dev/CodeNativeDesignDemo) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/truck](https://github.com/yuechen-li-dev/truck) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |
| [yuechen-li-dev/Aetheris-PCBA-DFM](https://github.com/yuechen-li-dev/Aetheris-PCBA-DFM) | Local remote discovered | See local directory coverage and exact local HEAD; remote default branch not freshly synchronized |

## Unidentified named candidates

IntelliDraft and ROALoop were not identified in this inventory. This is an access/evidence unknown, not a claim that no private or differently named implementation exists. SDSL-V/Prometheus/Machina are subsystem names resolved to their owning repositories in the report.
