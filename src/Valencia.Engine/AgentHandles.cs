using Valencia.Core.Timeline;

namespace Valencia.Engine;

/// <summary>Reversible project-scoped aliases; no registry, truncated hash, mutation or migration is needed.</summary>
public static class AgentHandles
{
    private static string Prefix(string kind) => kind switch
    {
        "sequence" => "S-", "asset" => "A-", "track" => "T-", "clip" => "C-", "marker" => "M-",
        "candidate" => "P-", "profile" => "O-", _ => throw new ArgumentException("Unknown handle kind.")
    };

    public static string For(string kind, Guid id) => Prefix(kind) +
        Convert.ToBase64String(id.ToByteArray(bigEndian: true)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static Guid Resolve(string reference, string kind, IEnumerable<Guid> scope)
    {
        if (TryDecode(reference, kind, out var id) && scope.Contains(id)) return id;
        throw new AgentRequestException(new("UNKNOWN_HANDLE", $"Unknown {kind} reference '{reference}' in this scope.",
            "List objects in the intended project/sequence and copy the handle exactly (case-sensitive).", reference));
    }

    private static bool TryDecode(string reference, string kind, out Guid id)
    {
        if (Guid.TryParseExact(reference, "D", out id)) return true;
        if (reference.Length != 24 || !reference.StartsWith(Prefix(kind), StringComparison.Ordinal)) return false;
        Span<byte> bytes = stackalloc byte[16];
        if (!Convert.TryFromBase64Chars((reference[2..].Replace('-', '+').Replace('_', '/') + "==").AsSpan(), bytes, out int length)
            || length != 16) return false;
        id = new(bytes, bigEndian: true);
        return For(kind, id) == reference;
    }

    public static SequenceDocument Sequence(ProjectDocument document, string reference) =>
        document.Sequences.First(s => s.Id == Resolve(reference, "sequence", document.Sequences.Select(s => s.Id)));
    public static MediaAsset Asset(ProjectDocument document, string reference) =>
        document.Assets.First(a => a.Id == Resolve(reference, "asset", document.Assets.Select(a => a.Id)));
}
