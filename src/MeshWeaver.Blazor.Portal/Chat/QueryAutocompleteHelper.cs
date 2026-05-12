using System.Reactive.Linq;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Blazor.Portal.Chat;

/// <summary>
/// Helper for <see cref="MeshNodeAutocomplete"/>: composes per-query
/// <see cref="IMeshService.ObserveQuery{T}"/> streams into a single deduped
/// suggestion list. Lives in a .cs file (not the .razor) so the IObservable
/// extension methods resolve cleanly — the razor file's combined LINQ/Async
/// namespaces make <c>Take</c>/<c>Catch</c> ambiguous against Rx.
/// </summary>
internal static class QueryAutocompleteHelper
{
    public static async Task<List<QuerySuggestion>> LoadSuggestionsAsync(
        IMeshService meshQuery,
        IReadOnlyList<string> queries,
        string? searchText)
    {
        var userText = (searchText ?? "").Trim();
        var observables = queries.Select(baseQuery =>
        {
            var fullQuery = string.IsNullOrEmpty(userText)
                ? baseQuery
                : $"{baseQuery} {userText}";
            return meshQuery
                .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(fullQuery))
                .Take(1)
                .Catch<QueryResultChange<MeshNode>, Exception>(
                    _ => Observable.Empty<QueryResultChange<MeshNode>>());
        });

        var merged = await Observable.Merge(observables).ToList();
        return merged
            .SelectMany(c => c.Items)
            .Select(n => new QuerySuggestion(n.Path, n.Name ?? n.Id, n.NodeType, 1.0, n.Icon))
            .GroupBy(s => s.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }
}
