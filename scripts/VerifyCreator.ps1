param([string]$Directory='artifacts/local/m2d', [string]$SourcePath)
$ErrorActionPreference='Stop'
$directoryPath=(Resolve-Path $Directory).Path
$outputs=Join-Path $directoryPath 'outputs'
$source=if ($SourcePath) { (Resolve-Path $SourcePath).Path } else { Join-Path $directoryPath 'three-hour.mkv' }
$plan=Get-Content (Join-Path $outputs 'creator-plan.json') -Raw | ConvertFrom-Json
$settings=($plan.operations | Where-Object operation -eq 'creator').settings
$clips=@($plan.operations | Where-Object { $_.operation -eq 'insert' -and $_.clip.streamIndex -eq 0 } | ForEach-Object clip | Sort-Object { [long]$_.start })
$clock=[decimal]254016000000
function Number([decimal]$value) { $value.ToString('0.############',[Globalization.CultureInfo]::InvariantCulture) }
function RunBytes([string[]]$arguments) {
    $start=[Diagnostics.ProcessStartInfo]::new('ffmpeg');$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in $arguments){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start);$errors=$process.StandardError.ReadToEndAsync();$memory=[IO.MemoryStream]::new()
    try {
        $process.StandardOutput.BaseStream.CopyTo($memory)
        if(-not $process.WaitForExit(30000)){ $process.Kill($true);throw 'Independent decode timed out.' }
        if($process.ExitCode -ne 0){throw $errors.GetAwaiter().GetResult()}
        return ,$memory.ToArray()
    } finally {$memory.Dispose();$process.Dispose()}
}
Add-Type -TypeDefinition @'
using System;
public static class CreatorPixels {
 public static double Error(byte[] a,byte[] b,int width,int rows) {
  if(a.Length!=b.Length||a.Length==0)throw new Exception("Independent frame sizes differ.");
  double sum=0;int count=width*rows*3;for(int i=0;i<count;i++)sum+=Math.Abs(a[i]-b[i]);return sum/count;
 }
 public static int Stripe(byte[] pixels,int width,int scale) {
  int value=0;for(int bit=0;bit<16;bit++){int p=((3*scale)*width+(bit*10+5)*scale)*3;if(pixels[p]>128)value|=1<<bit;}return value;
 }
}
'@
$results=[Collections.Generic.List[object]]::new()
foreach($profile in $settings.profiles){
    $file=Join-Path $outputs ($profile.name+'.mp4')
    $probe=(& ffprobe -v error -show_entries format=duration:stream=codec_name,width,height,sample_rate -of json $file | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0){throw 'Independent probe failed.'}
    $expectedDuration=([decimal]$profile.end.ticks-[decimal]$profile.start.ticks)/$clock
    if([Math]::Abs([double]$probe.format.duration-[double]$expectedDuration) -gt .05){throw 'Output duration differs from its exact profile window.'}
    if($probe.streams.codec_name -notcontains 'h264' -or $probe.streams.codec_name -notcontains 'aac'){throw 'H264/AAC streams required.'}
    $video=$probe.streams|Where-Object codec_name -eq h264
    if($video.width -ne $profile.width -or $video.height -ne $profile.height){throw 'Output dimensions differ from profile.'}
    $times=[Collections.Generic.HashSet[long]]::new();[void]$times.Add(0)
    $frameCount=[long][Math]::Ceiling($expectedDuration*30)
    [void]$times.Add([Math]::Min(30,$frameCount-1));[void]$times.Add($frameCount-1)
    foreach($clip in $clips){
        $cut=([decimal]$clip.start-[decimal]$profile.start.ticks)/$clock*30
        foreach($delta in @(-1,0,1)){ $at=[long]$cut+$delta;if($at -ge 0 -and $at -lt $frameCount){[void]$times.Add($at)} }
    }
    foreach($frame in ($times|Sort-Object)){
        $outputTime=[decimal]$frame/30
        $sequenceTicks=[decimal]$profile.start.ticks+[decimal]$frame*($clock/30)
        $clip=$clips|Where-Object {
            # Source positions are authoritative; no nominal source FPS.
            $duration=([decimal]$_.sourceOut.value*$_.sourceOut.numerator/$_.sourceOut.denominator-[decimal]$_.sourceIn.value*$_.sourceIn.numerator/$_.sourceIn.denominator)*$clock+[decimal]$_.sourceOut.offsetTicks-[decimal]$_.sourceIn.offsetTicks
            [decimal]$_.start -le $sequenceTicks -and [decimal]$_.start+$duration -gt $sequenceTicks
        }|Select-Object -First 1
        if($null -eq $clip){throw 'Reference fixture unexpectedly contains a video gap.'}
        $target=[decimal]$clip.sourceIn.value*$clip.sourceIn.numerator/$clip.sourceIn.denominator+([decimal]$clip.sourceIn.offsetTicks+$sequenceTicks-[decimal]$clip.start)/$clock
        $begin=[Math]::Max(0,[double]$target-1)
        $interval=(Number ([decimal]$begin))+'%'+(Number ($target+([decimal]0.1)))
        $pts=(& ffprobe -v error -select_streams v:0 -read_intervals $interval -show_frames -show_entries frame=best_effort_timestamp -of json $source | ConvertFrom-Json).frames.best_effort_timestamp
        if($LASTEXITCODE -ne 0){throw 'Independent source PTS probe failed.'}
        $wanted=@($pts|Where-Object { [decimal]$_/1000 -le $target }|Sort-Object)[-1]
        if($null -eq $wanted){throw 'No preceding source frame in independent demux interval.'}
        $scale=if($profile.fit -eq 'fill'){"scale=-2:$($profile.height):flags=neighbor,crop=$($profile.width):$($profile.height)"}else{"scale=$($profile.width):$($profile.height):flags=neighbor"}
        $reference=RunBytes @('-v','error','-copyts','-ss',(Number ([decimal][Math]::Max(0,[double]$target-11))),'-i',$source,'-vf',"select=eq(pts\,$wanted),$scale",'-frames:v','1','-fps_mode','passthrough','-pix_fmt','rgb24','-f','rawvideo','pipe:1')
        $actual=RunBytes @('-v','error','-ss',(Number ([Math]::Max([decimal]0,$outputTime-([decimal]0.00001)))),'-i',$file,'-frames:v','1','-pix_fmt','rgb24','-f','rawvideo','pipe:1')
        $error=[CreatorPixels]::Error($actual,$reference,$profile.width,[int]($profile.height*.60))
        Write-Output "$($profile.name) frame=$frame target=$target pts=$wanted mae=$error"
        if($error -gt 14){throw "Independent source-selection mismatch: $($profile.name) frame=$frame sourcePts=$wanted mae=$error"}
        $stripe=$null
        if($profile.name -eq 'long-form'){
            $expectedId=[CreatorPixels]::Stripe($reference,$profile.width,2);$stripe=[CreatorPixels]::Stripe($actual,$profile.width,2)
            if($stripe -ne $expectedId){throw "Independent visible frame ID differs: output=$stripe source=$expectedId"}
        }
        $results.Add([ordered]@{profile=$profile.name;outputFrame=$frame;sourcePts=$wanted;sourceTimebase='1/1000';rgbMaeTop60Percent=$error;visibleFrameId=$stripe})
    }
}
[ordered]@{outcome='passed';reference='Independent FFprobe actual PTS lookup and FFmpeg decode of source and outputs; plan placements interpreted outside Valencia';frameChecks=$results;scope='All long-form cuts before/at/after plus start/end and three vertical output windows; captions excluded from source-pixel comparison'} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $directoryPath 'independent-video.json')
Write-Output (Join-Path $directoryPath 'independent-video.json')



