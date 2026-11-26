# Valencia source and dependency notices

Valencia's source is provided under the GNU GPL version 3 in `LICENSE`. User-owned components may also be available under separately agreed commercial terms; inherited/third-party notices remain applicable.

Direct source integrations:

- **Copeland / Machina.UI / Aurelian.Graphics**: Yuechen Li and contributors, GPLv3, sibling `Copeland/LICENSE`. `ForwardTexturedM3.v.ts` and Ui2D MSDF/analytic shaders are linked from that checkout and compiled through the existing Copeland/Aurelian compiler. M2e directly references Aurelian.Machina.Graphics for glyph/atlas and shape adaptation; the prior bitmap presentation translator is no longer linked. Neither the Aetheris temporal policy nor the combined AGPL shader bundle is imported.
- **InputMan.Core**: Yuechen Li, MIT; notice retained in `licenses/InputMan-MIT.txt`.
- **Deliverance.Core**: Yuechen Li, MIT; notice retained in `licenses/Deliverance-MIT.txt`. Used by `Valencia.Persistence` for M1a project snapshots; the M0 player has no save UI.
- **Typography/OpenFont**, through Machina.Fonts: upstream authors and individual source-file notices; its supplied license notice is retained when present in `licenses`. See the source headers in Copeland's `src/ThirdParty/Machina.Typography.OpenFont` for constituent attribution.

Managed dependencies include **FFmpeg.AutoGen 7.1.1**, **NAudio.WinMM/Core 3.3.0**, **Silk.NET 2.23.0**, and the referenced libraries' explicit dependencies. [docs/dependencies.json](docs/dependencies.json) records resolved package versions, declared licenses and repository URLs. Package-supplied license/notice files are retained under `licenses/` when included in the packages.

**FFmpeg.AutoGen is a binding, not FFmpeg itself.** Native FFmpeg DLLs are user-supplied and must match the documented ABI. No FFmpeg native binary is copied into Valencia's published output by the build. The native distribution's actual configure flags, libraries, source availability and applicable license terms remain its own distribution inventory; the managed binding's MIT license does not replace those terms. See [FFmpeg licensing](https://ffmpeg.org/legal.html).

M1b export additionally invokes the user-installed FFmpeg executable, using its **libx264** H.264 and native AAC encoders. Validation used Gyan FFmpeg 7.1.1 shared, built with GPL/version3 support. The executable and its codec libraries are not redistributed by this project. No new managed packages were added for M1b.

Test fixtures are generated locally from FFmpeg's synthetic test patterns and sine generator. No user footage or third-party media is bundled.

M2d adds **SkiaSharp 3.119.1** and **SkiaSharp.NativeAssets.Win32 3.119.1** for CPU caption rasterization. Their MIT license and native constituent notices are retained in `licenses/SkiaSharp-MIT.txt` and `licenses/SkiaSharp-native-NOTICES.txt`. Windows Arial/Consolas fonts are read from the user's installed system; their font files are not redistributed. Noise suppression uses the user's FFmpeg `libavfilter`/`afftdn`; loudness measurements use its `loudnorm` implementation. FilmCraft source was consulted for design concepts only, as recorded in `docs/filmcraft-reference.md`; no Rust implementation or runtime dependency was imported.

M2a directly references **Copeland.TS.Backend.CSharp** under Copeland's existing GPLv3 terms. Runtime script assembly compilation uses **Microsoft.CodeAnalysis.CSharp 4.14.0** (MIT), now explicitly pinned in Valencia.Scripting; Roslyn was already in the resolved dependency inventory. MCP is a small optional protocol adapter in the CLI, with no additional SDK/package dependency. No Node.js, npm or JavaScript runtime is shipped or required.

The historical M2c CPU path used **Whisper.net 1.9.1** and **Whisper.net.Runtime 1.9.1** (MIT). The default Valencia build now uses Aurelian.Inference and its Visual TypeScript compute shaders. `licenses/Whisper.net-MIT.txt` and `licenses/whisper.cpp-MIT.txt` retain attribution. **System.Speech 10.0.0** (MIT) generates Windows test fixtures. **MathNet.Numerics 5.0.0** (MIT, `licenses/MathNet-MIT.txt`) remains in the separate GPU research executable. Neither package is an application runtime dependency.

Whisper model weights are not bundled. The explicit downloader fetches original Whisper GGML conversions from `ggerganov/whisper.cpp` at revision `5359861c739e955e79d9a303bcbc70fb988958b1` and verifies SHA-256. [OpenAI Whisper](https://github.com/openai/whisper) publishes its code and model weights under MIT; [whisper.cpp](https://github.com/ggml-org/whisper.cpp) is MIT. Retain upstream attribution when distributing models. See `docs/M2C.md` for exact model identities, byte sizes, hashes and redistribution boundaries. No model or speech dataset is silently downloaded by the application or tests.

M2e adds **System.Security.Cryptography.ProtectedData 10.0.0** (Microsoft, MIT, https://github.com/dotnet/dotnet) for Windows CurrentUser DPAPI. Rendering also explicitly references the existing SkiaSharp/Win32 3.119.1 packages for native-pixel fallback text. **Space Mono Regular** and **Crimson Text Regular** retain their supplied SIL Open Font License notices in `licenses/SpaceMono-OFL.txt`, `licenses/CrimsonText-OFL.txt` and published `Assets/Fonts`. Installed Windows fallback fonts are not redistributed. OBS Studio is not shipped as a Valencia product dependency; the installed build is copied only into ignored qualification artifacts for an isolated fixture. User OBS profiles are untouched.

The Edge Code UI style ships three SIL Open Font License 1.1 families in `src/Valencia.Rendering/Assets/Fonts` (published to `Assets/Fonts`), rasterized at runtime by SkiaSharp:

- **IBM Plex Mono** Regular and Medium, Copyright IBM Corp., unmodified files (Plex carries the Reserved Font Name "Plex"). `licenses/IBMPlexMono-OFL.txt`.
- **Archivo Expanded Bold**, Copyright The Archivo Project Authors. A static instance (wdth 125, wght 700) of the Archivo variable font, renamed; no Reserved Font Name. `licenses/Archivo-OFL.txt`.
- **Noto Serif Display ExtraCondensed Black**, Copyright Google LLC / The Noto Project Authors. A static instance (wdth 62.5, wght 900) of the Noto Serif Display variable font, renamed. `licenses/NotoSerifDisplay-OFL.txt`.

The instances were made with fontTools `instancer`; the modified fonts remain under the OFL and are not sold by themselves.
