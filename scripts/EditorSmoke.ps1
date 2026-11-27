param(
    [string]$AppPath = 'src/Valencia.App/bin/Release/net10.0-windows/Valencia.App.exe',
    [string]$OutputRoot = 'artifacts/local/m1b-smoke'
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    $app = (Resolve-Path -LiteralPath $AppPath).Path
    $run = Join-Path $OutputRoot ([Guid]::NewGuid().ToString('N'))
    $fixtures = (New-Item -ItemType Directory (Join-Path $run 'fixtures') -Force).FullName
    $source = Join-Path $fixtures 'av.mp4'
    & ffmpeg -hide_banner -loglevel error -y -f lavfi -i 'testsrc2=size=640x360:rate=30:duration=6' -f lavfi -i 'sine=frequency=440:sample_rate=48000:duration=6' -vf "drawtext=fontfile='C\:/Windows/Fonts/consola.ttf':text='%{n}':x=10:y=10:fontsize=28:fontcolor=white:box=1:boxcolor=black" -c:v libx264 -pix_fmt yuv420p -g 60 -bf 2 -c:a aac $source
    if ($LASTEXITCODE -ne 0) { throw 'First fixture generation failed.' }
    & ffmpeg -hide_banner -loglevel error -y -f lavfi -i 'color=blue:size=640x360:rate=30:duration=2' -f lavfi -i 'sine=frequency=880:sample_rate=48000:duration=2' -c:v libx264 -pix_fmt yuv420p -c:a aac (Join-Path $fixtures 'second.mp4')
    if ($LASTEXITCODE -ne 0) { throw 'Second fixture generation failed.' }
    & $app --editor-smoke $source --output $run
    if ($LASTEXITCODE -ne 0) { throw "Native editor smoke failed. See $run." }
    Write-Output (Resolve-Path -LiteralPath $run).Path
}
finally { Pop-Location }
