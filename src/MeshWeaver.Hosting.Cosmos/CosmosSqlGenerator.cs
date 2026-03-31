using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MeshWeaver.Mesh;

namespace MeshWeaver.Hosting.Cosmos;

/// <summary>
/// Generates Cosmos DB SQL queries from parsed RSQL/FIQL queries.
/// </summary>
public class CosmosSqlGenerator
{
    private int _paramIndex;
    private readonly Dictionary<string, object> _parameters = new();

    /// <summary>
    /// Generates a Cosmos DB SQL WHERE clause from a parsed query.
    /// </summary>
    /// <param name="query">The parsed RSQL query</param>
    /// <param name="alias">The container alias (default: "c")</param>
    /// <returns>The SQL WHERE clause and parameters</returns>
    public (string WhereClause, Dictionary<string, object> Parameters) GenerateWhereClause(
        ParsedQuery query,
        string alias = "c")
    {
        _paramIndex = 0;
        _parameters.Clear();

        var clauses = new List<string>();

        // Generate filter clause if present
        if (query.Filter != null)
        {
            var filterClause = GenerateNodeClause(query.Filter, alias);
            if (!string.IsNullOrEmpty(filterClause))
                clauses.Add(filterClause);
        }

        // Generate text search clause if present
        if (!string.IsNullOrEmpty(query.TextSearch))
        {
            var textClause = GenerateTextSearchClause(query.TextSearch, alias);
            if (!string.IsNullOrEmpty(textClause))
                clauses.Add(textClause);
        }

        var whereClause = clauses.Count > 0
            ? "WHERE " + string.Join(" AND ", clauses)
            : "";

        return (whereClause, new Dictionary<string, object>(_parameters));
    }

    /// <summary>
    /// Generates a complete Cosmos DB SQL SELECT query.
    /// </summary>
    /// <param name="query">The parsed RSQL query</param>
    /// <param name="alias">The container alias (default: "c")</param>
    /// <returns>The complete SQL query and parameters</returns>
    public (string Sql, Dictionary<string, object> Parameters) GenerateSelectQuery(
        ParsedQuery query,
        string alias = "c")
    {
        var (whereClause, parameters) = GenerateWhereClause(query, alias);

        var sql = new StringBuilder("SELECT");

        // Add TOP if limit specified
        if (query.Limit.HasValue)
            sql.Append($" TOP {query.Limit.Value}");

        sql.Append($" * FROM {alias}");

        if (!string.IsNullOrEmpty(whereClause))
            sql.Append($" {whereClause}");

        // Add ORDER BY if specified
        if (query.OrderBy != null)
        {
            var direction = query.OrderBy.Descending ? "DESC" : "ASC";
            sql.Append($" ORDER BY {alias}.{query.OrderBy.Property} {direction}");
        }

        return (sql.ToString(), parameters);
    }

    /// <summary>
    /// Generates a scope-based path filtering clause.
    /// </summary>
    /// <param name="basePath">The base path to filter on</param>
    /// <param name="scope">The query scope</param>
    /// <param name="alias">The container alias (default: "c")</param>
    /// <returns>The scope clause and parameters</returns>
    public (string Clause, Dictionary<string, object> Parameters) GenerateScopeClause(
        string? basePath,
        QueryScope scope,
        string alias = "c")
    {
        var parameters = new Dictionary<string, object>();

        if (string.IsNullOrEmpty(basePath))
            return ("", parameters);

        var normalizedPath = basePath.Trim('/');

        var clause = scope switch
        {
            QueryScope.Exact => GenerateExactClause(normalizedPath, alias, parameters),
            QueryScope.Children => GenerateChildrenClause(normalizedPath, alias, parameters),
            QueryScope.Descendants => GenerateDescendantsClause(normalizedPath, alias, parameters),
            QueryScope.Subtree => GenerateSubtreeClause(normalizedPath, alias, parameters),
            QueryScope.Ancestors => GenerateAncestorsClause(normalizedPath, alias, parameters),
            QueryScope.AncestorsAndSelf => GenerateAncestorsAndSelfClause(normalizedPath, alias, parameters),
            QueryScope.Hierarchy => GenerateHierarchyClause(normalizedPath, alias, parameters),
            _ => ""
        };

        return (clause, parameters);
    }

