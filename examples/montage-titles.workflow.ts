using Valencia.Scripting;

function run(api: ScriptApi): string {
    const sequence = api.SelectedSequence();
    const plan = api.Plan(sequence);
    const video = api.FirstTrack(sequence, "video");
    const clip = api.FirstClipOnTrack(sequence, video);
    const reaction = api.Argument("reaction", "MAGNIFICENT!");
    const impactClip = "d9892941-2669-45fa-bb7a-474e89c8a32c";
    const impactStart = api.Frames(99L, 30, 1);

    // Stable IDs make the title cues editable by subsequent plans.
    plan.StyledTitle("92bd7cbb-1670-4fc1-bfd9-80a757940e1d", "BUNNY CAM", api.Frames(9L, 30, 1),
        api.Frames(68L, 30, 1), "slideUp", api.Frames(13L, 30, 1), api.Frames(8L, 30, 1), 0.5, 0.18, 0.12, 4294967295L, 336860415L);
    plan.StyledTitle("69087ed4-cff9-4244-958e-6a00cf66b561", reaction, impactStart,
        api.Frames(165L, 30, 1), "impact", api.Frames(16L, 30, 1), api.Frames(8L, 30, 1), 0.5, 0.32, 0.21, 4293071615L, 336860415L);
    plan.StyledTitle("ed688dae-bb30-4224-abfe-cc7abbfc49ba", "BIG BUCK ENERGY", api.Frames(224L, 30, 1),
        api.Frames(310L, 30, 1), "pop", api.Frames(16L, 30, 1), api.Frames(12L, 30, 1), 0.5, 0.74, 0.12, 1946157055L, 336860415L);
    plan.Split(clip, impactStart, impactClip);
    plan.ClipMotion(clip, "punchIn", api.Frames(24L, 30, 1), 0.8);
    plan.ClipMotion(impactClip, "shake", api.Frames(18L, 30, 1), 1.0);
    return api.Apply(plan);
}
