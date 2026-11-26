using Valencia.Scripting;

function run(api: ScriptApi): string {
    const sequence = api.SelectedSequence();
    const video = api.FirstTrack(sequence, "video");
    const plan = api.Plan(sequence);
    const title = plan.Keyframes();
    title.Key(0L, 0.5, 0.11, 0.72, -10.0, 0.0, "ease");
    title.Key(api.Frames(12L, 30, 1), 0.5, 0.075, 1.04, 1.5, 1.0, "ease");
    title.Key(api.Frames(22L, 30, 1), 0.62, 0.23, 1.0, 0.0, 1.0, "hold");
    title.Key(api.Frames(52L, 30, 1), 0.5, 0.075, 1.0, 0.0, 1.0, "linear");
    title.Key(api.Frames(61L, 30, 1), 0.5, 0.075, 0.95, 0.0, 0.0, "linear");
    plan.TitleKeys("5c8aeb42-39a9-4714-8437-d5de1175b618", title);

    plan.Transition(video, "8dad2905-5078-4508-abd2-acf0225c07e6",
        api.FirstClipOnTrack(sequence, video), "0abf107c-7073-4ab0-980a-45b11ac22fda",
        api.Frames(4L, 30, 1), api.Frames(8L, 30, 1));

    const gain = plan.GainEnvelope();
    gain.Point(0L, -18.0);
    gain.Point(api.Frames(22L, 30, 1), 0.0);
    gain.Point(api.Frames(93L, 30, 1), 0.0);
    gain.Point(api.Frames(97L, 30, 1), -12.0);
    gain.Point(api.Frames(112L, 30, 1), 0.0);
    gain.Point(api.Frames(146L, 30, 1), 0.0);
    gain.Point(api.Frames(155L, 30, 1), -9.0);
    gain.Point(api.Frames(166L, 30, 1), 0.0);
    gain.Point(api.Frames(326L, 30, 1), 0.0);
    gain.Point(api.Frames(360L, 30, 1), -24.0);
    plan.TrackEnvelope("01f6f34a-0d88-4bd1-b682-64af8937f455", gain);
    return api.Apply(plan);
}
