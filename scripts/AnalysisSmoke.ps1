param([string]$CliPath = 'artifacts/publish/cli-win-x64/Valencia.Cli.exe', [switch]$Native)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
Push-Location $repository
try {
    $cli = (Resolve-Path $CliPath).Path
    $directory = Join-Path $repository 'artifacts/local/m2b-demo'
    New-Item -ItemType Directory $directory -Force | Out-Null
    function InvokeCli([string]$name, [string[]]$arguments) {
        $start = [Diagnostics.ProcessStartInfo]::new($cli)
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $process = [Diagnostics.Process]::Start($start)
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $peak = 0L
        try {
            while (-not $process.WaitForExit(10)) {
                $process.Refresh()
                try { $peak = [Math]::Max($peak, $process.PeakWorkingSet64) } catch [InvalidOperationException] { }
                if ($timer.Elapsed.TotalSeconds -gt 120) { $process.Kill($true); throw 'CLI smoke timed out.' }
            }
            $text = $stdout.GetAwaiter().GetResult()
            $diagnostics = $stderr.GetAwaiter().GetResult()
            $text | Set-Content (Join-Path $directory "$name.json")
            $diagnostics | Set-Content (Join-Path $directory "$name.stderr.log")
            if ($process.ExitCode -ne 0) { throw "$name failed: $diagnostics" }
            return [pscustomobject]@{ result=($text | ConvertFrom-Json); elapsedMs=$timer.Elapsed.TotalMilliseconds; sampledPeakWorkingSetBytes=$peak }
        } finally { $process.Dispose() }
    }
    $source = Join-Path $directory 'recording.mkv'
    $project = Join-Path $directory 'recording.valencia'
    $videoFilter = "drawbox=x=0:y=0:w=iw:h=ih:color=blue:t=fill:enable='gte(t,3)',drawtext=fontfile='C\:/Windows/Fonts/consola.ttf':text='%{n}':x=4:y=4:fontsize=16:fontcolor=white:box=1:boxcolor=black," +
        "geq=lum='if(gte(Y,H-12),if(mod(floor(N/pow(2,floor(X/20))),2),235,16),lum(X,Y))':cb='if(gte(Y,H-6),128,cb(X,Y))':cr='if(gte(Y,H-6),128,cr(X,Y))'"
    & ffmpeg -v error -y -f lavfi -i 'testsrc2=size=160x90:rate=30:duration=6' -f lavfi -i "aevalsrc=exprs='if(gte(t,2)*lt(t,4),0,0.5*sin(2*PI*440*t))|-if(gte(t,2)*lt(t,4),0,0.5*sin(2*PI*440*t))':s=48000:d=6" -f lavfi -i 'sine=frequency=880:sample_rate=48000:duration=6' -map 0:v -map 1:a -map 2:a -vf $videoFilter -c:v libx264 -pix_fmt yuv420p -c:a pcm_s16le $source
    if ($LASTEXITCODE -ne 0) { throw 'Recording fixture failed.' }
    $sourceHash = (Get-FileHash $source).Hash
    $created = InvokeCli 'create' @('project','create',$project,'--media',$source)
    $asset = $created.result.document.assets[0].id
    $sequence = $created.result.document.sequences[0].id
    $originalProjectHash = (Get-FileHash $project).Hash
    $cold = InvokeCli 'audio-cold' @('analyze','silence',$project,'--asset',$asset,'--stream','1')
    $warm = InvokeCli 'audio-warm' @('analyze','waveform',$project,'--asset',$asset,'--stream','1')
    if ($cold.result.job.cacheHit -or -not $warm.result.job.cacheHit -or $cold.result.result.silenceCount -ne 1) { throw 'Audio analysis/cache result incorrect.' }
    $scenes = InvokeCli 'scenes' @('analyze','scenes',$project,'--asset',$asset,'--stream','0')
    if ($scenes.result.result.sceneCount -lt 1) { throw 'Known visual cut was not detected.' }
    $key = $cold.result.job.key
    $policy = Join-Path $directory 'policy.obj.ts'
    'const $value: RoughCutPolicy = { minimumSegmentMs: 100, minimumRemovalMs: 500, prePaddingMs: 0, postPaddingMs: 0 };' | Set-Content $policy
    $planFile = Join-Path $directory 'roughcut.json'
    $proposed = InvokeCli 'proposal' @('roughcut','plan',$project,'--analysis',$key,'--policy',$policy,'--output',$planFile)
    if (-not $proposed.result.valid) { throw 'Rough cut failed validation.' }
    if ((Get-FileHash $project).Hash -ne $originalProjectHash) { throw 'Dry-run modified project.' }
    $saved = Join-Path $directory 'edited.valencia'
    $committed = InvokeCli 'commit' @('roughcut','apply',$project,'--plan',$planFile,'--save-as',$saved)
    $reopened = InvokeCli 'reopened' @('project','info',$saved)
    $movie = Join-Path $directory 'edited.mp4'
    $exported = InvokeCli 'export' @('export',$saved,'--sequence',$sequence,'--output',$movie)
    $scripted = InvokeCli 'copeland' @('script','run',(Join-Path $repository 'examples/auto-roughcut.ts'),'--project',$project)
    if (-not $scripted.result.success) { throw 'Copeland rough cut failed.' }

    function Decode([string]$file, [string]$time, [bool]$audio, [bool]$mixSource = $false, [bool]$fullFrame = $false) {
        $start = [Diagnostics.ProcessStartInfo]::new('ffmpeg')
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        $arguments = @('-v','error','-ss',$time,'-i',$file)
        if ($audio) {
            $arguments += @('-t','0.2','-vn')
            if ($mixSource) { $arguments += @('-filter_complex','[0:a:0][0:a:1]amix=inputs=2:normalize=0,pan=mono|c0=c0[a]','-map','[a]') }
            else { $arguments += @('-af','pan=mono|c0=c0') }
            $arguments += @('-ar','48000','-f','f32le','pipe:1')
        } else {
            $scale = if ($fullFrame) { 'scale=160:90:flags=area' } else { 'scale=32:18:flags=area' }
            $arguments += @('-frames:v','1','-vf',$scale,'-pix_fmt','rgb24','-f','rawvideo','pipe:1')
        }
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
    function FrameError([byte[]]$a, [byte[]]$b) {
        if ($a.Length -ne $b.Length -or $a.Length -eq 0) { throw 'Frame size mismatch.' }
        $sum = 0.0
        for ($i=0; $i -lt $a.Length; $i++) { $sum += [Math]::Abs([int]$a[$i]-[int]$b[$i]) }
        return $sum/$a.Length
    }
    $earlyError = FrameError (Decode $movie '1' $false) (Decode $source '1' $false)
    $lateError = FrameError (Decode $movie '2.5' $false) (Decode $source '4.5' $false)
    if ($earlyError -gt 8 -or $lateError -gt 8) { throw 'Retained source frame verification failed.' }
    function FrameCode([string]$file, [string]$time) {
        $pixels = Decode $file $time $false $false $true
        if ($pixels.Length -ne 160*90*3) { throw 'Counter frame size incorrect.' }
        $code = 0
        for ($bit=0; $bit -lt 8; $bit++) {
            if ($pixels[(84*160+$bit*20+10)*3] -ge 128) { $code = $code -bor (1 -shl $bit) }
        }
        return $code
    }
    $earlyCode = FrameCode $movie '1'
    $lateCode = FrameCode $movie '2.5'
    $sourceCode = FrameCode $source '4.5'
    if ($earlyCode -ne 30 -or $lateCode -ne 135 -or $lateCode -ne $sourceCode) { throw 'Independent binary source-frame identity mismatch.' }
    $audio = Decode $movie '2.5' $true
    $reference = Decode $source '4.5' $true $true
    if ($audio.Length -ne $reference.Length) { throw 'Audio length mismatch.' }
    $bestError = [double]::MaxValue; $bestLag = 0
    for ($lag=-48; $lag -le 48; $lag++) {
        $sum = 0.0
        for ($sample=48; $sample -lt $audio.Length/4-48; $sample++) {
            $sum += [Math]::Abs([BitConverter]::ToSingle($audio,$sample*4)-[BitConverter]::ToSingle($reference,($sample+$lag)*4))
        }
        $error = $sum/($audio.Length/4-96)
        if ($error -lt $bestError) { $bestError=$error; $bestLag=$lag }
    }
    if ($bestError -gt .04) { throw 'Audio source/alignment verification failed within the 1 ms Matroska source-clock tolerance.' }
    $probe = & ffprobe -v error -show_entries format=duration:stream=codec_name,width,height,r_frame_rate,sample_rate -of json $movie | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or [Math]::Abs([double]$probe.format.duration-4) -gt .04 -or -not ($probe.streams.codec_name -contains 'h264') -or -not ($probe.streams.codec_name -contains 'aac')) { throw 'Export codecs/duration failed.' }
    if ((Get-FileHash $source).Hash -ne $sourceHash -or (Get-FileHash $project).Hash -ne $originalProjectHash) { throw 'Source or original project modified.' }

    # Encoded 120-second stream: throughput/memory observation, not a multi-hour realtime playback claim.
    $longSource = Join-Path $directory 'benchmark.wav'
    & ffmpeg -v error -y -f lavfi -i "aevalsrc=exprs='if(gte(t,20)*lt(t,40),0,0.5*sin(2*PI*440*t))':s=48000:d=120" -c:a pcm_s16le $longSource
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark fixture failed.' }
    $longProject = Join-Path $directory 'benchmark.valencia'
    $longCreated = InvokeCli 'benchmark-create' @('project','create',$longProject,'--media',$longSource)
    $longAsset = $longCreated.result.document.assets[0].id
    $benchmark = InvokeCli 'benchmark-analysis' @('analyze','audio',$longProject,'--asset',$longAsset,'--stream','0')
    $benchmarkWarm = InvokeCli 'benchmark-cache' @('analyze','audio',$longProject,'--asset',$longAsset,'--stream','0')
    $cacheFile = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Valencia/Analysis-v1/$key.vca"
    $cacheBytes = (Get-Item $cacheFile).Length
    if ($Native) {
        & artifacts/publish/win-x64/Valencia.App.exe --analysis-smoke $source --output artifacts/local/m2b-native
        if ($LASTEXITCODE -ne 0) { throw 'Native analysis smoke failed.' }
    }
    [ordered]@{
        outcome='passed'; source=$source; sourceSha256=$sourceHash; sourceUnchanged=$true; originalProjectUnchanged=$true;
        audioCold=$cold; audioWarm=$warm; scenes=$scenes; planningMs=$proposed.elapsedMs; estimatedDuration=$proposed.result.estimatedDuration;
        cacheArtifactBytes=$cacheBytes; retainedFrameEarlyMae=$earlyError; retainedFrameLateMae=$lateError;
        verifiedFrameIds=@{ early=$earlyCode; late=$lateCode; expectedSource=$sourceCode };
        audioMaeAfterSourceClockAlignment=$bestError; measuredAudioOffsetSamples=$bestLag; measuredAudioOffsetMs=$bestLag/48.0; audioComparisonToleranceMs=1;
        independentProbe=$probe; copelandSucceeded=$scripted.result.success; benchmarkMediaSeconds=120;
        benchmark=$benchmark; benchmarkWarm=$benchmarkWarm; throughputIncludingCliStartup=120000/$benchmark.elapsedMs;
        sourceCharacteristics='160x90/30 H.264 numbered frames, hard visual cut at 3s, stereo anti-phase 440Hz/known silence [2,4), independent mono 880Hz; Matroska PCM 48kHz';
        output=$movie
    } | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $directory 'result.json')
    Get-Content (Join-Path $directory 'result.json')
} finally { Pop-Location }
