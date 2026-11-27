using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Analysis;

public sealed record ObservationBox(double X, double Y, double Width, double Height);
public sealed record SourceObservation(SourceRange Range, string Text, string Provider, string Version,
    string Configuration, string Evidence, double? Confidence = null, ObservationBox? Box = null, string? Track = null,
    ImmutableArray<string>? Keywords = null);

/// <summary>A lexical baseline over aligned transcript segments. Each split has a time or vocabulary reason.</summary>
public static partial class TranscriptTopics
{
    public const string Algorithm = "transcript-topics-lexical-v1";
    private static readonly HashSet<string> StopWords = new(
        "about after again also and are because been before being can could did does for from going had has have here how into just like more must not now of on one or our out said should some than that the their them then there these they this those through too under use very want was were what when where which who will with would you your".Split(),
        StringComparer.Ordinal);

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'-]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Terms();

    private sealed record Unit(SourceRange Range, string Text, string[] Terms);

    public static ImmutableArray<SourceObservation> Build(TranscriptData transcript, CancellationToken cancellation = default)
    {
        var units = new List<Unit>();
        foreach (var segment in transcript.Segments)
        {
            cancellation.ThrowIfCancellationRequested();
            if (segment.FirstWord < 0 || segment.WordCount <= 0 || segment.FirstWord > transcript.Words.Length - segment.WordCount)
                continue;
            var words = transcript.Words.AsSpan(segment.FirstWord, segment.WordCount).ToArray()
                .Where(w => w.TimingUsable && w.Range.Epoch == segment.Range.Epoch).ToArray();
            if (words.Length == 0) continue;
            var range = new SourceRange(words[0].Range.Start, words[^1].Range.End, words[0].Range.Epoch);
            if (range.End.CompareTo(range.Start) <= 0) continue;
            units.Add(new(range, segment.Text, Keywords(segment.Text).ToArray()));
        }
        var result = ImmutableArray.CreateBuilder<SourceObservation>();
        if (units.Count == 0) return [];
        var block = new List<Unit> { units[0] };
        string opening = "recording start";
        for (int i = 1; i < units.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var prior = units[i - 1];
            var current = units[i];
            string? boundary = null;
            if (current.Range.Epoch != prior.Range.Epoch) boundary = "timestamp epoch change";
            else
            {
                long gap = current.Range.Start.Since(prior.Range.End, TimeRounding.NearestAwayFromZero).Ticks;
                if (gap >= 4 * SequenceTime.TicksPerSecond) boundary = "speech gap ≥4s";
                else if (block.Count >= 2 && i + 1 < units.Count && units[i + 1].Range.Epoch == current.Range.Epoch)
                {
                    var left = block.TakeLast(3).SelectMany(x => x.Terms).ToHashSet(StringComparer.Ordinal);
                    var right = current.Terms.Concat(units[i + 1].Terms).ToHashSet(StringComparer.Ordinal);
                    double similarity = (double)left.Count(right.Contains) / Math.Max(1, left.Union(right).Count());
                    if (left.Count >= 5 && right.Count >= 5 && similarity <= .10) boundary = "low lexical overlap between neighbouring segments";
                }
            }
            if (boundary is not null)
            {
                AddBlock(block, opening);
                block.Clear();
                opening = boundary;
            }
            block.Add(current);
        }
        AddBlock(block, opening);
        return result.ToImmutable();

        void AddBlock(List<Unit> members, string reason)
        {
            if (result.Count >= 4096) throw new InvalidOperationException("Topic index exceeds 4096 segments; analyze a shorter source.");
            var terms = members.SelectMany(unit => unit.Terms).GroupBy(x => x).OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal).Take(8).Select(g => g.Key).ToImmutableArray();
            string excerpt = string.Join(' ', members.Take(3).Select(unit => unit.Text)).Trim();
            if (excerpt.Length > 220) excerpt = excerpt[..219] + "…";
            result.Add(new(new(members[0].Range.Start, members[^1].Range.End, members[0].Range.Epoch), excerpt,
                "Valencia", Algorithm, "gap=4s; lexical=jaccard<=0.10; lookaround=3/2 aligned segments",
                "heuristic topic boundary: " + reason, Keywords: terms));
        }
    }

    private static IEnumerable<string> Keywords(string text) => Terms().Matches(text.ToLowerInvariant())
        .Select(match => match.Value).Where(term => !StopWords.Contains(term));
}
