[CmdletBinding()]
param(
    [string]$Output = 'artifacts/local/m3b/release',
    [string]$Vendor = 'artifacts/local/m3b/vendor',
    [string]$InnoCompiler,
    [ValidatePattern('^0\.3\.0-rc\.[0-9]+$')][string]$Version = '0.3.0-rc.1',
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $root = [IO.Path]::GetFullPath($Output)
    if (Test-Path $root) { throw "Choose a new distribution output directory: $root" }
    $vendorPath = (Resolve-Path $Vendor).Path
    if (-not $InnoCompiler) { $InnoCompiler = Join-Path $vendorPath 'InnoSetup/ISCC.exe' }
    if (-not $SkipInstaller -and -not (Test-Path $InnoCompiler)) { throw 'Provide the Inno Setup 6.7.3 compiler using -InnoCompiler.' }

    $stage = (New-Item -ItemType Directory -Path (Join-Path $root 'portable')).FullName
    $cli = (New-Item -ItemType Directory -Path (Join-Path $root 'cli-stage')).FullName
    $flags = @('-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=false',
               '-p:DebugType=None','-p:DebugSymbols=false',"-p:Version=$Version")
    & dotnet publish src/Valencia.App/Valencia.App.csproj @flags -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained desktop publish failed.' }
    & dotnet publish src/Valencia.Cli/Valencia.Cli.csproj @flags -p:ValenciaDesktopDistribution=true -o $cli
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained CLI publish failed.' }
    foreach ($file in Get-ChildItem -LiteralPath $cli -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($cli, $file.FullName)
        $target = Join-Path $stage $relative
        if (Test-Path $target) {
            if ((Get-FileHash $target).Hash -ne (Get-FileHash $file.FullName).Hash) {
                throw "Desktop and CLI publish disagree on shared file $relative."
            }
        }
        else {
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }

    $native = (New-Item -ItemType Directory -Path (Join-Path $stage 'native')).FullName
    $ffmpegBin = Join-Path $vendorPath 'ffmpeg-install/bin'
    foreach ($file in 'avcodec-61.dll','avdevice-61.dll','avfilter-10.dll','avformat-61.dll','avutil-59.dll',
                      'swresample-5.dll','swscale-8.dll','ffmpeg.exe','ffprobe.exe') {
        Copy-Item -LiteralPath (Join-Path $ffmpegBin $file) -Destination $native
    }
    foreach ($file in 'libaom.dll','libvpx-1.dll','libopus-0.dll','libwinpthread-1.dll') {
        Copy-Item -LiteralPath (Join-Path $vendorPath "sysroot/ucrt64/bin/$file") -Destination $native
    }
    $pinnedPthread = (Get-FileHash (Join-Path $native 'libwinpthread-1.dll')).Hash
    if ($pinnedPthread -ne '2B3136C1D085FDB84E0B55C7A7E004672F35D576E4EFF7FB2BEF0B5899A781B7') { throw 'Unexpected winpthreads binary.' }
    $ffmpeg = Join-Path $native 'ffmpeg.exe'
    $versionLog = & $ffmpeg -version
    if ($LASTEXITCODE -ne 0) { throw 'The staged FFmpeg executable cannot start.' }
    $configuration = $versionLog | Where-Object { $_ -like 'configuration:*' }
    if ($versionLog[0] -notmatch '^ffmpeg version 7\.1\.4 ' -or
        $configuration -notmatch '--enable-shared' -or
        $configuration -match '--enable-(gpl|nonfree|libx264|libx265)' -or
        $configuration -notmatch '--disable-gpl' -or $configuration -notmatch '--disable-nonfree') {
        throw 'Staged FFmpeg failed the LGPL shared configuration gate.'
    }
    $encoders = & $ffmpeg -hide_banner -encoders
    foreach ($encoder in 'libaom-av1','libvpx-vp9','libopus') {
        if (-not ($encoders -match ('\s'+[regex]::Escape($encoder)+'\s'))) { throw "Staged FFmpeg lacks $encoder." }
    }
    if ($encoders -match '\slibx26[45]\s') { throw 'The standard distribution contains a forbidden compatibility encoder.' }

    $licenseRoot = (New-Item -ItemType Directory (Join-Path $stage 'licenses/media')).FullName
    foreach ($name in 'COPYING.LGPLv2.1','LICENSE.md','CREDITS') {
        Copy-Item -LiteralPath (Join-Path $vendorPath "ffmpeg-7.1.4/$name") -Destination $licenseRoot
    }
    foreach ($name in 'aom','libvpx','opus','libwinpthread') {
        $source = Join-Path $vendorPath "sysroot/ucrt64/share/licenses/$name"
        if ($name -eq 'libvpx' -and -not (Test-Path $source)) {
            $notices = (New-Item -ItemType Directory -Path (Join-Path $root 'vpx-notices')).FullName
            & tar.exe -xf (Join-Path $vendorPath 'downloads/mingw-w64-libvpx-1.17.0-1.src.tar.zst') -C $notices 'mingw-w64-libvpx/libvpx-1.17.0.tar.gz'
            if ($LASTEXITCODE -ne 0) { throw 'Could not extract the pinned libvpx source archive.' }
            & tar.exe -xf (Join-Path $notices 'mingw-w64-libvpx/libvpx-1.17.0.tar.gz') -C $notices 'libvpx-1.17.0/LICENSE' 'libvpx-1.17.0/PATENTS' 'libvpx-1.17.0/AUTHORS'
            if ($LASTEXITCODE -ne 0) { throw 'Could not extract libvpx license notices.' }
            $source = Join-Path $notices 'libvpx-1.17.0'
        }
        if (-not (Test-Path $source)) { throw "The pinned media package is missing its $name notices." }
        Copy-Item -LiteralPath $source -Destination (Join-Path $licenseRoot $name) -Recurse
    }
    $versionLog | Set-Content -LiteralPath (Join-Path $licenseRoot 'FFMPEG-BUILD.txt')
    $encoders | Set-Content -LiteralPath (Join-Path $licenseRoot 'FFMPEG-ENCODERS.txt')
    foreach ($doc in 'DISTRIBUTION.md','THIRD_PARTY_COMPONENTS.md','EXPORT_PROFILES.md','M3B.md','M3A_AGENT_GUIDE.md') {
        Copy-Item -LiteralPath (Join-Path $repo "docs/$doc") -Destination (Join-Path $stage 'docs')
    }
    Copy-Item -LiteralPath (Join-Path $repo 'examples/agent-time.ts') -Destination $stage
    $sources = [ordered]@{
        Valencia = (& git rev-parse HEAD).Trim()
        ValenciaWorkingTree = "Exact checkout source is provided in the matching source archive."
        Copeland = (& git -C ../Copeland rev-parse HEAD).Trim()
        InputMan = (& git -C ../InputMan rev-parse HEAD).Trim()
        Deliverance = (& git -C ../Deliverance rev-parse HEAD).Trim()
    }
    [ordered]@{ version=$Version; rid='win-x64'; selfContained=$true; sdk=(& dotnet --version);
        signed=$false; source=$sources; ffmpeg='7.1.4'; sourceArchive="Valencia-$Version-sources.zip";
        mediaSourceArchive="Valencia-$Version-media-sources.zip" } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'valencia-release.json')

    $dependencyFiles = @('Valencia.App.deps.json','Valencia.Cli.deps.json')
    $packages = foreach ($dependencyFile in $dependencyFiles) {
        (Get-Content -LiteralPath (Join-Path $stage $dependencyFile) -Raw | ConvertFrom-Json -AsHashtable).libraries.Keys
    }
    $fileManifest = Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
        [ordered]@{ path=([IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/')); bytes=$_.Length; sha256=(Get-FileHash $_.FullName).Hash }
    }
    [ordered]@{ version=1; packages=@($packages | Sort-Object -Unique); files=@($fileManifest) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'docs/installed-components.json')

    $sourceStage = (New-Item -ItemType Directory (Join-Path $root 'source-stage')).FullName
    $checkout = (New-Item -ItemType Directory (Join-Path $sourceStage 'ValenciaNLE')).FullName
    foreach ($relative in (& git -c core.quotepath=false ls-files --cached --others --exclude-standard | Select-Object -Unique)) {
        $file = Join-Path $repo $relative
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
        $target = Join-Path $checkout $relative
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item -LiteralPath $file -Destination $target
    }
    foreach ($name in 'Copeland','InputMan','Deliverance') {
        & git -C "../$name" archive --format=zip "--prefix=$name/" "--output=$(Join-Path $sourceStage "$name.zip")" $sources[$name]
        if ($LASTEXITCODE -ne 0) { throw "Could not archive $name." }
    }

    $mediaSource = (New-Item -ItemType Directory (Join-Path $root 'media-source-stage')).FullName
    Get-ChildItem -LiteralPath (Join-Path $vendorPath 'downloads') -File |
        Where-Object { $_.Name -like '*.src.tar.zst' -or $_.Name -like 'ffmpeg-7.1.4.tar.xz*' -or $_.Name -eq 'ffmpeg-devel.asc' } |
        Copy-Item -Destination $mediaSource
    Copy-Item -LiteralPath (Join-Path $repo 'scripts/Build-Ffmpeg.ps1') -Destination $mediaSource
    Copy-Item -LiteralPath $licenseRoot -Destination (Join-Path $mediaSource 'licenses') -Recurse

    foreach ($archive in @(
        @{Name="Valencia-$Version-win-x64-portable.zip";Source=$stage},
        @{Name="Valencia-$Version-sources.zip";Source=$sourceStage},
        @{Name="Valencia-$Version-media-sources.zip";Source=$mediaSource}
    )) {
        & tar.exe -a -cf (Join-Path $root $archive.Name) -C $archive.Source .
        if ($LASTEXITCODE -ne 0) { throw "Archive failed: $($archive.Name)" }
    }
    if (-not $SkipInstaller) {
        $sequence = $Version.Split('.')[-1]
        & $InnoCompiler "/DStageDir=$stage" "/DOutputDir=$root" "/DValenciaVersion=$Version" "/DFileVersion=0.3.0.$sequence" (Join-Path $repo 'distribution/Valencia.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Inno Setup packaging failed.' }
    }
    $artifacts = Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Extension -in '.zip','.exe' }
    $artifacts | ForEach-Object { (Get-FileHash $_.FullName).Hash.ToLowerInvariant() + '  ' + $_.Name } |
        Set-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt')
    $installed = Get-ChildItem -LiteralPath $stage -Recurse -File | Measure-Object Length -Sum
    [ordered]@{ version=$Version; installedFiles=$installed.Count; installedBytes=[long]$installed.Sum;
        archives=@($artifacts | ForEach-Object { @{ name=$_.Name; bytes=$_.Length; sha256=(Get-FileHash $_.FullName).Hash } }) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'package-evidence.json')
    Write-Output $root
}
finally { Pop-Location }
