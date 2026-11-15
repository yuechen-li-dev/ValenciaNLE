using Valencia.Scripting;

// Trusted local script. Facts/cache inspection only: no timeline commit or save.
function run(api: ScriptApi): string {
    const asset = api.FirstAsset();
    const stream = api.FirstAudioStream(asset);
    const first = api.AnalyzeAudio(asset, stream, -35, 800);
    const firstKey = api.WaitAnalysis(first);
    const second = api.AnalyzeAudio(asset, stream, -35, 800);
    const secondKey = api.WaitAnalysis(second);
    return "[" + api.AnalysisStatus(first) + "," + api.AnalysisStatus(second) + "]";
}

