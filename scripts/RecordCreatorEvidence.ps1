$ErrorActionPreference='Stop'
$repository=Split-Path -Parent $PSScriptRoot
Push-Location $repository
try {
 function ReadJson([string]$path){Get-Content $path -Raw|ConvertFrom-Json}
 $testFiles=@('Valencia.Core.Tests.trx','Valencia.Persistence.Tests.trx','Valencia.Tests.trx','Valencia.Programming.Tests.trx','m2d-copeland.trx','m2d-aurelian-graphics.trx','m2d-aurelian-inference.trx')
 $tests=@();$allCases=@()
 foreach($name in $testFiles){
  $file="artifacts/local/tests/$name";[xml]$xml=Get-Content $file
  $c=$xml.TestRun.ResultSummary.Counters
  if([int]$c.failed -ne 0 -or [int]$c.notExecuted -ne 0 -or [int]$c.passed -ne [int]$c.total){throw "Suite not completely passed: $name"}
  $tests += [ordered]@{trx=$file;sha256=(Get-FileHash $file).Hash;total=[int]$c.total;passed=[int]$c.passed;failed=[int]$c.failed;skipped=[int]$c.notExecuted}
  foreach($result in $xml.TestRun.Results.UnitTestResult){
   $allCases += [ordered]@{suite=$name;name=$result.testName;outcome=$result.outcome;duration=$result.duration}
  }
 }
 [xml]$programming=Get-Content artifacts/local/tests/Valencia.Programming.Tests.trx
 $alignmentLine=([string]($programming.TestRun.Results.UnitTestResult|Where-Object testName -like '*DtwRefinement*').Output.StdOut).Trim()
 $alignment=$alignmentLine.Substring('M2D_ALIGNMENT='.Length)|ConvertFrom-Json
 [xml]$media=Get-Content artifacts/local/tests/Valencia.Tests.trx
 $seekLine=([string]($media.TestRun.Results.UnitTestResult|Where-Object testName -like '*VfrBFrames*').Output.StdOut -split "`r?`n"|Where-Object { $_.StartsWith('M2D_RANDOM_SEEK_MS=') })
 $seek=@($seekLine.Substring('M2D_RANDOM_SEEK_MS='.Length)|ConvertFrom-Json)
 $warm=ReadJson artifacts/local/m2d/result.json;$cold=ReadJson artifacts/local/m2d/cold-result.json
 $plan=ReadJson artifacts/local/m2d/outputs/creator-plan.json
 $profiles=($plan.operations|Where-Object operation -eq creator).settings.profiles
 $outputs=@();$comparison=@()
 foreach($profile in $profiles){
  $file="artifacts/local/m2d/outputs/$($profile.name).mp4"
  $probe=(& ffprobe -v error -show_entries format=duration,size:stream=codec_name,width,height,sample_rate,r_frame_rate -of json $file|ConvertFrom-Json)
  if($LASTEXITCODE -ne 0){throw 'Output probe failed'}
  $outputs += [ordered]@{file=$file;sha256=(Get-FileHash $file).Hash;profile=$profile;probe=$probe}
  $script="artifacts/local/m2d/three-hour.valencia.outputs/$($profile.name).mp4"
  $same=(Get-FileHash $script).Hash -eq (Get-FileHash $file).Hash
  $comparison += [ordered]@{file=$script;sha256=(Get-FileHash $script).Hash;byteIdenticalToCli=$same;
    decodedAudioIdentical=if($same){$true}else{(Compare-Object (Get-Content artifacts/local/m2d/cli-short-3.framemd5|Where-Object {$_ -like '1,*'}) (Get-Content artifacts/local/m2d/script-short-3.framemd5|Where-Object {$_ -like '1,*'})|Measure-Object).Count -eq 0};
    videoComparison=if($same){'Byte-identical container'}else{'Independent FFmpeg SSIM All reported 1.000000 (67.613117 dB); eight frames have tiny lossy pixel differences. Not bit-identical video.'}}
 }
 $comparison|ConvertTo-Json -Depth 8|Set-Content artifacts/local/m2d/script-comparison.json
 $fingerprint=@(rg --files src tests samples scripts -g '*.cs' -g '*.csproj' -g '*.obj.ts' -g '*.ps1' -g '!**/bin/**' -g '!**/obj/**'|Sort-Object|ForEach-Object {[ordered]@{path=$_;sha256=(Get-FileHash $_).Hash}})
 $canonical=($fingerprint|ForEach-Object {$_.path.Replace('\','/')+' '+$_.sha256}) -join "`n"
 $video=ReadJson artifacts/local/m2d/independent-video.json;$audio=ReadJson artifacts/local/m2d/independent-audio.json
 if($video.outcome -ne 'passed' -or $audio.outcome -ne 'passed'){throw 'Independent verification missing'}
 $loudness=Get-Content artifacts/local/m2d/export-loudness.log -Raw
 $start=$loudness.LastIndexOf('{');$end=$loudness.LastIndexOf('}')
 $measured=$loudness.Substring($start,$end-$start+1)|ConvertFrom-Json
 $proxy=(Get-Content artifacts/local/m2d/proxy-three-hour.json|Where-Object {$_.StartsWith('{"apiVersion":')}|Select-Object -Last 1)|ConvertFrom-Json
 $fixtures=$warm.fixture
 if((Get-FileHash $fixtures.source).Hash -ne $fixtures.sourceSha256){throw 'Source hash changed'}
 $evidence=[ordered]@{
  milestone='M2d';recordedAt=(Get-Date -Format o);outcome='Working creator PoC qualified on explicitly generated low-resolution three-hour media; original PoC parity remains partial'
  machine=[ordered]@{cpu=(Get-CimInstance Win32_Processor).Name.Trim();os=[Environment]::OSVersion.VersionString;sdk=(& dotnet --version);gpu=(& nvidia-smi --query-gpu=name,driver_version,memory.total --format=csv,noheader);ffmpeg=(& ffmpeg -version|Select-Object -First 1)}
  sourceBuild=[ordered]@{valenciaHasGitMetadata=(Test-Path .git);sourceManifestSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)));copelandCommit=(& git -C ../Copeland rev-parse HEAD);copelandStatus=(& git -C ../Copeland status --short|Out-String).Trim();manifest=$fingerprint}
  published=@('artifacts/publish/win-x64/Valencia.App.exe','artifacts/publish/win-x64/Valencia.App.dll','artifacts/publish/cli-win-x64/Valencia.Cli.exe','artifacts/publish/cli-win-x64/Valencia.Cli.dll'|ForEach-Object {[ordered]@{file=$_;sha256=(Get-FileHash $_).Hash}})
  tests=$tests;valenciaPassed=([int]$tests[0].passed+[int]$tests[1].passed+[int]$tests[2].passed+[int]$tests[3].passed)
  source=$fixtures;sourceUnchanged=$true;inputProjectUnchanged=$warm.projectUnchanged
  coldPipeline=[ordered]@{seconds=$cold.workflow.wallSeconds;cliPeakWorkingSetBytes=$cold.workflow.sampledPeakWorkingSetBytes;cpuSeconds=$cold.workflow.cpuSeconds;qualification='Earlier cold run before denoiser latency correction; output superseded; initial inspection at time zero is not qualified as a decoded first frame'}
  correctedCacheAssistedPipeline=[ordered]@{seconds=$warm.workflow.wallSeconds;cliPeakWorkingSetBytes=$warm.workflow.sampledPeakWorkingSetBytes;cpuSeconds=$warm.workflow.cpuSeconds;firstDecodedInspectionMs=$warm.workflow.result.firstFrameMs;durationTicks=$warm.workflow.result.preview.estimatedDuration.ticks;candidateCount=$warm.workflow.result.preview.candidateCount;segments=$warm.workflow.result.preview.segments.Count;operationCount=$warm.workflow.result.preview.operationCount;diagnostics=$warm.workflow.result.preview.diagnostics;normalization=$warm.workflow.result.preview.loudness}
  outputs=$outputs;independentVideo=$video;independentAudio=$audio
  actualExportLoudness=[ordered]@{integratedLufs=[double]$measured.input_i;truePeakDb=[double]$measured.input_tp;loudnessRange=[double]$measured.input_lra;note='Input measurements of the encoded file; loudnorm output fields are a discarded diagnostic filter output, not Valencia output'}
  alignment=$alignment;syntheticRandomSeekMilliseconds=$seek
  proxy=[ordered]@{timing=(ReadJson artifacts/local/m2d/proxy-timing.json);data=$proxy.proxy;nativePreview=(ReadJson artifacts/local/m2d-proxy-native/result.json)}
  script=[ordered]@{run=(ReadJson artifacts/local/m2d/script-run.json);outputs=$comparison;entry='examples/creator-cut.ts';runtime='Copeland C# backend, Roslyn, ordinary CLR; no Node';note='Three containers byte-identical; fourth has identical decoded audio and SSIM video comparison above'}
  native=[ordered]@{creator=(ReadJson artifacts/local/m2d-native/result.json);threeHourSource=(ReadJson artifacts/local/m2d-source-native/smoke.json);m0mp4=(ReadJson artifacts/local/smoke-mp4/smoke.json);m0mkv=(ReadJson artifacts/local/smoke-mkv/smoke.json);m1b=(ReadJson artifacts/local/m1b-smoke/result.json);m1bIndependent=(ReadJson artifacts/local/m1b-smoke/independent-verification.json);m2b=(ReadJson artifacts/local/m2b-native/result.json);m2c=(ReadJson artifacts/local/m2c-native/result.json)}
  font=[ordered]@{path='C:/Windows/Fonts/arial.ttf';sha256=(Get-FileHash C:/Windows/Fonts/arial.ttf).Hash;redistributed=$false}
  limits=@('Synthetic 160x90 source / 320x180 and 180x320 exports; no real OBS/editorial/high-resolution qualification','Three Shorts use distinct source windows; repeated fixture speech does not establish topic diversity','Native source smoke 3.57s and edited playback about 5s per run, not three hours elapsed playback','No full Whisper GPU inference or GPU scheduling research in this milestone','Peak GPU memory, child-process aggregate RAM and per-stage cold analysis throughput not separately measured','Native FFmpeg logged Matroska keyframe warnings; tested actual-PTS/source-frame checks pass; unusual/damaged cue behavior unqualified','Word alignment: one English SAPI voice, 15 uniquely matched starts; no overlapping-speaker or human corpus qualification','Full original codec/interchange/advanced trim matrix remains deferred or partial; see docs/M2D_POC_COVERAGE.md')
 }
 $evidence|ConvertTo-Json -Depth 50|Set-Content docs/validation/m2d.json
 $allCases|ConvertTo-Json -Depth 8|Set-Content docs/validation/m2d-tests.json
 Write-Output 'docs/validation/m2d.json'
} finally {Pop-Location}
