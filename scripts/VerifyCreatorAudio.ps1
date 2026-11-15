param([string]$Directory='artifacts/local/m2d')
$ErrorActionPreference='Stop'
$directoryPath=(Resolve-Path $Directory).Path
$outputs=Join-Path $directoryPath 'outputs';$source=Join-Path $directoryPath 'three-hour.mkv'
$plan=Get-Content (Join-Path $outputs 'creator-plan.json') -Raw|ConvertFrom-Json
$settings=($plan.operations|Where-Object operation -eq creator).settings
$microphone=@($plan.operations|Where-Object { $_.operation -eq 'insert' -and $_.clip.streamIndex -eq 1 }|Sort-Object {[long]$_.clip.start})
$music=@($plan.operations|Where-Object { $_.operation -eq 'insert' -and $_.clip.streamIndex -eq 2 }|Sort-Object {[long]$_.clip.start})
function Decode([string[]]$arguments){
 $start=[Diagnostics.ProcessStartInfo]::new('ffmpeg');$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 foreach($a in $arguments){$start.ArgumentList.Add($a)}
 $process=[Diagnostics.Process]::Start($start);$errors=$process.StandardError.ReadToEndAsync();$memory=[IO.MemoryStream]::new()
 try{$process.StandardOutput.BaseStream.CopyTo($memory);if(-not $process.WaitForExit(120000)){$process.Kill($true);throw 'Audio verifier timed out'};if($process.ExitCode -ne 0){throw $errors.GetAwaiter().GetResult()};return ,$memory.ToArray()}
 finally{$memory.Dispose();$process.Dispose()}
}
Add-Type -TypeDefinition @'
using System;
public static class CreatorAudioReference {
 static float[] Floats(byte[] b){float[] f=new float[b.Length/4];Buffer.BlockCopy(b,0,f,0,b.Length);return f;}
 public static double[] Compare(byte[] m,byte[] g,byte[] a,double micGain,double gameGain,double master,double ceiling,double time,double[] keys,double[] db){
  var mic=Floats(m);var music=Floats(g);var actual=Floats(a);if(mic.Length!=actual.Length||music.Length!=actual.Length)throw new Exception("Reference sample counts differ");
  var expected=new float[actual.Length];double error=0;for(int i=0;i<expected.Length;i++){
   double t=time+(i/2)/48000.0,automation=keys.Length==0?0:db[0];
   for(int k=1;k<keys.Length;k++){if(t>=keys[k])automation=db[k];else {if(t>keys[k-1])automation=db[k-1]+(db[k]-db[k-1])*(t-keys[k-1])/(keys[k]-keys[k-1]);break;}}
   expected[i]=(float)Math.Clamp((mic[i]*micGain+music[i]*gameGain*Math.Pow(10,automation/20))*master,-ceiling,ceiling);
   error+=Math.Abs(expected[i]-actual[i]);
  }
  double best=-1;int lag=0;for(int shift=-64;shift<=64;shift++){
   double xy=0,xx=0,yy=0;for(int i=128;i<actual.Length-128;i+=2){double x=expected[i],y=actual[i+shift*2];xy+=x*y;xx+=x*x;yy+=y*y;}
   double correlation=xy/Math.Sqrt(xx*yy);if(correlation>best){best=correlation;lag=shift;}
  }return new[]{error/actual.Length,best,(double)lag};
 }
}
'@
$results=[Collections.Generic.List[object]]::new();$clock=[decimal]254016000000
foreach($index in @(0,3,9,15)){
 $mic=$microphone[$index];$game=$music[$index];$clip=$mic.clip
 $globalSample=[long]([decimal]$clip.start/$clock*48000)+96000
 $sourceSample=[long]([decimal]$clip.sourceIn.offsetTicks/$clock*48000)+96000
 $noise=$clip.properties.audio.noiseReductionDb
 $a=Decode @('-v','error','-i',$source,'-map','0:1','-af',"aformat=sample_fmts=flt:sample_rates=48000:channel_layouts=stereo,afftdn=nr=${noise}:nf=-45:tn=0,atrim=start_sample=$($sourceSample+1200):end_sample=$($sourceSample+1200+9600)",'-ac','2','-ar','48000','-f','f32le','pipe:1')
 $b=Decode @('-v','error','-i',$source,'-map','0:2','-af',"atrim=start_sample=${sourceSample}:end_sample=$($sourceSample+9600)",'-ac','2','-ar','48000','-f','f32le','pipe:1')
 $micSettings=$settings.audio|Where-Object trackId -eq $mic.trackId;$gameSettings=$settings.audio|Where-Object trackId -eq $game.trackId
 $keys=@($gameSettings.envelope|ForEach-Object {[double]([decimal]$_.time.ticks/$clock)});$db=@($gameSettings.envelope.gainDb)
 foreach($profile in @($settings.profiles|Where-Object {$_.name -eq 'long-form' -or [decimal]$_.start.ticks -eq [decimal]$clip.start})){
  $outputSample=$globalSample-[long]([decimal]$profile.start.ticks/$clock*48000)
  $actual=Decode @('-v','error','-i',(Join-Path $outputs ($profile.name+'.mp4')),'-af',"atrim=start_sample=${outputSample}:end_sample=$($outputSample+9600)",'-ac','2','-ar','48000','-f','f32le','pipe:1')
  $metrics=[CreatorAudioReference]::Compare($a,$b,$actual,[Math]::Pow(10,$micSettings.gainDb/20),[Math]::Pow(10,$gameSettings.gainDb/20),[Math]::Pow(10,$settings.masterGainDb/20),[Math]::Pow(10,$settings.peakCeilingDb/20),$globalSample/48000.0,$keys,$db)
  $results.Add([ordered]@{profile=$profile.name;segment=$index;timelineSample=$globalSample;sourceSample=$sourceSample;meanAbsolutePcmError=$metrics[0];correlation=$metrics[1];offsetSamples=$metrics[2]})
  if([Math]::Abs($metrics[2]) -gt 2 -or $metrics[1] -lt .97 -or $metrics[0] -gt .02){throw "Independent audio alignment/mix mismatch: $($results[$results.Count-1]|ConvertTo-Json -Compress)"}
 }
}
$loudness=(& ffmpeg -hide_banner -nostats -i (Join-Path $outputs 'long-form.mp4') -af loudnorm=I=-16:TP=-1:LRA=11:print_format=json -f null NUL 2>&1|Out-String)
if($LASTEXITCODE -ne 0){throw 'Export loudness probe failed'}
$loudness|Set-Content (Join-Path $directoryPath 'export-loudness.log')
[ordered]@{outcome='passed';checks=$results;reference='Independent full-source sample-count trim, native FFmpeg afftdn with measured two-hop latency compensation, explicit plan gain/envelope mixing, separate AAC output decode';limitations='Four source windows compared to long-form and three Shorts (seven 200ms output checks), not continuous acoustic/display measurement; AAC is lossy'}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $directoryPath 'independent-audio.json')
