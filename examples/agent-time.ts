using Valencia.Scripting;

// Trusted local Copeland script; the CLR ABI returns an actual checked Int64.
function run(api: ScriptApi): string {
    const sequence = api.FirstSequence();
    const oneSecond = api.PositionTicks(sequence, "1s");
    const later = api.AddTicks(oneSecond, 9007199254740993L);
    return api.DescribePosition(sequence, later);
}
