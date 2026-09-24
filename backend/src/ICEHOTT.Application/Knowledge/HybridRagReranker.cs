using System.Text.RegularExpressions;
using ICEHOTT.Application.Abstractions;

namespace ICEHOTT.Application.Knowledge;

public sealed partial class HybridRagReranker : IRagReranker
{
    public IReadOnlyList<KnowledgeMatch> Rerank(
        string query,
        IReadOnlyList<KnowledgeMatch> candidates,
        int limit)
    {
        var queryTokens = Tokenize(query);
        var normalizedQuery = Normalize(query);

        var ranked = candidates
            .GroupBy(x => x.ChunkId)
            .Select(group => group.First())
            .Select(match =>
            {
                var contentTokens = Tokenize(match.Content);
                var coverage = queryTokens.Count == 0
                    ? 0d
                    : queryTokens.Count(contentTokens.Contains) / (double)queryTokens.Count;
                var phraseBoost = !string.IsNullOrWhiteSpace(normalizedQuery) &&
                                  Normalize(match.Content).Contains(normalizedQuery, StringComparison.Ordinal)
                    ? 1d
                    : 0d;
                var score = Math.Clamp(
                    match.Score * 0.75 + coverage * 0.20 + phraseBoost * 0.05,
                    0d,
                    1d);

                return match with { Score = score };
            })
            .OrderByDescending(x => x.Score)
            .ToArray();

        var selected = new List<KnowledgeMatch>(Math.Clamp(limit, 1, 10));
        var perDocument = new Dictionary<Guid, int>();

        foreach (var match in ranked)
        {
            perDocument.TryGetValue(match.DocumentId, out var count);
            if (count >= 2) continue;

            selected.Add(match);
            perDocument[match.DocumentId] = count + 1;
            if (selected.Count >= limit) return selected;
        }

        foreach (var match in ranked)
        {
            if (selected.Any(x => x.ChunkId == match.ChunkId)) continue;
            selected.Add(match);
            if (selected.Count >= limit) break;
        }

        return selected;
    }

    private static HashSet<string> Tokenize(string text) =>
        WordRegex().Matches(Normalize(text))
            .Select(match => match.Value)
            .Where(token => token.Length >= 2)
            .ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string text) => text.Trim().ToLowerInvariant();

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
