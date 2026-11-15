type CopelandProfile = "Dotnet";
using Valencia.Scripting;

// Trusted local code. Writes new files; the input project and media remain unchanged.
function run(api: ScriptApi): string {
    const asset = api.FirstAsset();
    const sequence = api.FirstSequence();
    const job = api.Transcribe(asset, api.FirstAudioStream(asset), "tiny.en");
    const key = api.WaitAnalysis(job);
    const matches = api.TranscriptSearch(key, "important announcement");
    const plan = api.TextEditPlan(sequence, key, "important announcement", "retain");
    api.ValidatePlan(plan);
    api.ApplyPlan(plan);
    api.Undo();
    api.Redo();
    const saved: string = api.ProjectFile() + ".text.valencia";
    api.Save(saved);
    api.OpenProject(saved);
    api.WaitExport(api.Export(sequence, saved + ".mp4"));
    return matches;
}
