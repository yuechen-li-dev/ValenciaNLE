$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
Push-Location $repository
try {
    function ReadJson([string]$path) {
        Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    }
    function Distribution($values) {
        $sorted = @($values | ForEach-Object { [double]$_ } | Sort-Object)
        $count = $sorted.Count
        $median = if ($count % 2 -eq 0) {
            ($sorted[$count / 2 - 1] + $sorted[$count / 2]) / 2
        } else {
            $sorted[[int][Math]::Floor($count / 2)]
        }
        [ordered]@{
            count = $count
            medianMs = $median
            p95Ms = $sorted[[Math]::Ceiling(.95 * $count) - 1]
            minimumMs = $sorted[0]
            maximumMs = $sorted[-1]
        }
    }
    $root = 'artifacts/local/int64-audio'
    $before = ReadJson "$root/before-audio/audio-seeks.json"
    $after = ReadJson "$root/measured-audio/audio-seeks.json"
    $historical = ReadJson 'artifacts/local/m2e/qualification-long/headless.json'
    $full = ReadJson "$root/measured-full/headless.json"
    if (($before.measurements | ForEach-Object { "$($_.pass):$($_.second)" }) -join ',' -cne
        (($after.measurements | ForEach-Object { "$($_.pass):$($_.second)" }) -join ',')) {
        throw 'Matched audio request order differs.'
    }
    if (($historical.randomSeeks.requestedTicks -join ',') -cne ($full.randomSeeks.requestedTicks -join ',')) {
        throw 'Historical/current 40-request workloads differ.'
    }
    $tests = @()
    $testPaths = @('Valencia.Core.Tests', 'Valencia.Persistence.Tests', 'Valencia.Tests', 'Valencia.Programming.Tests') |
        ForEach-Object { "$root/tests/$_.trx" }
    $testPaths += '../Copeland/tests/Copeland/Copeland.TS.Tests/TestResults/int64-regression.trx'
    $testPaths += '../Copeland/tests/Copeland/Copeland.TS.Backend.CSharp.Tests/TestResults/int64-backend.trx'
    foreach ($path in $testPaths) {
        [xml]$xml = Get-Content -LiteralPath $path
        $counter = $xml.TestRun.ResultSummary.Counters
        if ([int]$counter.failed -ne 0 -or [int]$counter.notExecuted -ne 0 -or [int]$counter.total -ne [int]$counter.passed) {
            throw "Test suite is not fully passing: $path"
        }
        $tests += [ordered]@{
            trx = $path
            total = [int]$counter.total
            passed = [int]$counter.passed
            failed = [int]$counter.failed
            skipped = [int]$counter.notExecuted
            sha256 = (Get-FileHash -LiteralPath $path).Hash
        }
    }
    $sourceFiles = @(
        'src/Valencia.Media/FfmpegDecoder.cs', 'src/Valencia.Media/SequenceEvaluator.cs',
        'src/Valencia.Scripting/ScriptApi.cs', 'tests/Valencia.Tests/CreatorMediaTests.cs',
        'tests/Valencia.Programming.Tests/EngineAndScriptTests.cs',
        'src/Valencia.App/PlayerWindow.cs', 'src/Valencia.App/SmokeScenario.cs',
        'src/Valencia.App/Editing/EditorSmoke.cs', 'samples/Valencia.Qualification/Program.cs',
        'examples/rough-cut.ts', 'scripts/VerifyEditorExport.ps1'
    )
    $sourceFiles += @(
        'Semantics/Types.cs', 'Semantics/Binder.cs', 'Semantics/StaticEvaluation.cs',
        'Syntax/SyntaxKind.cs', 'Syntax/SyntaxFacts.cs', 'Syntax/Lexer.cs',
        'Syntax/Parser.cs', 'Syntax/FlowExecutionProjection.cs', 'Lowering/MirLowerer.cs'
    ) | ForEach-Object { "../Copeland/src/Copeland/Copeland.TS/$_" }
    $sourceFiles += '../Copeland/src/Copeland/Copeland.TS.Backend.CSharp/CSharp/CSharpBackend.cs'
    $sourceFiles += '../Copeland/tests/Copeland/Copeland.TS.Tests/Int64InteropTests.cs'
    $sources = @($sourceFiles | ForEach-Object {
        [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $_).Hash }
    })
    $native = ReadJson "$root/native-editor-qualified/result.json"
    $peaks = ReadJson "$root/native-player-focused/av-skew-peaks.json"
    $data = [ordered]@{
        schemaVersion = 1
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        outcome = 'engine-fixes-verified; native-playback-qualification-failed'
        machine = [ordered]@{
            cpu = (Get-CimInstance Win32_Processor).Name
            os = (Get-CimInstance Win32_OperatingSystem).Version
            dotnetSdk = (& dotnet --version)
            gpu = 'NVIDIA GeForce RTX 3070'
            gpuDriver = '610.88'
            ffmpeg = '7.1.1-full_build-www.gyan.dev'
        }
        source = $after.asset
        sourceDurationSeconds = $full.durationSeconds
        sourceScope = '7200.037-second repeated controlled OBS video with continuous reencoded AAC; not two hours of unique human footage. Three 48 kHz stereo AAC streams, 1080p60 H264, millisecond Matroska clocks.'
        method = 'Median averages middle pair; p95 nearest rank. Six warm requests are a paired comparison, not a tail-latency qualification. Fresh processes, same request order, OS caches not flushed, no other task-owned tests/benchmarks during final measurements. No audio device in these CPU PCM measurements.'
        matched = [ordered]@{
            beforeWarm = Distribution ($before.measurements | Where-Object pass -eq 1).elapsedMs
            afterWarm = Distribution ($after.measurements | Where-Object pass -eq 1).elapsedMs
            before = $before
            after = $after
        }
        fortyRequests = [ordered]@{
            baseline = 'Historical M2e, same seed/positions/source/machine; not a fresh paired baseline'
            beforeAudio = Distribution $historical.randomSeeks.audioMs
            afterAudio = Distribution $full.randomSeeks.audioMs
            current = $full
        }
        tests = $tests
        cli = ReadJson 'artifacts/local/m2a-demo/result.json'
        native = [ordered]@{
            editor = $native
            editorAvQualified = ($native.maximumAvSkewSeconds -le .15)
            independentExport = ReadJson "$root/native-editor-qualified/independent-verification.json"
            m0 = 'Failed 150 ms skew limit, including unchanged direct-file decoder path and a foregrounded native window'
            m0Peaks = $peaks
            m0FrameCosts = ReadJson "$root/native-player-focused/frame-costs.json"
            focusCheck = 'computer-use sky.activate_window followed by screenshot of the actual playing native window; still failed'
        }
        sources = $sources
        limitations = @(
            'Exact progressive sample-count audio anchors remain process-local. Cold requests beyond the indexed high-water mark still decode/count intervening samples.'
            'General CLR mutable field access, Int64 bitwise operators and Int64 GPU/JavaScript emission are not added. Existing TSON numbers remain binary64; exact wire values remain canonical strings.'
            'Native GPU draw stalls remain unresolved. Desktop save/script/export actions work and export content verifies, but neither M0 nor editor realtime A/V timing is qualified on this run.'
            'No renderer workaround, driver setting change, larger playback queue, codec rewrite or weakening of A/V acceptance thresholds was introduced.'
        )
    }
    $data | ConvertTo-Json -Depth 60 | Set-Content -LiteralPath docs/validation/int64-audio-seek.json
    Write-Output 'docs/validation/int64-audio-seek.json'
} finally {
    Pop-Location
}
