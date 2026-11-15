type CopelandProfile = "Dotnet";
using Valencia.Scripting;

// Trusted native script. The matching .creator.obj.ts file is validated by Copeland's type profile.
// Analyze first (StartAnalysis/WaitAnalysis, TranscribeAligned), or reference completed source artifacts in that file.
function run(api: ScriptApi): string {
    const sourceProject = api.ProjectFile();
    const request: string = sourceProject + ".creator.obj.ts";
    const job = api.StartCreatorFromFile(request);
    const key = api.WaitCreator(job);
    const summary = api.CreatorSummary(key);
    api.ApplyCreator(key);
    api.Undo();
    api.Redo();
    const destination: string = sourceProject + ".creator.valencia";
    api.Save(destination);
    api.OpenProject(destination);
    api.ExportOutputs(api.FirstSequence(), sourceProject + ".outputs");
    return summary;
}
