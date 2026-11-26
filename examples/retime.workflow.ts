using Valencia.Scripting;

function run(api: ScriptApi): string {
    const sequence = api.SelectedSequence();
    const track = api.FirstTrack(sequence, "video");
    const first = api.FirstClipOnTrack(sequence, track);
    const second = api.Argument("freezeClip", "0abf107c-7073-4ab0-980a-45b11ac22fda");
    const dissolve = api.Argument("dissolve", "8dad2905-5078-4508-abd2-acf0225c07e6");
    const plan = api.Plan(sequence);
    plan.ClearTransition(track, dissolve);
    plan.Speed(first, 2, 1);
    plan.Freeze(second, api.Frames(230L, 30, 1), api.Frames(210L, 30, 1));
    return api.Apply(plan);
}
