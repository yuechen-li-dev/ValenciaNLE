param([int]$Width=1920,[int]$Height=1080,[int]$Port=4467)
$ErrorActionPreference='Stop'
$repository=Split-Path -Parent $PSScriptRoot
$directory=Join-Path $repository 'artifacts/local/m2e'
$portable=Join-Path $directory 'obs-portable'
New-Item -ItemType Directory $directory -Force|Out-Null
if(Get-Process obs64 -ErrorAction SilentlyContinue){throw 'An OBS process is already running. This fixture setup will not change an active OBS instance.'}
if(-not(Test-Path "$portable/bin/64bit/obs64.exe")){
 New-Item -ItemType Directory $portable -Force|Out-Null
 foreach($part in @('bin','data','obs-plugins')){Copy-Item -LiteralPath "C:/Program Files/obs-studio/$part" -Destination $portable -Recurse}
}
New-Item -ItemType File "$portable/portable_mode.txt" -Force|Out-Null
$config="$portable/config/obs-studio"
New-Item -ItemType Directory "$config/basic/profiles/Valencia","$config/basic/scenes","$config/plugin_config/obs-websocket","$directory/recordings" -Force|Out-Null
$password=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:VALENCIA_OBS_PASSWORD=$password
# OBS itself requires its server password in its own config. This is an ephemeral, ignored fixture,
# never a source/project credential. Product client credentials use CurrentUser DPAPI.
@{server_enabled=$true;server_port=$Port;server_password=$password;auth_required=$true;alerts_enabled=$false;first_load=$false}|ConvertTo-Json|Set-Content "$config/plugin_config/obs-websocket/config.json"
@'
[General]
FirstRun=true
EnableAutoUpdates=false
LastVersion=536936450

[Basic]
Profile=Valencia Qualification
ProfileDir=Valencia
SceneCollection=Valencia Qualification
SceneCollectionFile=Valencia

[BasicWindow]
PreviewEnabled=true
StudioMode=false
RecordWhenStreaming=false
KeepRecordingWhenStreamStops=true

[Audio]
SampleRate=48000
ChannelSetup=Stereo
'@|Set-Content "$config/global.ini"
@"
[General]
Name=Valencia Qualification

[Video]
BaseCX=$Width
BaseCY=$Height
OutputCX=$Width
OutputCY=$Height
FPSType=0
FPSCommon=60
ScaleType=bilinear
ColorFormat=NV12
ColorSpace=709
ColorRange=Partial

[Audio]
SampleRate=48000
ChannelSetup=Stereo

[Output]
Mode=Advanced

[AdvOut]
RecType=Standard
RecFilePath=$($directory.Replace('\','/'))/recordings
RecFormat2=mkv
RecEncoder=obs_nvenc_h264_tex
RecAudioEncoder=ffmpeg_aac
RecTracks=7
RecRescale=false
RecApplyServiceSettings=false
RecSplitFile=false
Track1Bitrate=160
Track2Bitrate=160
Track3Bitrate=160
Track1Name=Microphone fixture
Track2Name=Game fixture
Track3Name=Communications fixture
"@|Set-Content "$config/basic/profiles/Valencia/basic.ini"
@{rate_control='CQP';cqp=28;keyint_sec=10;bf=3;preset='p5';tune='hq';multipass='qres'}|ConvertTo-Json|Set-Content "$config/basic/profiles/Valencia/recordEncoder.json"
$speech="$directory/microphone.wav"
if(-not(Test-Path $speech)){
 $voice=New-Object -ComObject SAPI.SpVoice;$stream=New-Object -ComObject SAPI.SpFileStream
 try{$stream.Open($speech,3);$voice.AudioOutputStream=$stream;[void]$voice.Speak('Welcome to Valencia. The important announcement begins now. Keep this explanation for the final video. This recording demonstrates separate microphone, game, and communications tracks. Thank you for listening.');$stream.Close()}
 finally{[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($stream);[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($voice)}
}
$motion="$directory/motion.mp4"
if(-not(Test-Path $motion)){
 & ffmpeg -v error -y -f lavfi -i 'testsrc2=size=1920x1080:rate=60:duration=12' -f lavfi -i 'sine=frequency=330:sample_rate=48000:duration=12' -vf "drawtext=fontfile='C\:/Windows/Fonts/consola.ttf':text='OBS fixture source frame %{n}':x=40:y=40:fontsize=42:fontcolor=white:box=1:boxcolor=black" -c:v libx264 -preset fast -crf 24 -g 600 -bf 3 -c:a aac $motion
 if($LASTEXITCODE -ne 0){throw 'Motion fixture generation failed'}
}
$communications="$directory/communications.wav"
if(-not(Test-Path $communications)){
 & ffmpeg -v error -y -f lavfi -i "aevalsrc=0.08*sin(2*PI*880*t)*lt(mod(t\,4)\,0.2):s=48000:d=12" -c:a pcm_s16le $communications
 if($LASTEXITCODE -ne 0){throw 'Communications fixture generation failed'}
}
function Media([string]$name,[string]$file,[int]$mixers){
 return @{name=$name;uuid=[guid]::NewGuid().ToString();id='ffmpeg_source';versioned_id='ffmpeg_source';settings=@{local_file=$file.Replace('\','/');is_local_file=$true;looping=$true;restart_on_activate=$true;close_when_inactive=$false;hw_decode=$false;clear_on_media_end=$false};mixers=$mixers;volume=1.0;balance=0.5;enabled=$true;muted=$false;sync=0;flags=0}
}
$game=Media 'Game fixture' $motion 2;$mic=Media 'Microphone fixture' $speech 1;$comm=Media 'Communications fixture' $communications 4
$items=@();$itemId=0
foreach($media in @($game,$mic,$comm)){
 $items+=@{name=$media.name;source_uuid=$media.uuid;id=++$itemId;visible=$true;locked=$false;rot=0;pos=@{x=0;y=0};scale=@{x=$Width/1920.0;y=$Height/1080.0};align=5;bounds_type=0}
}
$scene=@{name='Valencia Qualification';uuid=[guid]::NewGuid().ToString();id='scene';versioned_id='scene';settings=@{id_counter=3;custom_size=$false;items=$items};mixers=0;volume=1.0;enabled=$true;muted=$false}
@{name='Valencia Qualification';sources=@($game,$mic,$comm,$scene);groups=@();scene_order=@(@{name=$scene.name});current_scene=$scene.name;current_program_scene=$scene.name;current_transition='Fade';transitions=@();quick_transitions=@();saved_projectors=@();preview_locked=$false;scaling_enabled=$false;version=2}|ConvertTo-Json -Depth 15|Set-Content "$config/basic/scenes/Valencia.json"
$process=Start-Process -FilePath "$portable/bin/64bit/obs64.exe" -ArgumentList @('--portable','--disable-updater','--disable-missing-files-check','--multi','--profile','"Valencia Qualification"','--collection','"Valencia Qualification"') -WorkingDirectory "$portable/bin/64bit" -WindowStyle Hidden -PassThru
@{pid=$process.Id;endpoint="ws://127.0.0.1:$Port";recordingRoot="$directory/recordings";width=$Width;height=$Height;fps=60;provenance='Real OBS encoding of controlled generated media sources, not human creator footage; user OBS configuration untouched'}|ConvertTo-Json|Set-Content "$directory/obs-fixture.json"
Write-Output "Isolated OBS process $($process.Id), port $Port. Password remains in this process environment, never printed."
