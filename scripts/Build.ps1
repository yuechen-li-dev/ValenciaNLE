param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    & dotnet build Valencia.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Valencia build failed.' }
    if (-not $SkipTests) {
        foreach ($suite in @('Valencia.Core.Tests', 'Valencia.Persistence.Tests', 'Valencia.Tests', 'Valencia.Programming.Tests')) {
            & dotnet test "tests/$suite/$suite.csproj" -c Release --no-build --logger "trx;LogFileName=$suite.trx" --results-directory artifacts/local/tests
            if ($LASTEXITCODE -ne 0) { throw "$suite failed." }
        }
    }
    & dotnet publish src/Valencia.App/Valencia.App.csproj -c Release -r win-x64 --self-contained false -o artifacts/publish/win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Valencia publish failed.' }
    & dotnet publish src/Valencia.Cli/Valencia.Cli.csproj -c Release -r win-x64 --self-contained false -o artifacts/publish/cli-win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Valencia CLI publish failed.' }
    Write-Output 'Run artifacts/publish/win-x64/Valencia.App.exe with an optional local video path.'
}
finally {
    Pop-Location
}
