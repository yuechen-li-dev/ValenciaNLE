using Valencia.Scripting;

function run(api: ScriptApi): string {
    const name = api.Argument("name", "Vertical assembly");
    const asset = api.Argument("asset", api.FirstAsset());
    const sequence = api.CreateSequence(name, 1080, 1920, 30, 1, 48000);
    api.InsertAsset(asset, sequence, 0L);
    api.AddMarker(sequence, 0L, "Review opening");
    return sequence;
}
