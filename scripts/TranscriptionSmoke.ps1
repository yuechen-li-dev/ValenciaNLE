param([switch]$Native)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
Push-Location $repository
try {
    $directory = Join-Path $repository 'artifacts/local/m2c'
    New-Item -ItemType Directory $directory -Force | Out-Null
    $env:VALENCIA_MODEL_DIR = Join-Path $directory 'models'
    $cli = Join-Path $repository 'artifacts/publish/cli-win-x64/Valencia.Cli.exe'
    function InvokeCli([string]$name, [string[]]$arguments) {
        $start = [Diagnostics.ProcessStartInfo]::new($cli)
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $process = [Diagnostics.Process]::Start($start)
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $peak = 0L
        try {
            while (-not $process.WaitForExit(10)) {
                try { $process.Refresh(); $peak = [Math]::Max($peak, $process.PeakWorkingSet64) } catch [InvalidOperationException] { }
                if ($timer.Elapsed.TotalSeconds -gt 120) { $process.Kill($true); throw 'CLI timed out.' }
            }
            $text = $stdout.GetAwaiter().GetResult(); $diagnostics = $stderr.GetAwaiter().GetResult()
            $text | Set-Content (Join-Path $directory "$name.json")
            $diagnostics | Set-Content (Join-Path $directory "$name.stderr.log")
            if ($process.ExitCode -ne 0) { throw "$name failed: $diagnostics" }
            return [pscustomobject]@{ result=($text | ConvertFrom-Json); wallMs=$timer.Elapsed.TotalMilliseconds; sampledPeakWorkingSetBytes=$peak }
        } finally { $process.Dispose() }
    }
    $wave = Join-Path $directory 'speech.wav'
    $voice = New-Object -ComObject SAPI.SpVoice
    $stream = New-Object -ComObject SAPI.SpFileStream
    try {
        $stream.Open($wave, 3)
        $voice.AudioOutputStream = $stream
        [void]$voice.Speak('Welcome to Valencia. The important announcement begins now. Keep this sentence for the final video. Thank you for listening.')
        $stream.Close()
    } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($stream); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($voice) }
    $source = Join-Path $directory 'recording.mp4'
    # Binary source-frame identity in the bottom eight cells survives independent MP4 decoding.
    $filter = "geq=lum='if(gte(Y,H-12),if(mod(floor(N/pow(2,floor(X/20))),2),235,16),lum(X,Y))':cb='if(gte(Y,H-6),128,cb(X,Y))':cr='if(gte(Y,H-6),128,cr(X,Y))'"
    & ffmpeg -v error -y -f lavfi -i 'testsrc2=size=160x90:rate=30' -i $wave -vf $filter -shortest -c:v libx264 -pix_fmt yuv420p -c:a aac $source
    if ($LASTEXITCODE -ne 0) { throw 'Speech/video fixture generation failed.' }
    $sourceHash = (Get-FileHash $source).Hash
    $project = Join-Path $directory 'recording.valencia'
    $created = InvokeCli 'create' @('project','create',$project,'--media',$source)
    $originalHash = (Get-FileHash $project).Hash
    $asset = $created.result.document.assets[0].id
    $sequence = $created.result.document.sequences[0].id
    $cold = InvokeCli 'transcript-cold' @('transcribe',$project,'--asset',$asset,'--stream','1')
    $warm = InvokeCli 'transcript-warm' @('transcribe',$project,'--asset',$asset,'--stream','1')
    if ($cold.result.job.cacheHit -or -not $warm.result.job.cacheHit) { throw 'Expected cold miss followed by hit.' }
    $key = $cold.result.job.key
    $search = InvokeCli 'search' @('transcript','search',$project,'--analysis',$key,'--query','important announcement')
    if ($search.result.Count -ne 1) { throw 'Expected one phrase match.' }
    $plan = Join-Path $directory 'text-plan.json'
    $proposal = InvokeCli 'text-proposal' @('textedit','plan',$project,'--analysis',$key,'--query','important announcement','--mode','retain','--output',$plan)
    if ((Get-FileHash $project).Hash -ne $originalHash) { throw 'Dry-run modified input project.' }
    $saved = Join-Path $directory 'text-edited.valencia'
    $applied = InvokeCli 'text-applied' @('textedit','apply',$project,'--plan',$plan,'--save-as',$saved)
    if (-not $applied.result.committed) { throw 'Text edit did not commit.' }
    $reopened = InvokeCli 'text-reopened' @('project','info',$saved)
    $movie = Join-Path $directory 'text-edited.mp4'
    $exported = InvokeCli 'text-export' @('export',$saved,'--sequence',$sequence,'--output',$movie)
    $script = InvokeCli 'copeland-text' @('script','run',(Join-Path $repository 'examples/text-edit.ts'),'--project',$project)
    if (-not $script.result.success) { throw 'Copeland execution failed.' }
    $clip = ($reopened.result.project.document.sequences[0].tracks | Where-Object kind -eq video).clips[0]
    $sourceStart = [double]$clip.sourceIn.timestamp.value * $clip.sourceIn.timestamp.numerator / $clip.sourceIn.timestamp.denominator + [double]$clip.sourceIn.offset.ticks / 254016000000.0
    function Decode([string]$file, [double]$time, [bool]$audio) {
        $start = [Diagnostics.ProcessStartInfo]::new('ffmpeg')
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        $args = @('-v','error','-ss',$time.ToString('R',[Globalization.CultureInfo]::InvariantCulture),'-i',$file)
        if ($audio) { $args += @('-t','0.2','-vn','-ac','1','-ar','48000','-f','f32le','pipe:1') }
        else { $args += @('-frames:v','1','-vf','scale=160:90:flags=area','-pix_fmt','rgb24','-f','rawvideo','pipe:1') }
        foreach ($argument in $args) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($start)
        $errors = $process.StandardError.ReadToEndAsync(); $memory = [IO.MemoryStream]::new()
        try {
            $process.StandardOutput.BaseStream.CopyTo($memory)
            if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw 'Decode timed out.' }
            if ($process.ExitCode -ne 0) { throw $errors.GetAwaiter().GetResult() }
            return ,$memory.ToArray()
        } finally { $memory.Dispose(); $process.Dispose() }
    }
    function FrameCode([byte[]]$pixels) {
        if ($pixels.Length -ne 160*90*3) { throw 'Wrong decoded image size.' }
        $code = 0
        for ($bit=0; $bit -lt 8; $bit++) { if ($pixels[(84*160+$bit*20+10)*3] -ge 128) { $code = $code -bor (1 -shl $bit) } }
        return $code
    }
    $checks = foreach ($time in @(0.1,0.4,0.8)) {
        $a = Decode $movie $time $false; $b = Decode $source ($sourceStart+$time) $false
        $code = FrameCode $a; $expected = FrameCode $b
        if ($code -ne $expected) { throw "Source frame mismatch $code != $expected" }
        @{ outputSeconds=$time; sourceSeconds=$sourceStart+$time; frameId=$code; expectedFrameId=$expected }
    }
    $audio = Decode $movie 0.4 $true; $reference = Decode $source ($sourceStart+0.4) $true
    $bestError = [double]::MaxValue; $bestLag = 0
    for ($lag=-48; $lag -le 48; $lag++) {
        $sum = 0.0
        for ($sample=48; $sample -lt $audio.Length/4-48; $sample++) {
            $sum += [Math]::Abs([BitConverter]::ToSingle($audio,$sample*4)-[BitConverter]::ToSingle($reference,($sample+$lag)*4))
        }
        $error = $sum/($audio.Length/4-96)
        if ($error -lt $bestError) { $bestError=$error; $bestLag=$lag }
    }
    if ($bestError -gt .04) { throw 'Export audio differs from selected source range.' }
    $probe = & ffprobe -v error -show_entries format=duration:stream=codec_name,width,height,r_frame_rate,sample_rate -of json $movie | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not ($probe.streams.codec_name -contains 'h264') -or -not ($probe.streams.codec_name -contains 'aac')) { throw 'MP4 codec validation failed.' }
    if ((Get-FileHash $source).Hash -ne $sourceHash -or (Get-FileHash $project).Hash -ne $originalHash) { throw 'Input changed.' }
    if ($Native) {
        & artifacts/publish/win-x64/Valencia.App.exe --transcript-smoke $source --output artifacts/local/m2c-native
        if ($LASTEXITCODE -ne 0) { throw 'Native transcript smoke failed.' }
    }
    [ordered]@{ outcome='passed'; sourceSha256=$sourceHash; sourceUnchanged=$true; projectUnchanged=$true; cold=$cold; warm=$warm;
        sourceStartSeconds=$sourceStart; verifiedFrames=@($checks); audioMae=$bestError; audioOffsetSamples=$bestLag; audioOffsetMs=$bestLag/48.0;
        independentProbe=$probe; copeland=$script; proposalMs=$proposal.wallMs; output=$movie
    } | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $directory 'result.json')
    Write-Output (Join-Path $directory 'result.json')
} finally { Pop-Location }
