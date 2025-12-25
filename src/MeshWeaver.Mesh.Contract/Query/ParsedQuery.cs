namespace MeshWeaver.Mesh.Query;

/// <summary>
/// Represents a fully parsed RSQL query with reserved parameters.
/// </summary>
/// <param name="Filter">The parsed RSQL AST (null if no filter conditions)</param>
/// <param name="TextSearch">Full-text search value from $search parameter</param>
/// <param name="Scope">Path scope from $scope parameter</param>
public record ParsedQuery(
    RsqlNode? Filter,
    string? TextSearch,
    QueryScope Scope = QueryScope.Exact
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
