param([string]$Directory = 'artifacts/local/m1b-smoke', [string]$SourcePath = '')
$ErrorActionPreference = 'Stop'
$directoryPath = (Resolve-Path $Directory).Path
$exportPath = Join-Path $directoryPath 'edited.mp4'
$sourcePath = if ($SourcePath.Length -eq 0) {
    Join-Path (Split-Path $directoryPath -Parent) 'fixtures/av.mp4'
} else {
    (Resolve-Path -LiteralPath $SourcePath).Path
}
function Decode([string]$mediaPath, [double]$seconds, [bool]$audio) {
    $start = [Diagnostics.ProcessStartInfo]::new('ffmpeg')
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $decodeArguments = @('-v','error','-ss',$seconds.ToString([Globalization.CultureInfo]::InvariantCulture),'-i',$mediaPath)
    if ($audio) { $decodeArguments += @('-t','0.2','-vn','-ac','1','-ar','48000','-f','f32le','pipe:1') }
    else { $decodeArguments += @('-frames:v','1','-vf','scale=32:18:flags=area','-pix_fmt','rgb24','-f','rawvideo','pipe:1') }
    foreach ($argument in $decodeArguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $errors = $process.StandardError.ReadToEndAsync()
    $memory = [IO.MemoryStream]::new()
    try {
        $process.StandardOutput.BaseStream.CopyTo($memory)
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw 'Independent decode timed out.' }
        if ($process.ExitCode -ne 0) { throw $errors.GetAwaiter().GetResult() }
        return ,$memory.ToArray()
    } finally { $memory.Dispose(); $process.Dispose() }
}
function Rms([byte[]]$bytes) {
    $sum = 0.0
    for ($index = 0; $index -lt $bytes.Length; $index += 4) { $sample = [BitConverter]::ToSingle($bytes,$index); $sum += $sample*$sample }
    return [Math]::Sqrt($sum / ($bytes.Length/4))
}
function Error([byte[]]$left,[byte[]]$right) {
    if ($left.Length -ne $right.Length -or $left.Length -eq 0) { throw 'Decoded frame sizes differ.' }
    $total = 0.0
    for ($index = 0; $index -lt $left.Length; $index++) { $total += [Math]::Abs([int]$left[$index]-[int]$right[$index]) }
    return $total/$left.Length
}
$gap = Decode $exportPath 3 $false
if (($gap | Measure-Object -Maximum).Maximum -gt 2) { throw 'Video gap is not black.' }
$earlyError = Error (Decode $exportPath 1 $false) (Decode $sourcePath 1 $false)
$afterCutError = Error (Decode $exportPath 5 $false) (Decode $sourcePath 3 $false)
if ($earlyError -gt 8 -or $afterCutError -gt 8) { throw 'Edited source selections differ from independent source decode.' }
$blue = Decode $exportPath 9 $false
$blueMean = 0.0
for ($index=2; $index -lt $blue.Length; $index+=3) { $blueMean += $blue[$index] }
$blueMean /= ($blue.Length/3)
if ($blueMean -lt 240) { throw 'Second source is absent/out of order.' }
$silenceRms = Rms (Decode $exportPath 3 $true)
$signalRms = Rms (Decode $exportPath 5 $true)
if ($silenceRms -gt .001 -or $signalRms -lt .03) { throw 'Edited audio signal/gap is incorrect.' }
$probe = (& ffprobe -v error -show_entries format=duration:stream=codec_name,width,height,r_frame_rate,sample_rate -of json $exportPath | ConvertFrom-Json)
# The linked-clip trim now trims audio and video together. Compare against the source document duration
# recorded before export; old smoke reports (before linked trimming) describe a ten-second document.
$smoke = Get-Content (Join-Path $directoryPath 'result.json') -Raw | ConvertFrom-Json
$expectedDuration = if ($null -ne $smoke.exportDurationSeconds) { [double]$smoke.exportDurationSeconds } else { 10.0 }
if ($LASTEXITCODE -ne 0 -or [Math]::Abs([double]$probe.format.duration-$expectedDuration) -gt .04) { throw 'Export duration is incorrect.' }
if (-not ($probe.streams.codec_name -contains 'h264') -or -not ($probe.streams.codec_name -contains 'aac')) { throw 'Required codecs absent.' }
[ordered]@{outcome='passed';decoder='independent FFmpeg CLI';probe=$probe;earlySourceFrameError=$earlyError;postCutSourceFrameError=$afterCutError;gapVideoMaximum=($gap|Measure-Object -Maximum).Maximum;secondSourceBlueMean=$blueMean;gapAudioRms=$silenceRms;signalAudioRms=$signalRms} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $directoryPath 'independent-verification.json')
Get-Content (Join-Path $directoryPath 'independent-verification.json')
