using Valencia.Scripting;

function relay<T>(value: T): T {
    return value;
}

enum ChapterInterval {
    Seconds(value: long),
    Invalid,
}

function interval(mode: string): ChapterInterval {
    if (mode == "short") { return ChapterInterval.Seconds(5L); }
    if (mode == "long") { return ChapterInterval.Seconds(30L); }
    return ChapterInterval.Invalid;
}

function run(api: ScriptApi): string {
    const mode = api.Argument("mode", "long");
    const seconds: long = match relay<ChapterInterval>(interval(mode)) {
        Seconds(value) => value,
        Invalid => 0L,
    };
    if (seconds == 0L) { return "Expected short or long"; }

    const sequence = api.SelectedSequence();
    const duration: long = api.DurationTicks(sequence);
    const step: long = api.Frames(seconds * 30L, 30, 1);
    const label = api.Argument("label", "Chapter");
    let count: int = 0;
    for (let at: long = 0L; at < duration && count < 64; at = api.AddTicks(at, step)) {
        api.Checkpoint();
        api.AddMarker(sequence, at, label + " " + String.From(count + 1));
        count = count + 1;
    }
    return "Created " + String.From(count) + " markers";
}
