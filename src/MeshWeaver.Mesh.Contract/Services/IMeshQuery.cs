using System.Runtime.CompilerServices;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Query service for searching MeshNodes and partition objects.
/// Separated from IMeshStorage to allow swappable implementations
/// (InMemory, ElasticSearch, Cosmos with vector search, etc.)
/// This is the scoped wrapper that automatically injects JsonSerializerOptions from IMessageHub.
/// </summary>
public interface IMeshQuery
{
    /// <summary>
    /// Query nodes and partition objects with full-text search, filtering, and scoping.
    /// Uses GitHub-style query syntax (e.g., "nodeType:Story status:Open laptop").
    /// </summary>
    /// <param name="request">Query request with filters, path, and options</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Matching objects (MeshNodes and partition objects)</returns>
    IAsyncEnumerable<object> QueryAsync(MeshQueryRequest request, CancellationToken ct = default);

    /// <summary>
    /// Autocomplete query - given a namespace, find best matching subnodes.
    /// Returns suggestions ordered by path length first (for path-based autocomplete).
    /// </summary>
    /// <param name="basePath">Base path to search from</param>
    /// <param name="prefix">Prefix to match (partial name/path)</param>
    /// <param name="limit">Maximum number of suggestions to return</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Suggestions ordered by path length, then score, then name</returns>
    IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        int limit = 10,
        CancellationToken ct = default);

    /// <summary>
    /// Autocomplete query with specified ordering mode.
    /// </summary>
    /// <param name="basePath">Base path to search from</param>
    /// <param name="prefix">Prefix to match (partial name/path)</param>
    /// <param name="mode">Ordering mode (PathFirst or RelevanceFirst)</param>
    /// <param name="limit">Maximum number of suggestions to return</param>
    /// <param name="contextPath">Context path for proximity-based scoring (null for no proximity boost)</param>
    /// <param name="context">Context for visibility filtering (e.g., "search"). Nodes excluded from this context are hidden.</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Suggestions ordered according to mode</returns>
    IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        AutocompleteMode mode,
        int limit = 10,
        string? contextPath = null,
        string? context = null,
        CancellationToken ct = default);

    /// <summary>
    /// Creates an observable query that monitors data sources for changes and emits updates.
    /// The first emission contains the full initial result set (ChangeType = Initial).
    /// Subsequent emissions contain incremental changes (Added, Updated, Removed).
    /// </summary>
    /// <typeparam name="T">The type of objects to query (typically MeshNode).</typeparam>
    /// <param name="request">Query request with filters, path, scope, and options.</param>
    /// <returns>An observable that emits query result changes.</returns>
    /// <example>
    /// <code>
    /// var subscription = meshQuery
    ///     .ObserveQuery&lt;MeshNode&gt;(MeshQueryRequest.FromQuery("path:ACME nodeType:Story scope:descendants"))
    ///     .Subscribe(change =&gt;
    ///     {
    ///         Console.WriteLine($"Change: {change.ChangeType}, Items: {change.Items.Count}");
    ///     });
    /// // Later: dispose to stop watching
    /// subscription.Dispose();
    /// </code>
    /// </example>
    IObservable<QueryResultChange<T>> ObserveQuery<T>(MeshQueryRequest request);

    /// <summary>
    /// Selects a single property value from a node at the given path.
    /// Efficient way to get one property without loading the full Content blob.
    /// </summary>
    /// <typeparam name="T">The expected property type.</typeparam>
    /// <param name="path">The node path.</param>
    /// <param name="property">The property name on MeshNode.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The property value, or default if node not found or property is null.</returns>
    Task<T?> SelectAsync<T>(string path, string property, CancellationToken ct = default);

    /// <summary>
    /// Returns an IMeshQuery that runs all queries with the node's own identity
    /// as the AccessContext (same as IMeshNodePersistence.ImpersonateAsNode()).
    /// Use this when infrastructure code needs read access without a user context
    /// (e.g., VirtualUserMiddleware checking node existence before authentication).
    /// </summary>
    IMeshQuery ImpersonateAsNode();
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
    /// Default path to use when no path is specified in the query.
    /// Set this from the current navigation context (e.g., INavigationService.CurrentNamespace)
    /// before calling query methods.
    /// </summary>
    public string? DefaultPath { get; init; }

    /// <summary>
    /// Context path for proximity-based scoring.
    /// When set, results closer to this path in the graph hierarchy receive a score boost.
    /// Typically set to the user's current namespace (e.g., "Systemorph/Marketing").
    /// </summary>
    public string? ContextPath { get; init; }

    /// <summary>
    /// Number of results to skip (for paging).
    /// </summary>
    public int? Skip { get; init; }

    /// <summary>
    /// Maximum number of results to return (takes precedence over limit in query string).
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>
    /// Context for visibility filtering. When set, nodes with this context
    /// in their ExcludeFromContext are excluded from results.
    /// Parsed context: qualifier in query string is used as fallback.
    /// </summary>
    public string? Context { get; init; }

    /// <summary>
    /// Creates a new request with the specified query string.
    /// </summary>
    public static MeshQueryRequest FromQuery(string query) => new() { Query = query };

    /// <summary>
    /// Creates a new request with query and user ID.
    /// </summary>
    public static MeshQueryRequest FromQuery(string query, string? userId)
        => new() { Query = query, UserId = userId };

    /// <summary>
    /// Creates a new request with query and default path.
    /// </summary>
    public static MeshQueryRequest FromQuery(string query, string? userId, string? defaultPath)
        => new() { Query = query, UserId = userId, DefaultPath = defaultPath };
}

/// <summary>
/// Autocomplete suggestion result.
/// </summary>
/// <param name="Path">Full path to the node</param>
/// <param name="Name">Display name of the node</param>
/// <param name="NodeType">Type of the node (may be null)</param>
/// <param name="Score">Relevance score (higher is better match)</param>
/// <param name="Icon">Icon URL or identifier for display in UI (may be null)</param>
public record QuerySuggestion(string Path, string Name, string? NodeType, double Score, string? Icon = null);

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
