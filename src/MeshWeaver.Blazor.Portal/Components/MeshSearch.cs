using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Blazor.Portal.Components;

/// <summary>
/// Composes the portal search box's suggestion stream from the unified mesh
/// query surface. Fully reactive — every branch returns an
/// <see cref="IObservable{T}"/> of the WHOLE current suggestion set
/// (<see cref="IReadOnlyList{T}"/> of <see cref="QuerySuggestion"/>), re-emitted
/// as each underlying source converges: <see cref="IMeshService.Query"/> /
/// <see cref="IMeshService.Autocomplete"/> seed each provider with
/// <c>.StartWith(empty)</c> and <c>CombineLatest</c>, so source B's matches show
/// before source A returns, then A+B re-ordered by score. The caller subscribes
/// once and binds the entire collection per emission — no channels, no
/// async-enumerable streaming, no per-item insertion.
/// </summary>
internal static class MeshSearch
{
    /// <summary>
    /// Fetch more candidates than displayed so relevance scoring can surface the
    /// best matches even if the DB returns them in a different order.
    /// </summary>
    private const int CandidatePoolSize = 50;

    /// <summary>
    /// Builds the live suggestion stream for <paramref name="input"/>:
    /// <list type="bullet">
    ///   <item>empty → recently-accessed nodes (most recent first)</item>
    ///   <item><c>@path</c> → path autocomplete</item>
    ///   <item>free text → substring candidate pool re-scored by match quality + proximity</item>
    /// </list>
    /// </summary>
    public static IObservable<IReadOnlyList<QuerySuggestion>> Suggestions(
        IMeshService meshService, string? input, string? contextPath, int maxResults)
    {
        var trimmed = input?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            var query = $"source:accessed scope:descendants is:main sort:LastModified-desc context:search limit:{maxResults}";
            return meshService.Query(new MeshQueryRequest { Query = query })
                .Select(rows => Project(rows, maxResults));
        }

        if (trimmed.StartsWith('@'))
        {
            var afterAt = trimmed[1..];
            var lastSlash = afterAt.LastIndexOf('/');
            var basePath = lastSlash >= 0 ? afterAt[..lastSlash] : "";
            var prefix = lastSlash >= 0 ? afterAt[(lastSlash + 1)..] : afterAt;
            return meshService
                .Autocomplete(basePath, prefix, AutocompleteMode.RelevanceFirst, maxResults, contextPath, context: "search")
                .Select(rows => Project(rows, maxResults));
        }

        var textQuery = $"*{trimmed}* scope:descendants context:search is:main limit:{CandidatePoolSize}";
        return meshService.Query(new MeshQueryRequest { Query = textQuery })
            .Select(rows => Score(rows, trimmed, contextPath, maxResults));
    }

    private static IReadOnlyList<QuerySuggestion> Project(IReadOnlyCollection<QueryResult> rows, int maxResults)
        => rows
            .Take(maxResults)
            .Select(r => new QuerySuggestion(r.Path, r.Name ?? r.Id ?? "", r.NodeType, r.Score, r.Icon))
            .ToList();

    private static IReadOnlyList<QuerySuggestion> Score(
        IReadOnlyCollection<QueryResult> rows, string input, string? contextPath, int maxResults)
        => rows
            .Select(r => new QuerySuggestion(
                r.Path, r.Name ?? r.Id ?? "", r.NodeType,
                ComputeRelevanceScore(r, input, contextPath), r.Icon))
            .OrderByDescending(s => s.Score)
            .Take(maxResults)
            .ToList();

    /// <summary>
    /// Scores a row by how well it matches the search input. Name matches score
    /// highest — this is the "goodness of match" measure the merged collection is
    /// ordered by. Mirrors the autocomplete scoring tiers, plus a proximity boost
    /// for rows near the caller's current context.
    /// </summary>
    private static double ComputeRelevanceScore(QueryResult node, string searchInput, string? contextPath)
    {
        var name = node.Name ?? "";
        var terms = searchInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        double totalScore = 0;
        var scoredTerms = 0;
        foreach (var rawTerm in terms)
        {
            var term = rawTerm.Trim('*');
            if (string.IsNullOrEmpty(term)) continue;

            scoredTerms++;
            if (name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                totalScore += 100;
            else if (name.Contains(term, StringComparison.OrdinalIgnoreCase))
                totalScore += 80;
            else if ((node.Path ?? "").Contains(term, StringComparison.OrdinalIgnoreCase))
                totalScore += 20;
            else if ((node.NodeType ?? "").Contains(term, StringComparison.OrdinalIgnoreCase))
                totalScore += 10;
            else
                totalScore += 1; // matched in content/description
        }

        // Normalize so multi-word queries don't get inflated scores.
        var score = scoredTerms > 0 ? totalScore / scoredTerms : 1;

        // Proximity boost: nodes closer to the user's current context rank higher.
        score += PathProximity.ComputeBoost(contextPath, node.Path);

        return score;
    }
}
