type CopelandProfile = "Dotnet";
using Valencia.Scripting;

// Trusted native CLR execution. Analyze one selected microphone stream; keep aligned source tracks together.
function run(api: ScriptApi): string {
    const asset = api.FirstAsset();
    const audio = api.AnalyzeAudio(asset, api.FirstAudioStream(asset), -35, 800);
    const key = api.WaitAnalysis(audio);
    const scenes = api.AnalyzeScenes(asset, api.FirstVideoStream(asset));
    api.WaitAnalysis(scenes);
    const policy = '{"minimumSegmentMs":100,"minimumRemovalMs":500,"prePaddingMs":0,"postPaddingMs":0}';
    const plan = api.RoughCutPlan(api.FirstSequence(), key, policy);
    const preview = api.ValidatePlan(plan);
    api.ApplyPlan(plan);
    api.Undo();
    api.Redo();
    api.Save(api.ProjectFile() + ".roughcut.valencia");
    api.WaitExport(api.Export(api.FirstSequence(), api.ProjectFile() + ".mp4"));
    return preview;
}
