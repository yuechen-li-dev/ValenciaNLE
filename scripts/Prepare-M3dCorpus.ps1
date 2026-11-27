[CmdletBinding()]
param(
    [string]$Output = 'artifacts/local/m3d/corpus',
    [string]$NarrationFixture = 'artifacts/local/m2d/three-hour.mkv',
    [string]$Ffmpeg = 'ffmpeg'
)
$ErrorActionPreference = 'Stop'
$narration = (Resolve-Path -LiteralPath $NarrationFixture).Path
$destination = (New-Item -ItemType Directory -Force $Output).FullName
function Encode([string[]]$Arguments) {
    & $Ffmpeg -hide_banner -v error -n @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Fixture generation failed for $($Arguments[-1])." }
}

$seed = Join-Path $destination 'seed-1080.mkv'
$loop = Join-Path $destination 'three-hours-1080p60.mkv'
foreach ($item in @(
    @{ Name=$seed; Dimensions='1920x1080'; Seconds=12 },
    @{ Name=(Join-Path $destination 'motion-1440p60.mp4'); Dimensions='2560x1440'; Seconds=36 }
)) {
    Encode @('-f','lavfi','-i',"testsrc2=size=$($item.Dimensions):rate=60:duration=$($item.Seconds)",
        '-f','lavfi','-i',"sine=frequency=440:sample_rate=48000:duration=$($item.Seconds)",
        '-f','lavfi','-i',"sine=frequency=330:sample_rate=48000:duration=$($item.Seconds)",
        '-map','0:v','-map','1:a','-map','2:a','-c:v','h264_nvenc','-preset','p4','-cq','26','-g','240',
        '-bf','3','-pix_fmt','yuv420p','-c:a','aac',$item.Name)
}
# The looped AAC is unsuitable for continuous playback because its priming repeats at each join.
# Retain the technical video fixture and repack it with the existing uninterrupted speech/audio tracks.
Encode @('-stream_loop','899','-i',$seed,'-map','0','-c','copy','-t','10800',$loop)
Encode @('-copyts','-i',$loop,'-i',$narration,'-map','0:v:0','-map','1:a:0','-map','1:a:1','-c','copy',
    '-t','10800','-avoid_negative_ts','disabled',(Join-Path $destination 'three-hours-1080p60-continuous.mkv'))
Encode @('-f','lavfi','-i','testsrc2=size=854x478:rate=30000/1001:duration=6','-vf','format=p010le',
    '-c:v','hevc_nvenc','-profile:v','main10','-g','60','-bf','3','-pix_fmt','p010le',
    '-color_range','tv','-colorspace','bt709','-color_primaries','bt709','-color_trc','bt709',
    (Join-Path $destination 'hevc-sdr10-2997.mp4'))
Encode @('-f','lavfi','-i','testsrc2=size=640x360:rate=30:duration=6','-vf',
    "select='not(mod(n,3))+not(mod(n,5))',scale=in_range=tv:out_range=pc,setpts=PTS+2/TB",
    '-fps_mode','vfr','-c:v','libaom-av1','-cpu-used','8','-row-mt','1','-crf','25',
    '-color_range','pc','-colorspace','bt709','-color_primaries','bt709','-color_trc','bt709','-g','30',
    (Join-Path $destination 'av1-vfr-offset.webm'))
Encode @('-f','lavfi','-i','testsrc=size=320x180:rate=30:duration=4','-vf','format=yuv444p',
    '-c:v','libx264','-profile:v','high444','-g','60',(Join-Path $destination 'h264-444.mp4'))
