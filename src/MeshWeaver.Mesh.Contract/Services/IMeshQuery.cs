using System.Runtime.CompilerServices;

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
    /// <param name="ct">Cancellation token</param>
    /// <returns>Matching objects (MeshNodes and partition objects)</returns>
    IAsyncEnumerable<object> QueryAsync(MeshQueryRequest request, CancellationToken ct = default);

    /// <summary>
    /// Autocomplete query - given a namespace, find best matching subnodes.
    /// Returns suggestions ordered by relevance score.
    /// </summary>
    /// <param name="basePath">Base path to search from</param>
    /// <param name="prefix">Prefix to match (partial name/path)</param>
    /// <param name="limit">Maximum number of suggestions to return</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Suggestions ordered by relevance</returns>
    IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        int limit = 10,
        CancellationToken ct = default);
}

/// <summary>
/// Request parameters for mesh queries.
/// </summary>
public record MeshQueryRequest
{
    /// <summary>
    /// GitHub-style query string.
    /// Examples:
    /// - "nodeType:Story" - filter by property
    /// - "status:Open laptop" - filter + text search
    /// - "nodeType:Story scope:descendants" - with scope
    /// - "sort:lastAccessedAt-desc limit:20" - with ordering and limit
    /// </summary>
    public string Query { get; init; } = "";

    /// <summary>
    /// Base path to search from. Empty string means root.
    /// Combined with Scope to determine search area.
    /// </summary>
    public string BasePath { get; init; } = "";

    /// <summary>
    /// Optional namespace restriction. When set, only returns nodes
    /// whose path starts with this namespace.
    /// </summary>
    public string? Namespace { get; init; }

    /// <summary>
    /// User ID for access control filtering.
    /// When set, results are filtered to only include nodes the user can access.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// When true, queries user activity records instead of or in addition to nodes.
    /// Equivalent to including "source:activity" in query.
    /// </summary>
    public bool IncludeActivities { get; init; }

    /// <summary>
    /// Override limit (takes precedence over limit in query string).
    /// </summary>
    public int? Limit { get; init; }

    /// <summary>
    /// Creates a new request with the specified query string.
    /// </summary>
    public static MeshQueryRequest FromQuery(string query) => new() { Query = query };

    /// <summary>
    /// Creates a new request with query and base path.
    /// </summary>
    public static MeshQueryRequest FromQuery(string query, string basePath)
        => new() { Query = query, BasePath = basePath };
}

/// <summary>
/// Autocomplete suggestion result.
/// </summary>
/// <param name="Path">Full path to the node</param>
/// <param name="Name">Display name of the node</param>
/// <param name="NodeType">Type of the node (may be null)</param>
/// <param name="Score">Relevance score (higher is better match)</param>
public record QuerySuggestion(string Path, string Name, string? NodeType, double Score);
