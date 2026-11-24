using Valencia.Scripting;

function run(api: ScriptApi): string {
    const sequence = api.SelectedSequence();
    const plan = api.Plan(sequence);
    const clip = api.FirstClipOnTrack(sequence, api.FirstTrack(sequence, "video"));
    const title = "5c8aeb42-39a9-4714-8437-d5de1175b618";
    const timing = api.Argument("timing", "early");
    let titleY = 0.30;
    let hit = 99L;
    if (timing == "early") {
        titleY = 0.075;
        hit = 97L;
    }

    const camera = plan.Keyframes();
    camera.Key(0L, 0.5, 0.5, 1.0, 0.0, 1.0, "hold");
    camera.Key(api.Frames(hit, 30, 1), 0.5, 0.51, 1.24, -4.0, 1.0, "ease");
    camera.Key(api.Frames(118L, 30, 1), 0.5, 0.5, 1.0, 0.0, 1.0, "linear");
    plan.ClipKeys(clip, camera);

    plan.StyledTitle(title, "BIRD CAM", api.Frames(93L, 30, 1), api.Frames(154L, 30, 1),
        "static", 0L, 0L, 0.5, titleY, 0.11, 4293071615L, 336860415L);
    const lettering = plan.Keyframes();
    lettering.Key(0L, 0.5, titleY + 0.035, 0.72, -10.0, 0.0, "ease");
    lettering.Key(api.Frames(12L, 30, 1), 0.5, titleY, 1.04, 1.5, 1.0, "ease");
    lettering.Key(api.Frames(22L, 30, 1), 0.5, titleY, 1.0, 0.0, 1.0, "hold");
    lettering.Key(api.Frames(52L, 30, 1), 0.5, titleY, 1.0, 0.0, 1.0, "linear");
    lettering.Key(api.Frames(61L, 30, 1), 0.5, titleY, 0.95, 0.0, 0.0, "linear");
    plan.TitleKeys(title, lettering);
    return api.Apply(plan);
}
