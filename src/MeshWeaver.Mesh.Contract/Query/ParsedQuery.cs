namespace MeshWeaver.Mesh.Query;

/// <summary>
/// Represents a fully parsed RSQL query with reserved parameters.
/// </summary>
/// <param name="Filter">The parsed RSQL AST (null if no filter conditions)</param>
/// <param name="TextSearch">Full-text search value from $search parameter</param>
/// <param name="Scope">Path scope from $scope parameter</param>
/// <param name="OrderBy">Ordering clause from $orderBy parameter</param>
/// <param name="Limit">Result limit from $limit parameter</param>
/// <param name="Source">Data source from $source parameter</param>
public record ParsedQuery(
    RsqlNode? Filter,
    string? TextSearch,
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
