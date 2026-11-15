$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
Push-Location $repository
try {
    function ReadJson([string]$path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
    function Distribution($values) {
        $sorted = @($values | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ } | Sort-Object)
        $count = $sorted.Count
        if ($count -eq 0) { return $null }
        $median = if ($count % 2 -eq 0) { ($sorted[$count/2-1]+$sorted[$count/2])/2 } else { $sorted[[int][Math]::Floor($count/2)] }
        [ordered]@{ count=$count; p50=$median; p95=$sorted[[Math]::Ceiling(.95*$count)-1];
            p99=$sorted[[Math]::Ceiling(.99*$count)-1]; maximum=$sorted[-1] }
    }
    function TraceSummary([string]$path) {
        $trace = ReadJson $path
        $frames = @($trace.frames | Select-Object -Skip 10)
        $gpu = @($frames | ForEach-Object {
            $total = 0.0
            foreach ($pass in $_.passes) {
                if ($pass.GpuInterval) {
                    $total += ([decimal]$pass.GpuInterval.EndTicks-[decimal]$pass.GpuInterval.StartTicks)*$pass.GpuInterval.NanosecondsPerTick/1000000
                }
            }
            $total
        })
        $phases = [ordered]@{}
        $names = @($frames | ForEach-Object { $_.cpu.PSObject.Properties.Name } | Sort-Object -Unique)
        foreach ($name in $names) { $phases[$name] = Distribution ($frames | ForEach-Object { $_.cpu.$name }) }
        [ordered]@{
            raw=$path; validation=$trace.validation; observed=$trace.observedFrames; dropped=$trace.droppedTraceFrames
            warmupFramesExcluded=10; drawMs=(Distribution $frames.drawMs); loopCadenceMs=(Distribution $frames.cadenceMs)
            gpuRenderIntervalsMs=(Distribution $gpu); cpuPhasesMs=$phases
            acquireAndImageFenceMs=(Distribution $frames.presentation.AcquireMilliseconds)
            copyRecordSubmitWaitMs=(Distribution $frames.presentation.CopyMilliseconds)
            presentCallMs=(Distribution $frames.presentation.PresentMilliseconds)
            uploadStagingMs=(Distribution $frames.upload.StagingMilliseconds)
            uploadFenceWaitMs=(Distribution $frames.upload.FenceWaitMilliseconds)
            allFrameDrawMaximumMs=($trace.frames.drawMs | Measure-Object -Maximum).Maximum
            descriptorAllocationsAfterWarmup=($frames.passes.Metrics.DescriptorSetAllocations | Measure-Object -Sum).Sum
            processPeakSampledWorkingSetBytes=($trace.frames.workingSetBytes | Measure-Object -Maximum).Maximum
            gcPauseMilliseconds=$trace.frames[-1].totalGcPauseMs
        }
    }
    $root = 'artifacts/local/render-stabilization'
    $runs = @(ReadJson "$root/qualified/runs.json")
    if (@($runs | Where-Object exitCode -ne 0).Count) { throw 'Native qualification is not completely passing.' }
    $native = @()
    foreach ($run in $runs) {
        $directory = "$root/qualified/$($run.name)"
        $resultPath = if ($run.name -like 'm0-*') { "$directory/smoke.json" } else { "$directory/result.json" }
        $trace = if (Test-Path "$directory/trace.json") { TraceSummary "$directory/trace.json" } else { $null }
        if ($trace -and $trace.validation.errors -ne 0) { throw "Vulkan errors: $directory" }
        $native += [ordered]@{ run=$run; result=(ReadJson $resultPath); trace=$trace }
    }
    $testPaths = @('Valencia.Core.Tests','Valencia.Persistence.Tests','Valencia.Tests','Valencia.Programming.Tests') |
        ForEach-Object { "$root/final-tests/$_.trx" }
    $testPaths += '../Copeland/tests/Aurelian/Aurelian.Graphics.Tests/TestResults/render-stabilization.trx'
    $testPaths += '../Copeland/tests/Copeland/Copeland.TS.Tests/TestResults/render-regression.trx'
    $testPaths += '../Copeland/tests/Copeland/Copeland.TS.Backend.CSharp.Tests/TestResults/render-regression.trx'
    $tests = @($testPaths | ForEach-Object {
        [xml]$xml = Get-Content -LiteralPath $_
        $c = $xml.TestRun.ResultSummary.Counters
        if ([int]$c.total -ne [int]$c.passed -or [int]$c.failed -ne 0 -or [int]$c.notExecuted -ne 0) { throw "Tests failed: $_" }
        [ordered]@{ path=$_; total=[int]$c.total; passed=[int]$c.passed; failed=[int]$c.failed; skipped=[int]$c.notExecuted;
            sha256=(Get-FileHash -LiteralPath $_).Hash }
    })
    $control = ReadJson "$root/paired-paired-control/result.json"
    $fixed = ReadJson "$root/paired-qualified/result.json"
    $comparison = @('Valencia.App.dll','Valencia.Rendering.dll','Aurelian.Graphics.dll','Valencia.Media.dll') | ForEach-Object {
        [ordered]@{ file=$_; control=(Get-FileHash -LiteralPath "$root/paired-control-host/$_").Hash;
            fixed=(Get-FileHash -LiteralPath "$root/qualified-host/$_").Hash }
    }
    $sourceFiles = @('src/Valencia.Media/DecodeSession.cs','src/Valencia.Media/PlaybackSession.cs',
        'src/Valencia.Media/SequencePlayback.cs','src/Valencia.Media/PreviewFrameClock.cs',
        'src/Valencia.Media/FfmpegDecoder.cs','src/Valencia.Media/SequenceEvaluator.cs','src/Valencia.Media/DecodedFrames.cs',
        'src/Valencia.Rendering/NativeVideoPresenter.cs','src/Valencia.Rendering/NativeRenderTrace.cs',
        'src/Valencia.App/RenderProbe.cs','src/Valencia.App/PlaybackQualification.cs',
        'src/Valencia.App/PlayerWindow.cs','src/Valencia.App/SmokeScenario.cs',
        'src/Valencia.App/Editing/EditorWindow.cs','src/Valencia.App/Editing/QualificationSmoke.cs',
        'src/Valencia.App/Editing/CreatorSmoke.cs','tests/Valencia.Tests/MediaTests.cs',
        'tests/Valencia.Tests/PlaybackQualificationTests.cs','tests/Valencia.Tests/TimelineIntegrationTests.cs')
    $sourceFiles += @('Native2D/Native2DContracts.cs','Native2D/VulkanOrderedQuadRenderer.cs',
        'Native2D/VulkanNativeSwapchainPresenter.cs','Native2D/NativePresentTimings.cs',
        'Resources/Uploads/VulkanTextureUploader.cs','Resources/Uploads/VulkanTextureUploadResult.cs',
        'Diagnostics/VulkanValidationCapture.cs') | ForEach-Object { "../Copeland/src/Aurelian/Aurelian.Graphics/Vulkan/$_" }
    $sources = @($sourceFiles | ForEach-Object { [ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash} })
    $data = [ordered]@{
        schemaVersion=1; generatedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
        outcome='verified AV queue/shutdown corrections; historical 250 ms draw cause unconfirmed'
        machine=[ordered]@{ cpu=(Get-CimInstance Win32_Processor).Name; os=(Get-CimInstance Win32_OperatingSystem).Version;
            sdk=(& dotnet --version); gpu='NVIDIA GeForce RTX 3070'; driver='610.88'; ffmpeg='7.1.1'; runtime='10.0.15' }
        thresholdSeconds=.15
        histogramMethod='Median averages middle pair; p95/p99 nearest rank. First ten render frames excluded from steady-state distributions; all-frame maximum also retained. GPU intervals are raw timestamp differences, not CPU submit time or calibrated display latency.'
        historicalEvidence='docs/validation/int64-audio-seek.json; no fine-grained GPU trace was captured for historical 250 ms host Draw stalls'
        currentFailingBaseline=[ordered]@{
            av=(ReadJson "$root/final/comparison-av/result.json"); avTrace=(TraceSummary "$root/final/comparison-av/trace.json")
            edited=(ReadJson "$root/final/qualification-1080/result.json"); editedTrace=(TraceSummary "$root/final/qualification-1080/trace.json")
        }
        counterfactual=[ordered]@{ injection='Explicit 700 ms UI sleep every sixty probe iterations, not a claimed GPU/driver reproduction';
            control=$control; fixed=$fixed; binaryHashes=$comparison;
            explanation='Same application, rendering and Aurelian assemblies; only Valencia.Media.dll replaced with this-turn pre-fix source-built snapshot.' }
        native=$native; tests=$tests; sources=$sources
        independentExport=(ReadJson "$root/qualified/editor/independent-verification.json")
        limitations=@(
            'Historical recurring 250 ms GPU Draw failure did not reproduce. Do not claim a driver/fence root cause or a GPU throughput fix.'
            'Current reproducible AV failure was obsolete FIFO preview backpressure after a UI stall; clock-paced bounded publication corrects it.'
            'A paused-generation UI-context shutdown dependency was exposed and corrected using ConfigureAwait(false); process exit is required for success.'
            'Native results include actual play/pause/seek/resize, edited cuts/gaps, captions/aspects, fresh concurrent proxy work and exports. These are automated native actions, not physical input/usability or multi-hour drift qualification.'
            'Default Khronos validation plus debug-utils callback capture is active for traced runs. Startup device creation precedes capture; forced device loss and synchronization-validation-specific features were not tested.'
            'Swapchain acquire and acquire-fence CPU time are combined. Presentation-copy record/submit/wait is combined. No GPU timestamps for the display engine or calibrated first-photon/acoustic latency.'
            'Pacing/driver waits remain synchronous. No async upload ring, frames-in-flight change, weaker synchronization or larger queue was introduced.'
            'Pre-fix final baseline overlaps regression-test CPU load for some comparisons. Early unloaded comparisons also exist. Timing changes are not all causally attributable to the fix.'
            'One intermediate rebuild overlapped a running source-bin application; those trials are invalid. A separate paced editor run exposed a shutdown hang after writing a passed result; it was terminated, excluded, and rerun after correction.'
        )
    }
    $data | ConvertTo-Json -Depth 70 | Set-Content docs/validation/m2e-render-stabilization.json
    Write-Output 'docs/validation/m2e-render-stabilization.json'
} finally { Pop-Location }
