param([string]$Project='artifacts/local/m2e/recorded-1080.valencia', [string]$Directory='artifacts/local/m2e/native-final-1080',[switch]$ExerciseObs)
$ErrorActionPreference='Stop'
$repository=Split-Path -Parent $PSScriptRoot
Push-Location $repository
try {
 $destination=[IO.Path]::GetFullPath($Directory);New-Item -ItemType Directory $destination -Force|Out-Null
 $env:VALENCIA_MODEL_DIR=Join-Path $repository 'artifacts/local/m2c/models'
 if($ExerciseObs){
  $fixture=Get-Content artifacts/local/m2e/obs-fixture.json -Raw|ConvertFrom-Json
  $expected=Join-Path $repository 'artifacts/local/m2e/obs-portable/bin/64bit/obs64.exe'
  if((Get-Process -Id $fixture.pid).Path -ne $expected){throw 'OBS qualification requires the repository-owned portable fixture'}
  $credentials=Get-Content artifacts/local/m2e/obs-portable/config/obs-studio/plugin_config/obs-websocket/config.json -Raw|ConvertFrom-Json
  $env:VALENCIA_OBS_PASSWORD=$credentials.server_password
  $env:VALENCIA_OBS_ENDPOINT=$fixture.endpoint
  $env:VALENCIA_OBS_RECORDING_ROOT=$fixture.recordingRoot
  $env:VALENCIA_OBS_MANIFEST_DIR=Join-Path $destination 'obs-manifests'
 } else {Remove-Item Env:VALENCIA_OBS_ENDPOINT -ErrorAction SilentlyContinue}
 $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $repository 'artifacts/publish/win-x64/Valencia.App.exe'))
 $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 foreach($argument in @('--qualification-smoke',[IO.Path]::GetFullPath($Project),'--output',$destination)){$start.ArgumentList.Add($argument)}
 $startedAt=[DateTimeOffset]::UtcNow;$timer=[Diagnostics.Stopwatch]::StartNew();$native=[Diagnostics.Process]::Start($start)
 $stdout=$native.StandardOutput.ReadToEndAsync();$stderr=$native.StandardError.ReadToEndAsync()
 $peak=0L;$dedicated=0L;$shared=0L;$gpuSamples=0;$next=0.0
 try{
  while(-not $native.WaitForExit(100)){
   try{$native.Refresh();$peak=[Math]::Max($peak,$native.PeakWorkingSet64)}catch [InvalidOperationException]{}
   if($timer.Elapsed.TotalSeconds -gt 180){$native.Kill($true);throw 'Native qualification exceeded 180 seconds'}
   if($timer.Elapsed.TotalSeconds -ge $next){
    $next=$timer.Elapsed.TotalSeconds+.5
    $memory=@(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory -ErrorAction SilentlyContinue|Where-Object {$_.Name.StartsWith("pid_$($native.Id)_",[StringComparison]::Ordinal)})
    if($memory.Count -gt 0){$gpuSamples++;$dedicated=[Math]::Max($dedicated,($memory|Measure-Object DedicatedUsage -Sum).Sum);$shared=[Math]::Max($shared,($memory|Measure-Object SharedUsage -Sum).Sum)}
   }
  }
  $stdout.GetAwaiter().GetResult()|Set-Content (Join-Path $destination 'stdout.log')
  $stderr.GetAwaiter().GetResult()|Set-Content (Join-Path $destination 'stderr.log')
  $firstCapture=Get-Item (Join-Path $destination 'timeline.png') -ErrorAction SilentlyContinue
  $firstCaptureMs=if($firstCapture -and $firstCapture.LastWriteTimeUtc -ge $startedAt.UtcDateTime){($firstCapture.LastWriteTimeUtc-$startedAt.UtcDateTime).TotalMilliseconds}else{$null}
  [ordered]@{startedAt=$startedAt.ToString('o');firstProgramMonitorCaptureUpperBoundMs=$firstCaptureMs;exitCode=$native.ExitCode;wallSeconds=$timer.Elapsed.TotalSeconds;sampledProcessPeakWorkingSetBytes=$peak;processCpuSeconds=$native.TotalProcessorTime.TotalSeconds;sampledDedicatedGpuBytes=$dedicated;sampledSharedGpuBytes=$shared;gpuMemorySamples=$gpuSamples;scope='WDDM per-process GPU memory observations every >=500ms; process CPU/RAM excludes FFmpeg export child; OS caches not flushed'}|ConvertTo-Json|Set-Content (Join-Path $destination 'resources.json')
  if($native.ExitCode -ne 0){throw 'Native qualification failed; inspect stderr.log'}
 }finally{$native.Dispose()}
}finally{Pop-Location}

