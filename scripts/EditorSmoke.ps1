param([string]$AppPath = 'artifacts/publish/win-x64/Valencia.App.exe')
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    New-Item -ItemType Directory artifacts/local/fixtures -Force | Out-Null
    & ffmpeg -hide_banner -loglevel error -y -f lavfi -i 'testsrc2=size=640x360:rate=30:duration=6' -f lavfi -i 'sine=frequency=440:sample_rate=48000:duration=6' -vf "drawtext=fontfile='C\:/Windows/Fonts/consola.ttf':text='%{n}':x=10:y=10:fontsize=28:fontcolor=white:box=1:boxcolor=black" -c:v libx264 -pix_fmt yuv420p -g 60 -bf 2 -c:a aac artifacts/local/fixtures/av.mp4
    if ($LASTEXITCODE -ne 0) { throw 'First fixture generation failed.' }
    & ffmpeg -hide_banner -loglevel error -y -f lavfi -i 'color=blue:size=640x360:rate=30:duration=2' -f lavfi -i 'sine=frequency=880:sample_rate=48000:duration=2' -c:v libx264 -pix_fmt yuv420p -c:a aac artifacts/local/fixtures/second.mp4
    if ($LASTEXITCODE -ne 0) { throw 'Second fixture generation failed.' }
    & $AppPath --editor-smoke artifacts/local/fixtures/av.mp4 --output artifacts/local/m1b-smoke
    if ($LASTEXITCODE -ne 0) { throw 'Native editor smoke failed or hardware was unavailable.' }
}
finally { Pop-Location }