    /// <summary>
    /// Generates a vector similarity search query.
    /// </summary>
    /// <param name="filterQuery">Optional filter query to combine with vector search</param>
    /// <param name="queryVector">The query embedding vector</param>
    /// <param name="topK">Number of results to return (default: 10)</param>
    /// <param name="embeddingField">The field containing embeddings (default: "embedding")</param>
    /// <param name="alias">The container alias (default: "c")</param>
    /// <returns>The vector search SQL query and parameters</returns>
    public (string Sql, Dictionary<string, object> Parameters) GenerateVectorSearchQuery(
        ParsedQuery? filterQuery,
        float[] queryVector,
        int topK = 10,
        string embeddingField = "embedding",
        string alias = "c")
    {
        var parameters = new Dictionary<string, object>();

        var sql = new StringBuilder($"SELECT TOP {topK} * FROM {alias}");

        if (filterQuery != null)
        {
            var (whereClause, filterParams) = GenerateWhereClause(filterQuery, alias);
            if (!string.IsNullOrEmpty(whereClause))
            {
                sql.Append($" {whereClause}");
                foreach (var (k, v) in filterParams)
                    parameters[k] = v;
            }
        }

        parameters["@queryVector"] = queryVector;
        sql.Append($" ORDER BY VectorDistance({alias}.{embeddingField}, @queryVector)");

        return (sql.ToString(), parameters);
    }

    private string GenerateExactClause(string path, string alias, Dictionary<string, object> parameters)
    {
        parameters["@scopePath"] = path;
        return $"{alias}.path = @scopePath";
    }

    private string GenerateChildrenClause(string path, string alias, Dictionary<string, object> parameters)
    {
        // Use STARTSWITH for index utilization, then RegexMatch for exact children
        parameters["@scopePrefix"] = $"{path}/";
        parameters["@childPattern"] = HierarchyPatterns.DirectChildren(path);
        return $"(STARTSWITH({alias}.path, @scopePrefix) AND RegexMatch({alias}.path, @childPattern))";
    }

    private string GenerateDescendantsClause(string path, string alias, Dictionary<string, object> parameters)
    {
        parameters["@scopePrefix"] = $"{path}/";
        return $"STARTSWITH({alias}.path, @scopePrefix)";
    }

    private string GenerateSubtreeClause(string path, string alias, Dictionary<string, object> parameters)
    {
        parameters["@scopePath"] = path;
        parameters["@scopePrefix"] = $"{path}/";
        return $"({alias}.path = @scopePath OR STARTSWITH({alias}.path, @scopePrefix))";
    }

    private string GenerateAncestorsClause(string path, string alias, Dictionary<string, object> parameters)
    {
        var ancestors = HierarchyPatterns.GetAncestorPaths(path);
        if (ancestors.Length == 0)
            return "1=0"; // No ancestors, return false condition

        var paramNames = new List<string>();
        for (var i = 0; i < ancestors.Length; i++)
        {
            var paramName = $"@ancestor{i}";
            parameters[paramName] = ancestors[i];
            paramNames.Add(paramName);
        }
        return $"{alias}.path IN ({string.Join(", ", paramNames)})";
    }

    private string GenerateAncestorsAndSelfClause(string path, string alias, Dictionary<string, object> parameters)
    {
        var ancestors = HierarchyPatterns.GetAncestorPaths(path);
        var allPaths = ancestors.Append(path).ToArray();

        var paramNames = new List<string>();
        for (var i = 0; i < allPaths.Length; i++)
        {
            var paramName = $"@ancestor{i}";
            parameters[paramName] = allPaths[i];
            paramNames.Add(paramName);
        }
        return $"{alias}.path IN ({string.Join(", ", paramNames)})";
    }

