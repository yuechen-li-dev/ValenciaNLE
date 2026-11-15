param([string]$CliPath = 'artifacts/publish/cli-win-x64/Valencia.Cli.exe')
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    $cli = (Resolve-Path $CliPath).Path
    $directory = Join-Path $repositoryRoot 'artifacts/local/m2a-demo'
    New-Item -ItemType Directory $directory -Force | Out-Null
    $source = Join-Path $directory 'source.mp4'
    $project = Join-Path $directory 'input.valencia'
    & ffmpeg -v error -y -f lavfi -i 'testsrc2=size=160x90:rate=30:duration=4' -f lavfi -i 'sine=frequency=440:sample_rate=48000:duration=4' -vf "drawtext=fontfile='C\:/Windows/Fonts/consola.ttf':text='%{n}':x=4:y=4:fontsize=16:fontcolor=white:box=1:boxcolor=black" -c:v libx264 -pix_fmt yuv420p -c:a aac $source
    if ($LASTEXITCODE -ne 0) { throw 'Fixture generation failed.' }
    & $cli project create $project --media $source | Set-Content (Join-Path $directory 'create.json')
    if ($LASTEXITCODE -ne 0) { throw 'Project creation failed.' }
    $originalHash = (Get-FileHash $project).Hash
    & $cli project info $project | Set-Content (Join-Path $directory 'info.json')
    if ($LASTEXITCODE -ne 0) { throw 'Project query failed.' }
    & $cli timeline list $project | Set-Content (Join-Path $directory 'timeline.json')
    if ($LASTEXITCODE -ne 0) { throw 'Timeline query failed.' }
    & $cli script run examples/rough-cut.ts --project $project | Set-Content (Join-Path $directory 'script.json')
    if ($LASTEXITCODE -ne 0) { throw 'Copeland execution failed.' }
    $script = Get-Content (Join-Path $directory 'script.json') -Raw | ConvertFrom-Json
    if (-not $script.success) { throw 'Script did not succeed.' }
    $preview = $script.returnValue | ConvertFrom-Json
    if (-not $preview.valid -or $preview.changes.Count -ne 4) { throw 'Unexpected dry-run diff.' }
    $reopened = "$project.edited.valencia"
    $movie = "$reopened.mp4"
    & $cli project info $reopened | Set-Content (Join-Path $directory 'reopened.json')
    if ($LASTEXITCODE -ne 0) { throw 'Reopen failed.' }

    # Equivalent exact operations through a non-executable typed obj.ts plan.
    $info = Get-Content (Join-Path $directory 'info.json') -Raw | ConvertFrom-Json
    $sequence = $info.project.document.sequences[0]
    $operations = @()
    foreach ($track in $sequence.tracks) {
        $right = [guid]::NewGuid().ToString()
        # Core's exact sequence clock is 254,016,000,000 ticks/second.
        $operations += [ordered]@{ operation='split'; clipId=$track.clips[0].id; at='508032000000'; rightClipId=$right }
        $operations += [ordered]@{ operation='move'; clipId=$right; trackId=$track.id; start='762048000000' }
    }
    $plan = [ordered]@{ schemaVersion=1; projectId=$info.project.document.id; projectRevision=$info.project.revision; sequenceId=$sequence.id; operations=$operations }
    $planFile = Join-Path $directory 'equivalent.obj.ts'
    ('const $value: EditPlan = ' + ($plan | ConvertTo-Json -Depth 12) + ';') | Set-Content $planFile
    & $cli plan validate $planFile --project $project | Set-Content (Join-Path $directory 'plan-preview.json')
    if ($LASTEXITCODE -ne 0) { throw 'Typed plan validation failed.' }
    & $cli plan apply $planFile --project $project --save-as (Join-Path $directory 'plan-edited.valencia') | Set-Content (Join-Path $directory 'plan-commit.json')
    if ($LASTEXITCODE -ne 0) { throw 'Typed plan commit failed.' }

    function Decode([string]$file, [string]$seconds, [bool]$audio) {
        $start = [Diagnostics.ProcessStartInfo]::new('ffmpeg')
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $arguments = @('-v','error','-ss',$seconds,'-i',$file)
        if ($audio) { $arguments += @('-t','0.2','-vn','-ac','1','-ar','48000','-f','f32le','pipe:1') }
        else { $arguments += @('-frames:v','1','-vf','scale=32:18:flags=area','-pix_fmt','rgb24','-f','rawvideo','pipe:1') }
        foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($start)
        $errors = $process.StandardError.ReadToEndAsync()
        $memory = [IO.MemoryStream]::new()
        try {
            $process.StandardOutput.BaseStream.CopyTo($memory)
            if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw 'Decode timed out.' }
            if ($process.ExitCode -ne 0) { throw $errors.GetAwaiter().GetResult() }
            return ,$memory.ToArray()
        } finally { $memory.Dispose(); $process.Dispose() }
    }
    function FrameError([byte[]]$left, [byte[]]$right) {
        if ($left.Length -ne $right.Length -or $left.Length -eq 0) { throw 'Frame sizes differ.' }
        $sum = 0.0
        for ($i = 0; $i -lt $left.Length; $i++) { $sum += [Math]::Abs([int]$left[$i] - [int]$right[$i]) }
        return $sum / $left.Length
    }
    $gap = Decode $movie '2.5' $false
    $gapMaximum = ($gap | Measure-Object -Maximum).Maximum
    $frameError = FrameError (Decode $movie '4' $false) (Decode $source '3' $false)
    $audio = Decode $movie '2.2' $true
    $sum = 0.0
    for ($i = 0; $i -lt $audio.Length; $i += 4) { $sample = [BitConverter]::ToSingle($audio,$i); $sum += $sample*$sample }
    $gapRms = [Math]::Sqrt($sum / ($audio.Length / 4))
    $probe = & ffprobe -v error -show_entries format=duration:stream=codec_name -of json $movie | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or [Math]::Abs([double]$probe.format.duration - 5) -gt .04) { throw 'Wrong export duration.' }
    if (-not ($probe.streams.codec_name -contains 'h264') -or -not ($probe.streams.codec_name -contains 'aac')) { throw 'Wrong export codecs.' }
    if ($gapMaximum -gt 2 -or $gapRms -gt .001 -or $frameError -gt 8) { throw 'Independent content verification failed.' }
    if ((Get-FileHash $project).Hash -ne $originalHash) { throw 'Original project was modified.' }
    & $cli schema export | Set-Content (Join-Path $directory 'schema.json')
    if ($LASTEXITCODE -ne 0) { throw 'Compiler schema extraction failed.' }
    [ordered]@{ outcome='passed'; decoder='independent FFmpeg CLI'; script=$script; probe=$probe; gapVideoMaximum=$gapMaximum; gapAudioRms=$gapRms; postCutFrameError=$frameError; originalProjectUnchanged=$true; output=$movie } | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $directory 'result.json')
    Get-Content (Join-Path $directory 'result.json')
} finally { Pop-Location }
