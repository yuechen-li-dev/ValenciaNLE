param([string]$Source='artifacts/local/m2e/recordings/2026-11-15 11-06-12.mkv', [string]$Directory='artifacts/local/m2e/creator-1080')
$ErrorActionPreference='Stop'
$sourcePath=(Resolve-Path $Source).Path
$outputs=(Resolve-Path $Directory).Path
$plan=Get-Content (Join-Path $outputs 'creator-plan.json') -Raw|ConvertFrom-Json
$settings=($plan.operations|Where-Object operation -eq creator).settings
$inserts=@($plan.operations|Where-Object operation -eq insert)
$videoClips=@($inserts|Where-Object {$_.clip.streamIndex -eq 0}|ForEach-Object clip|Sort-Object {[long]$_.start})
$clock=[decimal]254016000000
$probe=(& ffprobe -v error -show_streams -show_format -of json $sourcePath)|ConvertFrom-Json
if($LASTEXITCODE -ne 0 -or [double]$probe.format.duration -gt 60){throw 'This bounded independent verifier accepts only short qualification recordings (<=60s).'}
function Number([decimal]$value){$value.ToString('0.############',[Globalization.CultureInfo]::InvariantCulture)}
function Decode([string[]]$arguments){
 $start=[Diagnostics.ProcessStartInfo]::new('ffmpeg');$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 foreach($arg in $arguments){$start.ArgumentList.Add($arg)}
 $process=[Diagnostics.Process]::Start($start);$errors=$process.StandardError.ReadToEndAsync();$memory=[IO.MemoryStream]::new()
 try{$copyTask=$process.StandardOutput.BaseStream.CopyToAsync($memory);if(-not $process.WaitForExit(60000)){$process.Kill($true);throw 'Independent decode timed out'};if($process.ExitCode -ne 0){throw $errors.GetAwaiter().GetResult()};$null=$copyTask.GetAwaiter().GetResult();return ,$memory.ToArray()}
 finally{$memory.Dispose();$process.Dispose()}
}
Add-Type -TypeDefinition @"
using System;
public static class ObsExportReference {
 public static double Pixels(byte[] expected,byte[] actual,int width,int height){
  if(expected.Length!=width*height*3||expected.Length!=actual.Length)throw new Exception("Independent frame length mismatch");
  double sum=0;int count=width*(int)(height*.65)*3;for(int i=0;i<count;i++)sum+=Math.Abs(expected[i]-actual[i]);return sum/count;
 }
 public static float[] Floats(byte[] bytes){var data=new float[bytes.Length/4];Buffer.BlockCopy(bytes,0,data,0,bytes.Length);return data;}
 public static double[] Compare(float[] expected,byte[] bytes){
  var actual=Floats(bytes);if(actual.Length!=expected.Length)throw new Exception("Output sample count mismatch");
  double error=0;for(int i=0;i<actual.Length;i++)error+=Math.Abs(expected[i]-actual[i]);
  double best=-1;int lag=0;for(int shift=-64;shift<=64;shift++){double xy=0,xx=0,yy=0;for(int i=128;i<actual.Length-128;i+=2){double x=expected[i],y=actual[i+2*shift];xy+=x*y;xx+=x*x;yy+=y*y;}double c=xy/Math.Sqrt(xx*yy);if(c>best){best=c;lag=shift;}}
  return new[]{error/actual.Length,best,(double)lag};
 }
 public static void Mix(float[] output,float[] source,int start,double gain,double master,double ceiling,double time,double[] keys,double[] db){
  if(start<0||start+output.Length>source.Length)throw new Exception("Source reference sample range outside recording");
  for(int i=0;i<output.Length;i++){
   double at=time+(i/2)/48000.0,automation=keys.Length==0?0:db[0];
   for(int k=1;k<keys.Length;k++){if(at>=keys[k])automation=db[k];else{if(at>keys[k-1])automation=db[k-1]+(db[k]-db[k-1])*(at-keys[k-1])/(keys[k]-keys[k-1]);break;}}
   output[i]+=source[start+i]*(float)(gain*Math.Pow(10,automation/20));
  }
 }
 public static void Master(float[] output,double master,double ceiling){for(int i=0;i<output.Length;i++)output[i]=(float)Math.Clamp(output[i]*master,-ceiling,ceiling);}
}
"@
$frameProbe=(& ffprobe -v error -select_streams v:0 -show_frames -show_entries frame=best_effort_timestamp_time -of json $sourcePath)|ConvertFrom-Json
if($LASTEXITCODE -ne 0){throw 'Source PTS probe failed'}
$pts=@($frameProbe.frames|ForEach-Object {[decimal]$_.best_effort_timestamp_time})
$videoResults=[Collections.Generic.List[object]]::new();$audioResults=[Collections.Generic.List[object]]::new()
foreach($profile in $settings.profiles){
 $file=Join-Path $outputs ($profile.name+'.mp4')
 $media=(& ffprobe -v error -show_entries format=duration:stream=codec_name,width,height -of json $file)|ConvertFrom-Json
 if($LASTEXITCODE -ne 0){throw 'Output probe failed'}
 $duration=([decimal]$profile.end.ticks-[decimal]$profile.start.ticks)/$clock
 if([Math]::Abs([double]$media.format.duration-[double]$duration) -gt .04 -or $media.streams.codec_name -notcontains 'h264' -or $media.streams.codec_name -notcontains 'aac'){throw 'Export container/codec/duration mismatch'}
 $v=$media.streams|Where-Object codec_name -eq h264
 if($v.width -ne $profile.width -or $v.height -ne $profile.height){throw 'Export dimensions mismatch'}
 $fps=[decimal]$profile.frameRate.numerator/[decimal]$profile.frameRate.denominator
 $count=[long][Math]::Ceiling($duration*$fps);$indices=[Collections.Generic.HashSet[long]]::new()
 [void]$indices.Add(0);[void]$indices.Add([Math]::Min(30,$count-1));[void]$indices.Add($count-1)
 foreach($clip in $videoClips){$cut=([decimal]$clip.start-[decimal]$profile.start.ticks)/$clock*$fps;foreach($delta in @(-1,0,1)){$frameIndex=[long][Math]::Ceiling($cut)+$delta;if($frameIndex -ge 0 -and $frameIndex -lt $count){[void]$indices.Add($frameIndex)}}}
 if($profile.width -gt $profile.height){$w=192;$h=108}else{$w=108;$h=192}
 foreach($index in ($indices|Sort-Object)){
  $global=[decimal]$profile.start.ticks/$clock+$index/$fps
  $clip=$videoClips|Where-Object {[decimal]$_.start/$clock -le $global -and ([decimal]$_.start/$clock+([decimal]$_.sourceOut.offsetTicks-[decimal]$_.sourceIn.offsetTicks)/$clock) -gt $global}|Select-Object -First 1
  if(-not $clip){throw 'Output frame maps to an unexpected timeline gap'}
  $sourceAt=[decimal]$clip.sourceIn.value*$clip.sourceIn.numerator/$clip.sourceIn.denominator+[decimal]$clip.sourceIn.offsetTicks/$clock+$global-[decimal]$clip.start/$clock
  $sourceIndex=0;for($i=0;$i -lt $pts.Count;$i++){if($pts[$i] -le $sourceAt){$sourceIndex=$i}else{break}}
  $fit=if($profile.fit -eq 'fill'){'increase'}else{'decrease'}
  $spatial="scale=$($profile.width):$($profile.height):force_original_aspect_ratio=${fit}:flags=neighbor"
  if($fit -eq 'increase'){$spatial+=",crop=$($profile.width):$($profile.height)"}else{$spatial+=",pad=$($profile.width):$($profile.height):(ow-iw)/2:(oh-ih)/2:black"}
  $expected=Decode @('-v','error','-i',$sourcePath,'-map','0:0','-vf',"select='eq(n,$sourceIndex)',${spatial},scale=${w}:${h}:flags=area",'-fps_mode','passthrough','-frames:v','1','-pix_fmt','rgb24','-f','rawvideo','pipe:1')
  $actual=Decode @('-v','error','-i',$file,'-vf',"select='eq(n,$index)',scale=${w}:${h}:flags=area",'-fps_mode','passthrough','-frames:v','1','-pix_fmt','rgb24','-f','rawvideo','pipe:1')
  $error=[ObsExportReference]::Pixels($expected,$actual,$w,$h)
  $videoResults.Add([ordered]@{profile=$profile.name;frame=$index;timelineSeconds=[double]$global;sourceTimestamp=[double]$pts[$sourceIndex];sourceFrame=$sourceIndex;top65PercentMeanRgbError=$error})
  if($error -gt 10){throw "Independent source selection differs: $($videoResults[$videoResults.Count-1]|ConvertTo-Json -Compress)"}
 }
}
# Full-source reference PCM is bounded to this <=60s fixture. Production remains streaming.
$pcm=@{};$origins=@{}
foreach($stream in ($probe.streams|Where-Object codec_type -eq audio)){
 $index=[int]$stream.index
 $clip=($inserts|Where-Object {$_.clip.streamIndex -eq $index}|Select-Object -First 1).clip
 $filter='aformat=sample_fmts=flt:sample_rates=48000:channel_layouts=stereo'
 if($clip.properties.audio.noiseReductionDb -gt 0){$filter+=",afftdn=nr=$($clip.properties.audio.noiseReductionDb):nf=-45:tn=0"}
 $pcm[$index]=[ObsExportReference]::Floats((Decode @('-v','error','-i',$sourcePath,'-map',"0:$index",'-af',$filter,'-ac','2','-ar','48000','-f','f32le','pipe:1')))
 $first=(& ffprobe -v error -select_streams "$index" -read_intervals '%+#1' -show_frames -show_entries frame=best_effort_timestamp_time -of json $sourcePath)|ConvertFrom-Json
 $origins[$index]=[decimal]$first.frames[0].best_effort_timestamp_time
}
foreach($profile in $settings.profiles){
 $from=[decimal]$profile.start.ticks/$clock;$to=[decimal]$profile.end.ticks/$clock
 foreach($clip in $videoClips){
  $at=[Math]::Max($from,[decimal]$clip.start/$clock)+.5
  if($at+.2 -ge $to -or $at+.2 -ge ([decimal]$clip.start+([decimal]$clip.sourceOut.offsetTicks-[decimal]$clip.sourceIn.offsetTicks))/$clock){continue}
  $expected=[float[]]::new(9600*2)
  foreach($insert in $inserts){
   $c=$insert.clip;$index=[int]$c.streamIndex
   if($index -eq 0 -or [decimal]$c.start/$clock -gt $at -or ([decimal]$c.start+[decimal]$c.sourceOut.offsetTicks-[decimal]$c.sourceIn.offsetTicks)/$clock -le $at){continue}
   $sourceAt=[decimal]$c.sourceIn.value*$c.sourceIn.numerator/$c.sourceIn.denominator+[decimal]$c.sourceIn.offsetTicks/$clock+$at-[decimal]$c.start/$clock
   $sample=[long][Math]::Round(($sourceAt-$origins[$index])*48000,0,[MidpointRounding]::AwayFromZero)
   if($c.properties.audio.noiseReductionDb -gt 0){$sample+=1200}
   $track=$settings.audio|Where-Object trackId -eq $insert.trackId
   $keys=@($track.envelope|ForEach-Object {[double]([decimal]$_.time.ticks/$clock)});$db=@($track.envelope.gainDb)
   [ObsExportReference]::Mix($expected,$pcm[$index],[int]($sample*2),[Math]::Pow(10,$track.gainDb/20),1,1,$at,$keys,$db)
  }
  [ObsExportReference]::Master($expected,[Math]::Pow(10,$settings.masterGainDb/20),[Math]::Pow(10,$settings.peakCeilingDb/20))
  $outputSample=[long][Math]::Round(($at-$from)*48000,0,[MidpointRounding]::AwayFromZero)
  $actual=Decode @('-v','error','-i',(Join-Path $outputs ($profile.name+'.mp4')),'-af',"atrim=start_sample=${outputSample}:end_sample=$($outputSample+9600)",'-ac','2','-ar','48000','-f','f32le','pipe:1')
  $metrics=[ObsExportReference]::Compare($expected,$actual)
  $audioResults.Add([ordered]@{profile=$profile.name;timelineSeconds=$at;meanPcmError=$metrics[0];correlation=$metrics[1];offsetSamples=$metrics[2]})
  if([Math]::Abs($metrics[2]) -gt 2 -or $metrics[1] -lt .97 -or $metrics[0] -gt .02){throw "Independent multi-stream audio mismatch: $($audioResults[$audioResults.Count-1]|ConvertTo-Json -Compress)"}
 }
}
[ordered]@{outcome='passed';source=$sourcePath;profiles=$settings.profiles.Count;video=$videoResults;audio=$audioResults;reference='Independent FFmpeg CLI source/output decode; source frame lookup uses actual PTS, never nominal source FPS; explicit three-stream PCM gains and duck envelopes';limits='Short controlled OBS source; selected output frames/top65% (caption area excluded), selected 200ms audio windows, not human editorial review or acoustic/display latency'}|ConvertTo-Json -Depth 10|Set-Content (Join-Path $outputs 'independent.json')