    private string GenerateHierarchyClause(string path, string alias, Dictionary<string, object> parameters)
    {
        // Hierarchy = ancestors + self + descendants
        var ancestors = HierarchyPatterns.GetAncestorPaths(path);

        var paramNames = new List<string>();
        for (var i = 0; i < ancestors.Length; i++)
        {
            var paramName = $"@ancestor{i}";
            parameters[paramName] = ancestors[i];
            paramNames.Add(paramName);
        }

        // Add self
        var selfParam = $"@ancestor{ancestors.Length}";
        parameters[selfParam] = path;
        paramNames.Add(selfParam);

        parameters["@scopePrefix"] = $"{path}/";

        var ancestorsClause = $"{alias}.path IN ({string.Join(", ", paramNames)})";
        var descendantsClause = $"STARTSWITH({alias}.path, @scopePrefix)";

        return $"({ancestorsClause} OR {descendantsClause})";
    }

    private string GenerateNodeClause(QueryNode node, string alias)
    {
        return node switch
        {
            QueryComparison comparison => GenerateComparisonClause(comparison.Condition, alias),
            QueryAnd and => GenerateAndClause(and, alias),
            QueryOr or => GenerateOrClause(or, alias),
            _ => ""
        };
    }

    private string GenerateComparisonClause(QueryCondition condition, string alias)
    {
        var selector = $"{alias}.{condition.Selector}";
        var paramName = $"@p{_paramIndex++}";

        switch (condition.Operator)
        {
            case QueryOperator.Equal:
                _parameters[paramName] = ConvertValue(condition.Value);
                return $"{selector} = {paramName}";

            case QueryOperator.NotEqual:
                _parameters[paramName] = ConvertValue(condition.Value);
                return $"{selector} != {paramName}";

            case QueryOperator.GreaterThan:
                _parameters[paramName] = ConvertValue(condition.Value);
                return $"{selector} > {paramName}";

            case QueryOperator.LessThan:
                _parameters[paramName] = ConvertValue(condition.Value);
                return $"{selector} < {paramName}";

            case QueryOperator.GreaterOrEqual:
                _parameters[paramName] = ConvertValue(condition.Value);
                return $"{selector} >= {paramName}";

            case QueryOperator.LessOrEqual:
                _parameters[paramName] = ConvertValue(condition.Value);
                return $"{selector} <= {paramName}";

            case QueryOperator.Like:
                // Use CONTAINS for like queries (case-insensitive)
                var pattern = condition.Value.Trim('*');
                _parameters[paramName] = pattern;
                return $"CONTAINS({selector}, {paramName}, true)";

            case QueryOperator.In:
                // Generate IN clause with multiple parameters
                var inParams = new List<string>();
                foreach (var value in condition.Values)
                {
                    var inParamName = $"@p{_paramIndex++}";
                    _parameters[inParamName] = ConvertValue(value);
                    inParams.Add(inParamName);
                }
                return $"{selector} IN ({string.Join(", ", inParams)})";

            case QueryOperator.NotIn:
                // Generate NOT IN clause
                var notInParams = new List<string>();
                foreach (var value in condition.Values)
                {
                    var notInParamName = $"@p{_paramIndex++}";
                    _parameters[notInParamName] = ConvertValue(value);
                    notInParams.Add(notInParamName);
                }
                return $"{selector} NOT IN ({string.Join(", ", notInParams)})";

            default:
                return "";
        }
    }

    private string GenerateAndClause(QueryAnd and, string alias)
    {
        var clauses = and.Children
            .Select(child => GenerateNodeClause(child, alias))
            .Where(c => !string.IsNullOrEmpty(c))
            .ToList();

        if (clauses.Count == 0)
            return "";
        if (clauses.Count == 1)
            return clauses[0];

        return $"({string.Join(" AND ", clauses)})";
    }

    private string GenerateOrClause(QueryOr or, string alias)
    {
        var clauses = or.Children
            .Select(child => GenerateNodeClause(child, alias))
            .Where(c => !string.IsNullOrEmpty(c))
            .ToList();

        if (clauses.Count == 0)
            return "";
        if (clauses.Count == 1)
            return clauses[0];

        return $"({string.Join(" OR ", clauses)})";
    }

