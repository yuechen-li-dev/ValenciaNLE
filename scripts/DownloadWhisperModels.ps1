param([ValidateSet('tiny.en','large-v3')][string[]]$Model = @('tiny.en'))
$ErrorActionPreference = 'Stop'
$root = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/local/m2c/models'
$revision = '5359861c739e955e79d9a303bcbc70fb988958b1'
$hashes = @{
    'tiny.en' = '921E4CF8686FDD993DCD081A5DA5B6C365BFDE1162E72B08D75AC75289920B1F'
    'large-v3' = '64D182B440B98D5203C4F9BD541544D84C605196C4F7B845DFA11FB23594D1E2'
}
New-Item -ItemType Directory $root -Force | Out-Null
foreach ($id in $Model) {
    $destination = Join-Path $root "ggml-$id.bin"
    if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination).Hash -eq $hashes[$id]) {
        Write-Output "Verified existing $id"
        continue
    }
    $temporary = "$destination.partial"
    try {
        & curl.exe --fail --location --retry 2 --output $temporary "https://huggingface.co/ggerganov/whisper.cpp/resolve/$revision/ggml-$id.bin"
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $id" }
        if ((Get-FileHash -LiteralPath $temporary).Hash -ne $hashes[$id]) { throw "SHA-256 mismatch: $id" }
        Move-Item -LiteralPath $temporary -Destination $destination -Force
        Write-Output "Verified $destination"
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
}
Write-Output "Set VALENCIA_MODEL_DIR=$root. Models are not bundled in the application."
