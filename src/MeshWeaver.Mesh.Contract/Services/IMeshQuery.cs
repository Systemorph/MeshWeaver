using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Query service for searching MeshNodes and partition objects.
/// Separated from IPersistenceService to allow swappable implementations
/// (InMemory, ElasticSearch, Cosmos with vector search, etc.)
/// </summary>
public interface IMeshQuery
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
    /// <param name="ct">Cancellation token</param>
    /// <returns>Suggestions ordered according to mode</returns>
    IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        AutocompleteMode mode,
        int limit = 10,
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
}

/// <summary>
/// Request parameters for mesh queries.
/// Most query parameters are expressed in the query string itself.
/// </summary>
public record MeshQueryRequest
{
    /// <summary>
    /// GitHub-style query string with all parameters.
    /// Examples:
    /// - "nodeType:Story" - filter by property
    /// - "path:Org/Project nodeType:Story scope:descendants" - scoped search
    /// - "status:Open laptop" - filter + text search
    /// - "sort:lastAccessedAt-desc limit:20" - with ordering and limit
    /// - "source:activity" - query activity records
    /// </summary>
    public string Query { get; init; } = "";

    /// <summary>
    /// User ID for access control filtering.
    /// When set, results are filtered to only include nodes the user can access.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Number of results to skip (for paging).
    /// </summary>
    public int? Skip { get; init; }

    /// <summary>
    /// Maximum number of results to return (takes precedence over limit in query string).
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>
    /// Creates a new request with the specified query string.
    /// </summary>
    public static MeshQueryRequest FromQuery(string query) => new() { Query = query };

    /// <summary>
    /// Creates a new request with query and user ID.
    /// </summary>
    public static MeshQueryRequest FromQuery(string query, string? userId)
        => new() { Query = query, UserId = userId };
}

/// <summary>
/// Autocomplete suggestion result.
/// </summary>
/// <param name="Path">Full path to the node</param>
/// <param name="Name">Display name of the node</param>
/// <param name="NodeType">Type of the node (may be null)</param>
/// <param name="Score">Relevance score (higher is better match)</param>
public record QuerySuggestion(string Path, string Name, string? NodeType, double Score);

/// <summary>
/// Autocomplete ordering mode.
/// </summary>
public enum AutocompleteMode
{
    /// <summary>
    /// Order by path length first, then score, then name.
    /// Best for path-based autocomplete (e.g., typing a path reference).
    /// </summary>
    PathFirst,

    /// <summary>
    /// Order by score first (name match > path match > other), then path length, then name.
    /// Best for node search/selection (e.g., context selector).
    /// </summary>
    RelevanceFirst
}
