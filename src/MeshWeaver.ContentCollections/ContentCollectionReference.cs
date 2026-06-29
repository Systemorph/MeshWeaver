using MeshWeaver.Data;

namespace MeshWeaver.ContentCollections;

/// <summary>
/// Reference for accessing content collection configurations.
/// Used by the "collection" UnifiedPath handler.
/// Path format: collection[/CollectionName]
/// If CollectionName is provided, returns configuration for that specific collection.
/// If CollectionName is empty/null, returns all collection configurations.
/// </summary>
/// <param name="CollectionNames">Optional collection names to filter, or null/empty for all collections</param>
public record ContentCollectionReference(params IReadOnlyCollection<string>? CollectionNames) : WorkspaceReference<object>
{
    /// <summary>
    /// Renders the reference as its path form: <c>collection/name1,name2</c> when names are
    /// specified, otherwise just <c>collection</c> (all collections).
    /// </summary>
    /// <returns>The string path representation of this reference.</returns>
    public override string ToString() =>
        CollectionNames is { Count: > 0 }
            ? $"collection/{string.Join(",", CollectionNames)}"
            : "collection";
}
