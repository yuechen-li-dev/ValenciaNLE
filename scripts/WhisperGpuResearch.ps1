param([int]$Trials = 5)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
Push-Location $repository
try {
    $env:VALENCIA_MODEL_DIR = Join-Path $repository 'artifacts/local/m2c/models'
    $audio = 'artifacts/local/m2c/speech.wav'
    if (-not (Test-Path $audio)) { throw 'Run scripts/TranscriptionSmoke.ps1 first to generate the speech fixture.' }
    & dotnet build research/Valencia.WhisperGpu -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Research build failed.' }
    foreach ($configuration in @(@('tiny.en','32','gpu-tiny'), @('large-v3','32','gpu-large'), @('tiny.en','1','gpu-single-row'))) {
        & dotnet run --project research/Valencia.WhisperGpu -c Release --no-build -- $configuration[0] $audio "artifacts/local/m2c/$($configuration[2]).json" $configuration[1] $Trials
        if ($LASTEXITCODE -ne 0) { throw 'GPU qualification failed.' }
    }
} finally { Pop-Location }
