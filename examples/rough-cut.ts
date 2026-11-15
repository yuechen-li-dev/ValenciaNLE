type CopelandProfile = "Dotnet";
using Valencia.Scripting;

// TRUSTED LOCAL CODE: opens its input again, then writes a NEW project and MP4.
// Requires at least four seconds in the first video/audio clip, starting at zero.
function run(api: ScriptApi): string {
    api.OpenProject(api.ProjectFile());
    const sequence = api.FirstSequence();
    const videoTrack = api.FirstTrack(sequence, "video");
    const audioTrack = api.FirstTrack(sequence, "audio");
    const rightVideo = api.NewId();
    const rightAudio = api.NewId();
    const plan = api.Plan(sequence);
    const cut: long = api.Frames(60L, 30, 1);
    const destination: long = api.Frames(90L, 30, 1);

    plan.Split(api.FirstClipOnTrack(sequence, videoTrack), cut, rightVideo);
    plan.Split(api.FirstClipOnTrack(sequence, audioTrack), cut, rightAudio);
    plan.Move(rightVideo, videoTrack, destination);
    plan.Move(rightAudio, audioTrack, destination);
    const preview = api.Validate(plan);
    api.Apply(plan);
    api.Undo();
    api.Redo();
    api.Save(api.ProjectFile() + ".edited.valencia");
    const job = api.Export(sequence, api.ProjectFile() + ".mp4");
    api.WaitExport(job);
    return preview;
}
