# Valencia Windows x64 distribution

The 0.3.0-rc.3 bundle contains the desktop, CLI, stdio MCP entry point, .NET runtime, precompiled shaders, fonts and shared FFmpeg with D3D11VA decoding. Windows supplies D3D11 and audio, and the GPU vendor supplies the Vulkan and video decoder drivers. The app stores recovery, analysis, durable audio seek anchors, proxies, the render queue and managed models below `%LOCALAPPDATA%\Valencia`. Set `VALENCIA_DATA_DIR` before launch to choose an alternate data directory. Shortcuts and OBS credentials use their existing user settings locations.

## Build

Build on Windows x64 with the SDK in `global.json`, sibling Copeland, InputMan and Deliverance checkouts, MSYS2 UCRT64 GCC 15.2.0, Git for Windows GnuPG, and the Inno Setup 6.7.3 compiler. The FFmpeg builder verifies the upstream signature and all pinned inputs. The publisher checks the staged codec configuration, resolves the managed closure, verifies shared files agree across the two host publications, and emits portable, installer and matching source archives.

```powershell
./scripts/Build-Ffmpeg.ps1
./scripts/Publish-M3b.ps1 -InnoCompiler 'C:/Tools/InnoSetup/ISCC.exe'
```

Choose a new `-Output` directory for each publish. The result includes `SHA256SUMS.txt`, a component file manifest and package evidence. Extract the source archive and the three nested sibling archives into the same parent directory to rebuild from corresponding source. The media source archive includes the FFmpeg tarball and signature, build script and pinned source packages for its redistributed codec and runtime dependencies.

## Install, run and upgrade

Verify the download against `SHA256SUMS.txt`. Run the per-user setup and start Valencia from the Start menu. The portable ZIP uses the same executable and data locations. From either form run `Valencia.Cli.exe doctor` for device, codec, cache and model checks or use the desktop diagnostics action.

Run the new setup over the same install location to upgrade. Setup and uninstall manage program files and shortcuts. Project, model, export-queue and recovery data live in user directories. The local candidate is unsigned. Version checking is explicit through `Valencia.Cli.exe update check`, which queries the public stable release metadata and returns the release page. Installation remains a manual verified download. Store delivery requires a separate release and signing process.

For optional speech use `Valencia.Cli.exe models list` and `Valencia.Cli.exe models download tiny.en`. The managed download is 77,704,715 bytes. The catalog retains the 3,095,033,483-byte large-v3 identity for compatibility. The current transcription backend accepts tiny.en English. An existing verified model may be set with an absolute `VALENCIA_MODEL_DIR`.

`Valencia.Cli.exe creator export <project> --output-dir <directory>` uses VP9 and Opus in the Windows release. Select another available provider with `--encoder av1` or `--encoder h264`. Compatibility providers require an explicit FFmpeg path as documented below.

See [third-party components](THIRD_PARTY_COMPONENTS.md) and [export profiles](EXPORT_PROFILES.md).
