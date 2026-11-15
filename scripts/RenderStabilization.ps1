param([string]$HostDirectory = 'artifacts/local/render-stabilization/host',
    [string]$OutputDirectory = 'artifacts/local/render-stabilization/final',
    [int]$M0Trials = 10)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
Push-Location $repository
try {
    $app = (Resolve-Path (Join-Path $HostDirectory 'Valencia.App.exe')).Path
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $env:VALENCIA_MODEL_DIR = Join-Path $repository 'artifacts/local/m2c/models'
    $env:VALENCIA_PROXY_DIR = Join-Path $output 'fresh-proxies'
    Remove-Item Env:VALENCIA_OBS_ENDPOINT -ErrorAction SilentlyContinue
    $runs = [Collections.Generic.List[object]]::new()
    function RunCase([string]$name, [string[]]$arguments, [bool]$trace = $true) {
        $directory = Join-Path $output $name
        $env:VALENCIA_RENDER_TRACE = if ($trace) { Join-Path $directory 'trace.json' } else { $null }
        $commandArguments = $arguments + @('--output', $directory)
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $start = [Diagnostics.ProcessStartInfo]::new($app)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in $commandArguments) { $start.ArgumentList.Add($argument) }
        $native = [Diagnostics.Process]::Start($start)
        $stdout = $native.StandardOutput.ReadToEndAsync()
        $stderr = $native.StandardError.ReadToEndAsync()
        $timedOut = $false
        try {
            while (-not $native.WaitForExit(200)) {
                if ($timer.Elapsed.TotalSeconds -gt 200) {
                    $timedOut = $true
                    $native.Kill($true)
                    $native.WaitForExit()
                    break
                }
            }
            $stdout.GetAwaiter().GetResult() | Set-Content (Join-Path $output "$name.stdout.log")
            $stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $output "$name.stderr.log")
            $exitCode = if ($timedOut) { 124 } else { $native.ExitCode }
            $runs.Add([ordered]@{ name = $name; executable = $app; arguments = $commandArguments;
                exitCode = $exitCode; timedOut = $timedOut; elapsedSeconds = $timer.Elapsed.TotalSeconds; traced = $trace })
        } finally { $native.Dispose() }
        $runs | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'runs.json')
        Write-Output "$name exit $exitCode"
    }
    & ffmpeg -v error -y -i artifacts/local/player-fixtures/av.mp4 -an -c:v copy (Join-Path $output 'video-only.mp4')
    if ($LASTEXITCODE -ne 0) { throw 'Video-only remux failed.' }
    foreach ($mode in @('static','dynamic','video','av','changing-ui')) {
        $source = if ($mode -eq 'video') { Join-Path $output 'video-only.mp4' } else { 'artifacts/local/player-fixtures/av.mp4' }
        RunCase "comparison-$mode" @('--render-probe', $mode, '--source', $source)
    }
    for ($index = 0; $index -lt $M0Trials; $index++) {
        RunCase "m0-$index" @('--smoke','artifacts/local/player-fixtures/av.mkv') ($index % 2 -eq 0)
    }
    RunCase 'editor' @('--editor-smoke','artifacts/local/fixtures/av.mp4')
    RunCase 'creator' @('--creator-smoke','artifacts/local/m2d/three-hour.valencia.creator.valencia')
    RunCase 'qualification-1080' @('--qualification-smoke','artifacts/local/m2e/recorded-1080.valencia')
    RunCase 'qualification-1440' @('--qualification-smoke','artifacts/local/m2e/recorded-1440.valencia')
    $hashes = Get-ChildItem -LiteralPath (Split-Path $app) -Recurse -File |
        Where-Object { $_.Extension -in @('.dll','.exe','.json','.v.ts','.ttf') } |
        ForEach-Object { [ordered]@{ path = $_.FullName; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash } }
    $hashes | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'binary-manifest.json')
    if (@($runs | Where-Object exitCode -ne 0).Count -ne 0) { throw 'One or more native cases failed or were unavailable; inspect runs.json.' }
} finally {
    Pop-Location
}
