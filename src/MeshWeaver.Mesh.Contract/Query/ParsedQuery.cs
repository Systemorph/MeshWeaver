namespace MeshWeaver.Mesh;

/// <summary>
/// Represents a fully parsed query with reserved parameters.
/// </summary>
/// <param name="Filter">The parsed query AST (null if no filter conditions)</param>
/// <param name="TextSearch">Full-text search value (bare text in query)</param>
/// <param name="Path">Base path from path: qualifier (supports wildcards)</param>
/// <param name="Scope">Path scope from scope: qualifier</param>
/// <param name="OrderBy">Ordering clause from sort: qualifier</param>
/// <param name="Limit">Result limit from limit: qualifier</param>
/// <param name="Source">Data source from source: qualifier</param>
public record ParsedQuery(
    QueryNode? Filter,
    string? TextSearch,
    string? Path = null,
    QueryScope Scope = QueryScope.Exact,
    OrderByClause? OrderBy = null,
    int? Limit = null,
    QuerySource Source = QuerySource.Default
)
{
    /// <summary>
    /// An empty query with no filters.
    /// </summary>
    public static ParsedQuery Empty => new(null, null);

    /// <summary>
    /// Whether this query has any conditions to evaluate.
    /// </summary>
    public bool HasConditions => Filter != null || !string.IsNullOrEmpty(TextSearch);
}

/// <summary>
/// Specifies ordering for query results.
/// </summary>
/// <param name="Property">Property name to order by</param>
/// <param name="Descending">True for descending order, false for ascending</param>
public record OrderByClause(string Property, bool Descending = false);

/// <summary>
/// Specifies the data source for a query.
/// </summary>
public enum QuerySource
{
    /// <summary>
    /// Normal node/partition queries.
    /// </summary>
    Default,

    /// <summary>
    /// User activity records from _activity/{userId} partition.
    /// </summary>
    Activity
}
