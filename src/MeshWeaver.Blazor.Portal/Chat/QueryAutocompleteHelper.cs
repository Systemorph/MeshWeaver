using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Blazor.Portal.Chat;

/// <summary>
/// Helper for <see cref="MeshNodeAutocomplete"/>: composes per-query
/// <see cref="IMeshService.Query{T}"/> streams into a single deduped
/// suggestion list. Pure reactive — caller subscribes to the observable.
/// </summary>
internal static class QueryAutocompleteHelper
{
    /// <summary>
    /// Returns a folded, path-deduped list of <see cref="QuerySuggestion"/>
    /// for the given queries + user search text. Emits a fresh snapshot
    /// whenever a new path arrives. Per-path dedup via
    /// <see cref="ImmutableDictionary{TKey,TValue}"/> Scan.
    /// </summary>
    public static IObservable<IReadOnlyList<QuerySuggestion>> LoadSuggestions(
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
                .Query<MeshNode>(MeshQueryRequest.FromQuery(fullQuery))
                .Take(1)
                .Catch<QueryResultChange<MeshNode>, Exception>(
                    _ => Observable.Empty<QueryResultChange<MeshNode>>());
        });

        return Observable.Merge(observables)
            .SelectMany(change => change.Items)
            .Select(n => new QuerySuggestion(n.Path, n.Name ?? n.Id, n.NodeType, 1.0, n.Icon))
            .Scan(
                ImmutableDictionary<string, QuerySuggestion>.Empty
                    .WithComparers(StringComparer.OrdinalIgnoreCase),
                (acc, s) => acc.ContainsKey(s.Path) ? acc : acc.Add(s.Path, s))
            .Select(acc => (IReadOnlyList<QuerySuggestion>)acc.Values.ToArray());
    }
}
