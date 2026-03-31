using System.Reflection;

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
/// <param name="Select">Property names to project results onto (from select: qualifier)</param>
/// <param name="Context">Context for visibility filtering (from context: qualifier)</param>
/// <param name="IsMain">When true, filters to main nodes only (MainNode is null or equals Path)</param>
public record ParsedQuery(
    QueryNode? Filter,
    string? TextSearch,
    string? Path = null,
    QueryScope Scope = QueryScope.Exact,
    OrderByClause? OrderBy = null,
    int? Limit = null,
    QuerySource Source = QuerySource.Default,
    IReadOnlyList<string>? Select = null,
    string? Context = null,
    bool? IsMain = null
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

    /// <summary>
    /// Extracts the nodeType value from the filter if there's a simple equality condition.
    /// Returns null if the filter doesn't contain a nodeType condition or it's complex.
    /// </summary>
    public string? ExtractNodeType()
    {
        if (Filter == null) return null;
        return ExtractNodeTypeFromNode(Filter);
    }

    private static string? ExtractNodeTypeFromNode(QueryNode node) => node switch
    {
        QueryComparison c when c.Condition.Selector.Equals("nodeType", StringComparison.OrdinalIgnoreCase)
            && c.Condition.Operator == QueryOperator.Equal
            && c.Condition.Values.Length == 1 => c.Condition.Values[0],
        QueryAnd and => and.Children.Select(ExtractNodeTypeFromNode).FirstOrDefault(v => v != null),
        _ => null
    };

    /// <summary>
    /// Projects an item down to only the requested properties.
    /// Returns a dictionary with the selected property names and their values.
    /// </summary>
    public static object ProjectToSelect(object item, IReadOnlyList<string> properties)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var type = item.GetType();
        foreach (var prop in properties)
        {
            var pi = type.GetProperty(prop, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            dict[prop] = pi?.GetValue(item);
        }
        return dict;
    }
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
    /// ActivityLog nodes — implies nodeType:Activity filter.
    /// </summary>
    Activity,

    /// <summary>
    /// Nodes the current user has accessed, ordered by UserActivity last-access time.
    /// </summary>
    Accessed
}
