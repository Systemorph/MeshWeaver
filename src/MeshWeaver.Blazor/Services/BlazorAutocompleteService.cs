using System.Reactive.Linq;
using MeshWeaver.Blazor.Components.Monaco;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive;

namespace MeshWeaver.Blazor.Services;

/// <summary>
/// Centralized streaming autocomplete service for Blazor components.
/// Returns <see cref="IObservable{T}"/> snapshots — Monaco's
/// <c>CompletionCallback</c> subscribes once and pushes each snapshot to the suggest
/// widget as items arrive. No <c>Task</c>, no <c>await</c>, no blocking.
/// </summary>
public class BlazorAutocompleteService(IMeshService meshQuery)
{
    private const int CompletionLimit = 20;

    // Higher score = better. Sort descending.
    private static readonly IComparer<QuerySuggestion> ByScoreDescending =
        Comparer<QuerySuggestion>.Create((a, b) => b.Score.CompareTo(a.Score));

    /// <summary>
    /// Streams completion snapshots for <paramref name="query"/>. Resolves <c>@</c>-prefixed
    /// references vs free-text searches and dispatches to the appropriate mesh autocomplete.
    /// </summary>
    public IObservable<IReadOnlyList<CompletionItem>> GetCompletions(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Observable.Return<IReadOnlyList<CompletionItem>>(Array.Empty<CompletionItem>());

        if (query.StartsWith("@"))
            return GetReferenceCompletions(query[1..]);

        return Stream("", query, addressCategory: "");
    }

    /// <summary>
    /// Streams completions for <c>@</c>-references (without the <c>@</c> prefix).
    /// </summary>
    public IObservable<IReadOnlyList<CompletionItem>> GetReferenceCompletions(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return Stream("", "", addressCategory: "Addresses");

        if (reference.EndsWith("/"))
            return Stream(reference.TrimEnd('/'), "", addressCategory: "");

        // Partial match like "@Sys" — split into basePath/namePrefix.
        var lastSlash = reference.LastIndexOf('/');
        var basePath = lastSlash >= 0 ? reference[..lastSlash] : "";
        var namePrefix = lastSlash >= 0 ? reference[(lastSlash + 1)..] : reference;
        return Stream(basePath, namePrefix, addressCategory: "Addresses");
    }

    private IObservable<IReadOnlyList<CompletionItem>> Stream(
        string basePath, string namePrefix, string addressCategory) =>
        meshQuery.AutocompleteAsync(basePath, namePrefix, CompletionLimit)
            .ScanTopN(CompletionLimit, ByScoreDescending)
            .Select(snapshot => (IReadOnlyList<CompletionItem>)snapshot
                .Select(s => new CompletionItem
                {
                    Label = s.Path,
                    InsertText = string.IsNullOrEmpty(addressCategory) ? s.Path : $"@{s.Path}",
                    Description = s.NodeType ?? s.Name,
                    Detail = s.Name,
                    Category = addressCategory
                })
                .ToArray());
}
