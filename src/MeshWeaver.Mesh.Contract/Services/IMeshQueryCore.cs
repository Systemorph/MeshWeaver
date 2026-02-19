using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Core query service for searching MeshNodes and partition objects.
/// Separated from IPersistenceServiceCore to allow swappable implementations
/// (InMemory, ElasticSearch, Cosmos with vector search, etc.)
/// This is the internal interface that accepts JsonSerializerOptions per method.
/// Use IMeshQuery for the scoped wrapper that injects options automatically.
/// </summary>
public interface IMeshQueryCore
{
    /// <summary>
    /// Query nodes and partition objects with full-text search, filtering, and scoping.
    /// Uses GitHub-style query syntax (e.g., "nodeType:Story status:Open laptop").
    /// </summary>
    /// <param name="request">Query request with filters, path, and options</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Matching objects (MeshNodes and partition objects)</returns>
    IAsyncEnumerable<object> QueryAsync(MeshQueryRequest request, JsonSerializerOptions options, CancellationToken ct = default);

    /// <summary>
    /// Autocomplete query - given a namespace, find best matching subnodes.
    /// Returns suggestions ordered by path length first (for path-based autocomplete).
    /// </summary>
    /// <param name="basePath">Base path to search from</param>
    /// <param name="prefix">Prefix to match (partial name/path)</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <param name="limit">Maximum number of suggestions to return</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Suggestions ordered by path length, then score, then name</returns>
    IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        int limit = 10,
        CancellationToken ct = default);

    /// <summary>
    /// Autocomplete query with specified ordering mode.
    /// </summary>
    /// <param name="basePath">Base path to search from</param>
    /// <param name="prefix">Prefix to match (partial name/path)</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <param name="mode">Ordering mode (PathFirst or RelevanceFirst)</param>
    /// <param name="limit">Maximum number of suggestions to return</param>
    /// <param name="contextPath">Context path for proximity-based scoring (null for no proximity boost)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Suggestions ordered according to mode</returns>
    IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        AutocompleteMode mode,
        int limit = 10,
        string? contextPath = null,
        CancellationToken ct = default);

    /// <summary>
    /// Creates an observable query that monitors data sources for changes and emits updates.
    /// The first emission contains the full initial result set (ChangeType = Initial).
    /// Subsequent emissions contain incremental changes (Added, Updated, Removed).
    /// </summary>
    /// <typeparam name="T">The type of objects to query (typically MeshNode).</typeparam>
    /// <param name="request">Query request with filters, path, scope, and options.</param>
    /// <param name="options">JSON serializer options for type polymorphism</param>
    /// <returns>An observable that emits query result changes.</returns>
    /// <example>
    /// <code>
    /// var subscription = meshQuery
    ///     .ObserveQuery&lt;MeshNode&gt;(MeshQueryRequest.FromQuery("path:ACME nodeType:Story scope:descendants"), jsonOptions)
    ///     .Subscribe(change =&gt;
    ///     {
    ///         Console.WriteLine($"Change: {change.ChangeType}, Items: {change.Items.Count}");
    ///     });
    /// // Later: dispose to stop watching
    /// subscription.Dispose();
    /// </code>
    /// </example>
    IObservable<QueryResultChange<T>> ObserveQuery<T>(MeshQueryRequest request, JsonSerializerOptions options);

    /// <summary>
    /// Selects a single property value from a node at the given path.
    /// </summary>
    Task<T?> SelectAsync<T>(string path, string property, JsonSerializerOptions options, CancellationToken ct = default);
}
