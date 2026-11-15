$ErrorActionPreference='Stop'
$repository=Split-Path -Parent $PSScriptRoot
Push-Location $repository
try {
 function ReadJson([string]$path){Get-Content -LiteralPath $path -Raw|ConvertFrom-Json}
 function Distribution($values){
  $v=@($values|ForEach-Object {[double]$_}|Sort-Object);$n=$v.Count
  if($n -eq 0){return $null}
  $median=if($n%2 -eq 0){($v[$n/2-1]+$v[$n/2])/2}else{$v[[int][Math]::Floor($n/2)]}
  [ordered]@{count=$n;median=$median;p95=$v[[Math]::Ceiling(.95*$n)-1];maximum=$v[-1];method='Median averages middle pair; p95 nearest rank; p99 unqualified at this sample count'}
 }
 $tests=@();$cases=@()
 foreach($suite in @('Valencia.Core.Tests','Valencia.Persistence.Tests','Valencia.Tests','Valencia.Programming.Tests')){
  $path="artifacts/local/tests/$suite.trx";[xml]$xml=Get-Content -LiteralPath $path
  $counter=$xml.TestRun.ResultSummary.Counters
  if([int]$counter.failed -ne 0 -or [int]$counter.notExecuted -ne 0 -or [int]$counter.passed -ne [int]$counter.total){throw "Suite not fully passing: $suite"}
  $tests += [ordered]@{suite=$suite;total=[int]$counter.total;passed=[int]$counter.passed;failed=[int]$counter.failed;skipped=[int]$counter.notExecuted;trx=$path;sha256=(Get-FileHash $path).Hash}
  foreach($result in $xml.TestRun.Results.UnitTestResult){$cases += [ordered]@{suite=$suite;test=$result.testName;outcome=$result.outcome;duration=$result.duration}}
 }
 $qualification=@()
 foreach($id in @('1080','1440','long')){
  $path="artifacts/local/m2e/qualification-$id/headless.json";$data=ReadJson $path
  $qualification += [ordered]@{id=$id;raw=$path;source=$data.source;sourceCharacteristics=$data.sourceCharacteristics;durationSeconds=$data.durationSeconds;importMs=$data.importMs;firstSoftwareDecodeMs=$data.firstDecodeMs;videoSeekMs=(Distribution $data.randomSeeks.videoMs);audioSeekMs=(Distribution $data.randomSeeks.audioMs);sequentialVideoMs=(Distribution $data.sequentialVideoMilliseconds);identityProfileVideoMs=(Distribution $data.identityProfileVideoMilliseconds);processPeakWorkingSetBytes=$data.peakWorkingSetBytes;scope=$data.scope}
 }
 $native=@()
 foreach($id in @('1080','1440')){
  $directory="artifacts/local/m2e/native-final-$id";$data=ReadJson "$directory/result.json"
  if($data.outcome -ne 'passed'){throw 'Native qualification failed'}
  $native += [ordered]@{id=$id;raw="$directory/result.json";resources=(ReadJson "$directory/resources.json");gpu=$data.gpu;window=$data.window;sourceVideo=$data.sourceVideo;elapsedPlaybackSeconds=$data.playbackSeconds;uploadedPreviewFps=$data.previewFps;frameMs=(Distribution $data.playbackFrameMilliseconds);maximumPresentationSkewSeconds=$data.presentationSkewSeconds;playbackUnderrunFrames=$data.playbackUnderrunFrames;seekLatencyMs=(Distribution $data.seekLatenciesMs);requests=$data.SeekRequests;obsoleteCompletionsRejected=$data.StaleGenerationsRejected;latestSeekVerified=$data.latestSeekVerified;obs=$data.obs;backgroundProxy=$data.backgroundProxy;physicalPointerActionsTested=$false}
 }
 $creator=ReadJson artifacts/local/m2e/creator-1080.json
 $independent=ReadJson artifacts/local/m2e/creator-1080/independent.json
 if(-not $creator.committed -or $independent.outcome -ne 'passed'){throw 'OBS creator workflow not independently passed'}
 $repro=Get-ChildItem artifacts/local/m2e -Directory -Filter 'repro-*'|Sort-Object LastWriteTime -Descending|Select-Object -First 1
 if(-not $repro -or -not(Test-Path (Join-Path $repro.FullName 'outputs/independent.json'))){throw 'Reproducible fixture wrapper has not completed independent verification'}
 $protocol=ReadJson artifacts/local/m2e/mcp-actual-obs.json
 $script=ReadJson artifacts/local/m2e/copeland-actual-obs.json
 $analysisScript=ReadJson artifacts/local/m2e/copeland-obs-analysis.json
 if($protocol.outcome -ne 'passed' -or -not $script.success -or -not $analysisScript.success){throw 'Actual MCP/Copeland qualification missing'}
 $interactions=[ordered]@{outcome='observed';inputPath='Windows computer-use SendInput/UI Automation against actual published Valencia window';project='artifacts/local/m2e/creator-1080/creator.valencia';initialWindowPixels=@(1282,752);maximizedWindowPixels=@(2560,1392);actions=@('Ctrl+O opened native project dialog and loaded saved creator project','Ruler click sought to 1.017s and changed displayed source frame','Ctrl+K split all active unlocked tracks and displayed dirty state','Ctrl+Z restored the split and cleared dirty state','Pointer drag moved final video clip 4s->5s and sequence 7.15s->8.15s','Ctrl+Z restored placement; Ctrl+Shift+Z redid; final undo restored source document','Space started native playback; K paused at 6.627s','Native title-bar maximize preserved visible title/footer and sharp text','Window closed cleanly after edits were undone; no save was issued');notTested=@('Physical touch','Non-Windows tablet/phone','Varied DPI','External focus loss during held drag','Human editorial judgment');automationLimits=@('Accessibility element focus reporting in the OS file dialog was unreliable; screenshot-backed coordinates successfully selected the filename field','Outward border drag was rejected by the automation helper bounds; native maximize and application-driven resize covered resize separately')}
 $interactions|ConvertTo-Json -Depth 10|Set-Content docs/validation/m2e-interactions.json
 $shots=@();$shotRoot='docs/validation/m2e/screenshots';New-Item -ItemType Directory $shotRoot -Force|Out-Null
 foreach($directory in @('artifacts/local/m2e/native-final-1440','artifacts/local/m2e/layout-final')){
  foreach($file in Get-ChildItem $directory -Filter '*.png'){
   $name=(Split-Path $directory -Leaf)+'-'+$file.Name;$destination=Join-Path $shotRoot $name
   Copy-Item -LiteralPath $file.FullName -Destination $destination
   $png=[IO.File]::ReadAllBytes($destination)
   $width=[int]$png[16]*16777216+[int]$png[17]*65536+[int]$png[18]*256+[int]$png[19]
   $height=[int]$png[20]*16777216+[int]$png[21]*65536+[int]$png[22]*256+[int]$png[23]
   $shots += [ordered]@{source=$file.FullName;path=$destination;width=$width;height=$height;sha256=(Get-FileHash $destination).Hash;kind='Actual native GPU framebuffer readback, not a mockup'}
  }
 }
 $sources=@(Get-ChildItem src,tests,samples -Recurse -File|Where-Object {$_.Extension -in @('.cs','.csproj','.ts','.slnx') -and $_.FullName -notmatch '\\(bin|obj)\\'}|Sort-Object FullName|ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($repository,$_.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
 $sources|ConvertTo-Json -Depth 5|Set-Content docs/validation/m2e-source-sha256.json
 $cases|ConvertTo-Json -Depth 5|Set-Content docs/validation/m2e-tests.json
 $testTotal=0
 foreach($suite in $tests){$testTotal += $suite.passed}
 $data=[ordered]@{
  milestone='M2e';recordedAt=[DateTimeOffset]::Now.ToString('o');outcome='Controlled OBS creator PoC, native MSDF/layout correction and frontend handoff delivered; broad human-footage/editorial and multi-hour audio-scrub qualification remains incomplete'
  machine=@{cpu=(Get-CimInstance Win32_Processor).Name;os=[Environment]::OSVersion.VersionString;runtime='10.0.15';sdk='10.0.401';gpu='NVIDIA GeForce RTX 3070';gpuMemoryMiB=8192;driver='610.88';ffmpeg=(& ffmpeg -version|Select-Object -First 1);preview='FFmpeg software decode, RGBA CPU-to-GPU upload, Aurelian Vulkan, NAudio 3 WaveOut/device clock';export='CPU shared evaluator/Skia + FFmpeg libx264/AAC'}
  tests=$tests;testTotal=$testTotal;headless=$qualification;native=$native
  obs=@{actualVersion='32.1.2';websocket='5.7.3';rpc=1;fixture='scripts/PrepareObsQualification.ps1';recordings=@('artifacts/local/m2e/capture-1080-confirmed.json','artifacts/local/m2e/capture-1440-confirmed.json');provenance='Real OBS NVENC recording of generated frame-numbered pattern, SAPI microphone, game tone and communications pulses. 1440 output upscales the 1080 source pattern. User OBS configuration untouched.';credentials='Ephemeral ignored server config; product client uses Windows CurrentUser DPAPI; no passwords in evidence';mcpActualReadOnly=$protocol;copelandActualReadOnly=$script;reproDirectory=$repro.FullName}
  creator=@{raw='artifacts/local/m2e/creator-1080.json';elapsedSeconds=$creator.elapsedSeconds;firstHeadlessInspectionMs=$creator.firstFrameMs;preview=$creator.preview;exports=$creator.exports;independent=$independent;resourceMeasurement=(ReadJson artifacts/local/m2e/resource-1080/resources.json);cacheFromActualCopeland=($analysisScript.returnValue|ConvertFrom-Json);scriptElapsedMs=$analysisScript.elapsedMs;scope='Whole pipeline timing, not isolated encoder throughput; 18s controlled source, 7.15s retained, two distinct Shorts; heuristic quality unreviewed'}
  ui=@{raw=(ReadJson artifacts/local/m2e/layout-final/result.json);screenshots=$shots;interactions='docs/validation/m2e-interactions.json';profileLaw='Phone <600; portrait tablet 600..1023; desktop >=1024; landscape tablet override width>=600 && width>=height';fontMeasurement='Same NativeUiText for Machina lowering and MSDF presentation; measured shortcut wrapping; no stretched UI bitmap';unicode='Native-pixel Skia/system-font fallback, basic scalar placement; complex shaping/bidi unqualified'}
  regression=@{m0Mp4=(ReadJson artifacts/local/smoke-mp4/smoke.json);m0Mkv=(ReadJson artifacts/local/smoke-mkv/smoke.json);m1b=(ReadJson artifacts/local/m1b-smoke/result.json);m2d=(ReadJson artifacts/local/m2d/result.json);m2dIndependentVideo='artifacts/local/m2d/independent-video.json';m2dIndependentAudio='artifacts/local/m2d/independent-audio.json';upstream='No Copeland/InputMan/Deliverance tracked source changes. Referenced projects built from current source. Historical upstream test totals are not added to the 238 Valencia tests.'}
  limitations=@('No substantial human multi-hour recording available; long high-resolution fixture repeats controlled OBS video with continuous reencoded AAC','Ten seconds actual playback per high-resolution native smoke, not two-hour sustained playback','Audio seeks on the two-hour three-stream source remain seconds long; decoded sample-count anchors are progressive/process-local, not persisted','p99 seek latency unqualified at 40 samples','Presentation skew is device-clock vs displayed timeline-frame sampling, not acoustic/display measurement','First visible metric is a successful captured-monitor upper bound including startup/test setup, not first-photon instrumentation','Varied DPI, touch, accessibility, complex text shaping and tiny-window UX remain incomplete','Markers are approximate until refined; automatic multi-file split assembly and replay marker alignment are not implemented','Human editorial quality and broad speech accuracy remain unreviewed','Scalar nondefault color processing is not realtime qualified at full resolution')
  sourceInventory='docs/validation/m2e-source-sha256.json';workspaceGit='Valencia directory has no Git metadata; no clean-worktree/commit claim';officialObsProtocol='https://github.com/obsproject/obs-websocket/blob/master/docs/generated/protocol.md'
 }
 $data|ConvertTo-Json -Depth 40|Set-Content docs/validation/m2e.json
 Write-Output ('Wrote M2e evidence with '+$data.testTotal+' passing tests')
}finally{Pop-Location}
