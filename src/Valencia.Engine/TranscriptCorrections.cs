using System.Collections.Immutable;

namespace Valencia.Engine;

public sealed record TranscriptCorrection(Guid WordId, string Text);
public sealed record TranscriptCorrections(string Key, ImmutableArray<TranscriptCorrection> Edits);
