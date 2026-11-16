param(
    [string]$CliPath = 'src/Valencia.Cli/bin/Release/net11.0/Valencia.Cli.exe',
    [int]$RecordingSeconds = 10800,
    [int]$TargetSeconds = 600,
    [switch]$ReuseFixture,
    [switch]$Proxy
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
Push-Location $repository
try {
    $directory = Join-Path $repository 'artifacts/local/m2d'
    New-Item -ItemType Directory $directory -Force | Out-Null
    $env:VALENCIA_MODEL_DIR = Join-Path $repository 'artifacts/local/m2c/models'
    $cli = (Resolve-Path $CliPath).Path
    function InvokeCli([string]$name, [string[]]$arguments) {
        $start = [Diagnostics.ProcessStartInfo]::new($cli)
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $process = [Diagnostics.Process]::Start($start)
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        $peak=0L
        try {
            while (-not $process.WaitForExit(100)) {
                try { $process.Refresh(); $peak=[Math]::Max($peak,$process.PeakWorkingSet64) } catch [InvalidOperationException] { }
                if ($timer.Elapsed.TotalSeconds -gt 1800) { $process.Kill($true); throw 'Creator workflow exceeded its bounded 30-minute smoke budget.' }
            }
            $text=$stdout.GetAwaiter().GetResult(); $diagnostics=$stderr.GetAwaiter().GetResult()
            $text | Set-Content (Join-Path $directory "$name.json")
            $diagnostics | Set-Content (Join-Path $directory "$name.stderr.log")
            if ($process.ExitCode -ne 0) { throw "$name failed: $diagnostics" }
            return [pscustomobject]@{ result=($text|ConvertFrom-Json); wallSeconds=$timer.Elapsed.TotalSeconds; sampledPeakWorkingSetBytes=$peak; cpuSeconds=$process.TotalProcessorTime.TotalSeconds }
        } finally { $process.Dispose() }
    }
    $source = Join-Path $directory 'three-hour.mkv'
    $project = Join-Path $directory 'three-hour.valencia'
    if (-not $ReuseFixture) {
        $speech = Join-Path $directory 'speech.wav'
        $voice = New-Object -ComObject SAPI.SpVoice
        $stream = New-Object -ComObject SAPI.SpFileStream
        $voiceName = $voice.Voice.GetDescription()
        try {
            $stream.Open($speech,3); $voice.AudioOutputStream=$stream
            [void]$voice.Speak('Welcome to Valencia. The important announcement begins now. Keep this sentence for the final video. Thank you for listening.')
            $stream.Close()
        } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($stream); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($voice) }
        $event = Join-Path $directory 'event.flac'
        & ffmpeg -v error -y -stream_loop -1 -i $speech -f lavfi -i 'anullsrc=r=48000:cl=mono' -filter_complex '[0:a]atrim=duration=60,asetpts=PTS-STARTPTS,aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=mono[t];[1:a]atrim=duration=540,asetpts=PTS-STARTPTS[s];[t][s]concat=n=2:v=0:a=1[a]' -map '[a]' -t 600 -c:a flac $event
        if ($LASTEXITCODE -ne 0) { throw 'Speech event fixture failed.' }
        $filter = "drawtext=fontfile='C\:/Windows/Fonts/consola.ttf':text='%{n}':x=3:y=14:fontsize=12:fontcolor=white:box=1:boxcolor=black," +
            "geq=lum='if(lt(Y,8),if(mod(floor(N/pow(2,floor(X/10))),2),235,16),lum(X,Y))':cb='if(lt(Y,8),128,cb(X,Y))':cr='if(lt(Y,8),128,cr(X,Y))'"
        $generation=[Diagnostics.Stopwatch]::StartNew()
        & ffmpeg -v error -y -f lavfi -i "testsrc2=size=160x90:rate=30:duration=$RecordingSeconds" -stream_loop -1 -i $event -f lavfi -i "sine=frequency=330:sample_rate=48000:duration=$RecordingSeconds" -t $RecordingSeconds -map 0:v -map 1:a -map 2:a -vf $filter -c:v libx264 -preset veryfast -g 300 -bf 3 -pix_fmt yuv420p -c:a aac -b:a 64k $source
        if ($LASTEXITCODE -ne 0) { throw 'Three-hour recording generation failed.' }
        [ordered]@{ provenance='Generated synthetic qualification fixture; no real OBS footage or editorial-quality claim'; durationSeconds=$RecordingSeconds;
            source=$source; sourceSha256=(Get-FileHash $source).Hash; voice=$voiceName; video='H.264 160x90/30, long 300-frame GOP, B-frames, visible frame number and 16-bit frame-ID stripe';
            audio='Two independent mono 48kHz AAC streams: sixty seconds repeated speech every ten minutes, and continuous 330Hz game/music signal';
            generationSeconds=$generation.Elapsed.TotalSeconds
        } | ConvertTo-Json | Set-Content (Join-Path $directory 'fixture.json')
        $created=InvokeCli 'create' @('project','create',$project,'--media',$source)
    } else {
        if (-not (Test-Path $source) -or -not (Test-Path $project)) { throw 'Reuse requires the existing generated source and unedited input project.' }
        $created=InvokeCli 'inspect-input' @('project','info',$project)
        $created.result=$created.result.project
    }
    $asset=$created.result.document.assets[0].id
    $sourceHash=(Get-FileHash $source).Hash; $projectHash=(Get-FileHash $project).Hash
    $arguments=@('creator','run',$project,'--asset',$asset,'--stream','1','--target-seconds',"$TargetSeconds",'--short-seconds','20',
        '--width','320','--height','180','--keywords','important announcement','--output-dir',(Join-Path $directory 'outputs'),'--commit','--verify-history')
    if ($Proxy) { $arguments += '--proxy' }
    $workflow=InvokeCli 'workflow' $arguments
    if (-not $workflow.result.committed -or $workflow.result.exports.Count -ne 4) { throw 'Expected an atomic committed long-form and three Shorts.' }
    if ([Math]::Abs([double]$workflow.result.preview.estimatedDuration.ticks/254016000000.0-$TargetSeconds) -gt 1) { throw 'Creator target duration was not achieved within the one-second fixture tolerance.' }
    if ((Get-FileHash $source).Hash -ne $sourceHash -or (Get-FileHash $project).Hash -ne $projectHash) { throw 'Original recording or input project changed.' }
    [ordered]@{ outcome='pipeline passed; independent output verification is a separate required step'; sourceUnchanged=$true; projectUnchanged=$true; workflow=$workflow;
        fixture=(Get-Content (Join-Path $directory 'fixture.json') -Raw | ConvertFrom-Json)
    } | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $directory 'result.json')
    Write-Output (Join-Path $directory 'result.json')
} finally { Pop-Location }
