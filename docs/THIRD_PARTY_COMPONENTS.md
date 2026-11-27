# M3b distribution components

The package manifest `docs/installed-components.json` is generated from both host dependency graphs and the actual package files. It records exact NuGet versions, relative paths, sizes and SHA-256. `licenses/` contains package-supplied notices and Valencia's source notice.

| Component | Release and provenance | License and distribution |
|---|---|---|
| Valencia | Source archive and commit in `valencia-release.json` | GPL-3.0, managed assemblies and precompiled assets with corresponding checkout |
| Copeland, Machina.UI and Aurelian | Sibling commit archives named in the manifest | GPL-3.0 integration, managed assemblies, shader artifacts and source |
| Deliverance and InputMan | Pinned sibling commit archives | MIT, managed assemblies and retained notices |
| .NET and Windows Desktop runtime | Self-contained win-x64 RID runtime from the pinned .NET 10 SDK/runtime packages | .NET license and runtime component notices, runtime libraries staged alongside both hosts |
| Roslyn | Microsoft.CodeAnalysis 4.14.0 | MIT, runtime compiler files and notices |
| FFmpeg.AutoGen | NuGet 7.1.1 | MIT binding, distinct from the native FFmpeg license |
| FFmpeg | 7.1.4 official release, detached upstream signature and pinned SHA-256 | LGPL-2.1-or-later, shared LGPL configuration and CLI tools, source/build in media source archive |
| libaom | MSYS2 UCRT64 aom 3.15.0-1 | BSD-2-Clause with upstream patent notice, shared DLL |
| libvpx | MSYS2 UCRT64 libvpx 1.17.0-1 | BSD-3-Clause with upstream patent notice, shared DLL |
| Opus | MSYS2 UCRT64 opus 1.6.1-2 | BSD-3-Clause, shared DLL |
| winpthreads | MSYS2 UCRT64 13.0.0.r560.g3197fc7d6-1 | Upstream winpthreads license text with binary, shared DLL and corresponding source package |
| SkiaSharp and native Skia | NuGet 3.119.1 win-x64 | MIT wrapper and native constituent notices |
| NAudio WinMM/Core and Silk.NET | Resolved package versions in the installed manifest | Package-supplied notices. Windows WinMM audio and Vulkan driver facilities are supplied by the OS or device vendor |
| Bundled typefaces | IBM Plex Mono, Archivo, Noto Serif Display | SIL OFL notices and shipped font files |
| OBS | Separately obtained OBS Studio, connected through OBS WebSocket | User-supplied optional application |
| Whisper model data | Pinned whisper.cpp conversion revision and digests from model catalog | MIT upstream model notices and an explicit optional download |
| x264, x265, Media Foundation and vendor encoders | Optional explicitly configured external FFmpeg and associated platform/vendor components | Their provider provenance and terms apply. The standard package excludes these external tools |

The shared FFmpeg build disables GPL, nonfree and version3 configuration and enables libaom, libvpx and libopus. The package retains the complete builder command in `licenses/media/FFMPEG-BUILD.txt`. Shared libraries can be replaced in the installation's `native` directory with ABI-compatible files. The release materials include corresponding source and build inputs for the shipped media stack. Runtime assembly inventories and source archives cover the exact staged candidate. [FFmpeg LGPL guidance](https://ffmpeg.org/legal.html)

Copyright terms, patent obligations and paid distribution are separate. Valencia's copyright holder can offer other terms for code they control. Third-party components remain governed by their respective terms. The named codecs, dynamic linking and provider boundaries do not establish patent clearance. This inventory is technical input for legal review. [FFmpeg legal considerations](https://ffmpeg.org/legal.html)
