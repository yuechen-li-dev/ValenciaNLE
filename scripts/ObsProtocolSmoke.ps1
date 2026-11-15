param()
$ErrorActionPreference='Stop'
$repository=Split-Path -Parent $PSScriptRoot
Push-Location $repository
try {
 $fixture=Get-Content artifacts/local/m2e/obs-fixture.json -Raw|ConvertFrom-Json
 $credentials=Get-Content artifacts/local/m2e/obs-portable/config/obs-studio/plugin_config/obs-websocket/config.json -Raw|ConvertFrom-Json
 $env:VALENCIA_OBS_PASSWORD=$credentials.server_password
 $projectInfo=(& artifacts/publish/cli-win-x64/Valencia.Cli.exe project info artifacts/local/m2e/recorded-1080.valencia)|ConvertFrom-Json
 $identity=$projectInfo.project.document.id
 $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $repository 'artifacts/publish/cli-win-x64/Valencia.Cli.exe'))
 $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 foreach($argument in @('mcp','--root',$repository,'--project','artifacts/local/m2e/recorded-1080.valencia')){$start.ArgumentList.Add($argument)}
 $server=[Diagnostics.Process]::Start($start);$errors=$server.StandardError.ReadToEndAsync();$script:requestId=0
 function Rpc([string]$method,$parameters){
  $request=@{jsonrpc='2.0';id=++$script:requestId;method=$method;params=$parameters}
  $server.StandardInput.WriteLine(($request|ConvertTo-Json -Depth 12 -Compress));$server.StandardInput.Flush()
  $line=$server.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
  if(-not $line){throw 'MCP server exited before replying'}
  $message=$line|ConvertFrom-Json
  if($message.error -or $message.result.isError){throw "MCP request $method / $($parameters.name) rejected: $($message.result.structuredContent.result.message) $($message.error.message)"}
  return $message.result
 }
 try{
  $initial=Rpc 'initialize' @{protocolVersion='2025-11-25';capabilities=@{};clientInfo=@{name='Valencia actual OBS read-only qualification';version='1'}}
  $server.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
  $tools=Rpc 'tools/list' @{}
  $project=(Rpc 'tools/call' @{name='project_info';arguments=@{projectId=$identity;compact=$true}}).structuredContent.result
  $connected=Rpc 'tools/call' @{name='obs_connect';arguments=@{projectId=$project.id;request=@{endpoint=$fixture.endpoint;recordingRoot='artifacts/local/m2e/recordings';manifest='artifacts/local/m2e/mcp-actual-obs-session.json'}}}
  $status=Rpc 'tools/call' @{name='obs_status';arguments=@{projectId=$project.id}}
  if($tools.tools.Count -ne 25 -or $status.structuredContent.result.controlAuthorized){throw 'Unexpected tool discovery/control policy'}
  [ordered]@{outcome='passed';transport='actual stdio MCP -> actual OBS 32 WebSocket; read-only';initialize=$initial;toolCount=$tools.tools.Count;connected=$connected.structuredContent.result;status=$status.structuredContent.result}|ConvertTo-Json -Depth 15|Set-Content artifacts/local/m2e/mcp-actual-obs.json
 }finally{
  $server.StandardInput.Close()
  if(-not $server.WaitForExit(10000)){$server.Kill($true)}
  $errors.GetAwaiter().GetResult()|Set-Content artifacts/local/m2e/mcp-actual-obs.log
  $server.Dispose()
 }
}finally{Pop-Location}


