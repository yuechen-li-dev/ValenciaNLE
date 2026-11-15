param([switch]$RecordFixture,[switch]$Commit,[int]$Seconds=20)
$ErrorActionPreference='Stop'
if(-not $RecordFixture){throw 'This bounded demo records the isolated OBS fixture. Invoke explicitly with -RecordFixture; add -Commit only to apply/save/export.'}
if($Seconds -lt 10 -or $Seconds -gt 60){throw 'Qualification demo duration must be 10..60 seconds'}
$repository=Split-Path -Parent $PSScriptRoot
Push-Location $repository
try {
 $fixture=Get-Content artifacts/local/m2e/obs-fixture.json -Raw|ConvertFrom-Json
 $expected=[IO.Path]::GetFullPath((Join-Path $repository 'artifacts/local/m2e/obs-portable/bin/64bit/obs64.exe'))
 if((Get-Process -Id $fixture.pid).Path -ne $expected){throw 'Run PrepareObsQualification.ps1 first; only the repository-owned fixture is accepted'}
 $secret=Get-Content artifacts/local/m2e/obs-portable/config/obs-studio/plugin_config/obs-websocket/config.json -Raw|ConvertFrom-Json
 $env:VALENCIA_OBS_PASSWORD=$secret.server_password
 $env:VALENCIA_MODEL_DIR=Join-Path $repository 'artifacts/local/m2c/models'
 $directory=Join-Path $repository ('artifacts/local/m2e/repro-'+(Get-Date -Format yyyyMMdd-HHmmss)+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))
 New-Item -ItemType Directory $directory|Out-Null
 $cli=Join-Path $repository 'artifacts/publish/cli-win-x64/Valencia.Cli.exe'
 function RunCli([string]$name,[string[]]$arguments){
  $start=[Diagnostics.ProcessStartInfo]::new($cli);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
  foreach($argument in $arguments){$start.ArgumentList.Add($argument)}
  $process=[Diagnostics.Process]::Start($start);$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
  try{
   if(-not $process.WaitForExit(300000)){$process.Kill($true);throw 'Demo invocation exceeded five minutes; OBS recording is not automatically stopped by local cancellation'}
   $text=$stdout.GetAwaiter().GetResult();$stderr.GetAwaiter().GetResult()|Set-Content (Join-Path $directory "$name.log")
   $text|Set-Content (Join-Path $directory "$name.json")
   if($process.ExitCode -ne 0){throw "$name failed; inspect its log"}
   return $text|ConvertFrom-Json
  }finally{$process.Dispose()}
 }
 $initial=Join-Path $directory 'initial.valencia';$recorded=Join-Path $directory 'recorded.valencia';$manifest=Join-Path $directory 'take.obs-session.json'
 $null=RunCli 'create' @('project','create',$initial,'--width',"$($fixture.width)",'--height',"$($fixture.height)",'--fps-num','60')
 $take=RunCli 'record' @('obs','record','--project',$initial,'--endpoint',$fixture.endpoint,'--recording-root',$fixture.recordingRoot,'--manifest',$manifest,'--seconds',"$Seconds",'--mark-at','5','--allow-recording-control','--place','--save-as',$recorded)
 $asset=$take.result.assetIds[0];$outputs=Join-Path $directory 'outputs'
 $arguments=@('creator','run',$recorded,'--asset',$asset,'--stream','1','--music-streams','2','--recording-manifest',$manifest,'--use-approximate-markers','--target-seconds','12','--min-seconds','1','--max-seconds','4','--short-seconds','3','--width',"$($fixture.width)",'--height',"$($fixture.height)",'--keywords','Valencia;important;explanation;recording;final','--output-dir',$outputs)
 if($Commit){$arguments+=@('--commit','--verify-history')}
 $result=RunCli 'creator' $arguments
 if($Commit){./scripts/VerifyObsCreator.ps1 -Source $take.result.manifest.files[0].path -Directory $outputs}
 Write-Output $directory
}finally{Pop-Location}
