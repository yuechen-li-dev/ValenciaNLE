param([string]$AppPath = 'src/Valencia.App/bin/Release/net10.0-windows/Valencia.App.exe')
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    # Keep player fixtures separate from EditorSmoke's numbered source media.
    New-Item -ItemType Directory -Path artifacts/local/player-fixtures -Force | Out-Null
    & ffmpeg -hide_banner -loglevel error -y -f lavfi -i 'testsrc2=size=640x360:rate=30:duration=6' -f lavfi -i 'sine=frequency=440:sample_rate=48000:duration=6' -c:v libx264 -pix_fmt yuv420p -g 60 -bf 2 -c:a aac -b:a 128k -shortest artifacts/local/player-fixtures/av.mp4
    if ($LASTEXITCODE -ne 0) { throw 'MP4 fixture generation failed.' }
    & ffmpeg -hide_banner -loglevel error -y -i artifacts/local/player-fixtures/av.mp4 -c copy artifacts/local/player-fixtures/av.mkv
    if ($LASTEXITCODE -ne 0) { throw 'MKV fixture generation failed.' }
    foreach ($container in @('mp4', 'mkv')) {
        & $AppPath --smoke "artifacts/local/player-fixtures/av.$container" --output "artifacts/local/smoke-$container"
        if ($LASTEXITCODE -ne 0) { throw "$container hardware smoke failed or hardware was unavailable." }
    }
}
finally {
    Pop-Location
}