    private string GenerateTextSearchClause(string textSearch, string alias)
    {
        // Split into terms — ALL terms must match as substrings (mirrors InMemory QueryEvaluator behavior).
        var terms = textSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
            return "";

        var searchFields = new[] { "name", "description", "nodeType", "path" };
        var termClauses = new List<string>();

        foreach (var term in terms)
        {
            var paramName = $"@p{_paramIndex++}";
            _parameters[paramName] = term.ToLowerInvariant();

            // Each term must appear in at least one field
            var fieldClauses = searchFields
                .Select(f => $"CONTAINS(LOWER({alias}.{f} ?? \"\"), {paramName})")
                .ToArray();
            termClauses.Add($"({string.Join(" OR ", fieldClauses)})");
        }

        return termClauses.Count == 1 ? termClauses[0] : $"({string.Join(" AND ", termClauses)})";
    }

    private static object ConvertValue(string value)
    {
        // Try to convert to appropriate type for Cosmos DB

        // Boolean
        if (bool.TryParse(value, out var boolVal))
            return boolVal;

        // Integer
        if (long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var longVal))
            return longVal;

        // Decimal/Double
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var doubleVal))
            return doubleVal;

        // DateTimeOffset
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateVal))
            return dateVal;

        // Default to string
        return value;
    }
}

/// <summary>
/// Generates regex patterns for Cosmos DB hierarchy queries.
/// </summary>
public static class HierarchyPatterns
{
    /// <summary>
    /// Direct children: a/b/* matches a/b/x but NOT a/b/x/y
    /// </summary>
    /// <param name="basePath">The base path (empty or null for root children)</param>
    /// <returns>A regex pattern matching direct children</returns>
    public static string DirectChildren(string? basePath)
    {
        if (string.IsNullOrEmpty(basePath))
            return "^[^/]+$"; // Root children
        return $"^{Regex.Escape(basePath)}/[^/]+$";
    }

    /// <summary>
    /// Exact depth below base: a/b/*/* matches exactly 2 levels below
    /// </summary>
    /// <param name="basePath">The base path</param>
    /// <param name="levelsBelow">Number of levels below the base path</param>
    /// <returns>A regex pattern matching the exact depth</returns>
    public static string ExactDepth(string? basePath, int levelsBelow)
    {
        var segments = string.Concat(Enumerable.Repeat("/[^/]+", levelsBelow));
        var prefix = string.IsNullOrEmpty(basePath) ? "^" : $"^{Regex.Escape(basePath)}";
        return prefix + segments + "$";
    }

    /// <summary>
    /// Contains segment: */electronics/* matches any path with /electronics/ segment
    /// </summary>
    /// <param name="segment">The segment to match</param>
    /// <returns>A regex pattern matching paths containing the segment</returns>
    public static string ContainsSegment(string segment)
        => $"/{Regex.Escape(segment)}/";

    /// <summary>
    /// Wildcard pattern: a/*/c matches a/x/c, a/y/c, etc.
    /// </summary>
    /// <param name="pattern">The wildcard pattern with * for single segment wildcards</param>
    /// <returns>A regex pattern for the wildcard</returns>
    public static string WildcardInPath(string pattern)
        => "^" + Regex.Escape(pattern).Replace("\\*", "[^/]+") + "$";

    /// <summary>
    /// Gets all ancestor paths of the given path.
    /// </summary>
    /// <param name="path">The path to get ancestors for</param>
    /// <returns>Array of ancestor paths (excluding self)</returns>
    public static string[] GetAncestorPaths(string path)
    {
        if (string.IsNullOrEmpty(path))
            return [];

        var segments = path.Split('/');
        var ancestors = new List<string>();
        for (var i = 1; i < segments.Length; i++)
            ancestors.Add(string.Join("/", segments.Take(i)));
        return ancestors.ToArray();
    }
}
