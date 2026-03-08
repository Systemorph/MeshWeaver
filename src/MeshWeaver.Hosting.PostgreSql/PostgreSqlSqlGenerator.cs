using System.Globalization;
using System.Text;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Pgvector;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Generates PostgreSQL SQL queries from parsed query AST.
/// </summary>
public class PostgreSqlSqlGenerator
{
    private int _paramIndex;
    private readonly Dictionary<string, object> _parameters = new();

    private static readonly Dictionary<string, string> PropertyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "n.name",
        ["nodeType"] = "n.node_type",
        ["node_type"] = "n.node_type",
        ["description"] = "n.description",
        ["category"] = "n.category",
        ["icon"] = "n.icon",
        ["order"] = "n.display_order",
        ["display_order"] = "n.display_order",
        ["lastModified"] = "n.last_modified",
        ["last_modified"] = "n.last_modified",
        ["version"] = "n.version",
        ["state"] = "n.state",
        ["id"] = "n.id",
        ["namespace"] = "n.namespace",
        ["path"] = "n.path",
        ["mainNode"] = "n.main_node",
        ["main_node"] = "n.main_node"
    };

    /// <summary>
    /// Non-text columns where case-insensitive comparison should NOT be applied.
    /// Everything else (text columns + JSONB content fields extracted via ->>) is treated as text.
    /// </summary>
    private static readonly HashSet<string> NonTextColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "order", "display_order", "version", "state", "lastModified", "last_modified"
    };

    private static bool IsTextField(string selector) =>
        !NonTextColumns.Contains(selector);

    /// <summary>
    /// Maps a selector from the query AST to a PostgreSQL column expression.
    /// </summary>
    public static string MapSelector(string selector)
    {
        if (PropertyMap.TryGetValue(selector, out var mapped))
            return mapped;

        // content.X.Y → n.content->'X'->>'Y'
        if (selector.StartsWith("content.", StringComparison.OrdinalIgnoreCase))
        {
            var parts = selector.Split('.');
            if (parts.Length == 2)
                return $"n.content->>'{parts[1]}'";

            var sb = new StringBuilder("n.content");
            for (var i = 1; i < parts.Length - 1; i++)
                sb.Append($"->'{parts[i]}'");
            sb.Append($"->>'{parts[^1]}'");
            return sb.ToString();
        }

        // Unknown selectors fall back to JSONB content
        return $"n.content->>'{selector}'";
    }

    /// <summary>
    /// Maps a selector for ORDER BY (returns typed column or JSONB extraction).
    /// </summary>
    private static string MapOrderBySelector(string selector)
    {
        if (PropertyMap.TryGetValue(selector, out var mapped))
            return mapped;

        if (selector.StartsWith("content.", StringComparison.OrdinalIgnoreCase))
        {
            var parts = selector.Split('.');
            if (parts.Length == 2)
                return $"n.content->>'{parts[1]}'";

            var sb = new StringBuilder("n.content");
            for (var i = 1; i < parts.Length - 1; i++)
                sb.Append($"->'{parts[i]}'");
            sb.Append($"->>'{parts[^1]}'");
            return sb.ToString();
        }

        return $"n.content->>'{selector}'";
    }

    public (string WhereClause, Dictionary<string, object> Parameters) GenerateWhereClause(
        ParsedQuery query, string? userId = null)
    {
        _paramIndex = 0;
        _parameters.Clear();

        var clauses = new List<string>();

        if (query.Filter != null)
        {
            var filterClause = GenerateNodeClause(query.Filter);
            if (!string.IsNullOrEmpty(filterClause))
                clauses.Add(filterClause);
        }

        if (!string.IsNullOrEmpty(query.TextSearch))
        {
            var textClause = GenerateTextSearchClause(query.TextSearch);
            if (!string.IsNullOrEmpty(textClause))
                clauses.Add(textClause);
        }

        if (!string.IsNullOrEmpty(userId))
        {
            var acClause = GenerateAccessControlClause(userId);
            clauses.Add(acClause);
        }

        if (query.IsMain == true)
        {
            clauses.Add("n.main_node = n.path");
        }

        var whereClause = clauses.Count > 0
            ? "WHERE " + string.Join(" AND ", clauses)
            : "";

        return (whereClause, new Dictionary<string, object>(_parameters));
    }

    public (string Sql, Dictionary<string, object> Parameters) GenerateSelectQuery(
        ParsedQuery query, string? userId = null, string? activityUserId = null)
    {
        var (whereClause, parameters) = GenerateWhereClause(query, userId);

        var isActivityQuery = query.Source == QuerySource.Activity && !string.IsNullOrEmpty(activityUserId);

        var sql = new StringBuilder("SELECT n.id, n.namespace, n.name, n.node_type, n.description, " +
            "n.category, n.icon, n.display_order, n.last_modified, n.version, n.state, n.content, " +
            "n.desired_id, n.main_node FROM mesh_nodes n");

        if (isActivityQuery)
        {
            parameters["@actUserId"] = activityUserId!;
            sql.Append(" LEFT JOIN user_activity ua ON n.path = ua.node_path AND ua.user_id = @actUserId");
        }

        if (!string.IsNullOrEmpty(whereClause))
            sql.Append($" {whereClause}");

        if (isActivityQuery)
        {
            sql.Append(" ORDER BY ua.last_accessed DESC NULLS LAST");
        }
        else if (query.OrderBy != null)
        {
            var direction = query.OrderBy.Descending ? "DESC" : "ASC";
            sql.Append($" ORDER BY {MapOrderBySelector(query.OrderBy.Property)} {direction}");
        }

        if (query.Limit.HasValue)
            sql.Append($" LIMIT {query.Limit.Value}");

        return (sql.ToString(), parameters);
    }

    public (string Clause, Dictionary<string, object> Parameters) GenerateScopeClause(
        string? basePath, QueryScope scope)
    {
        var parameters = new Dictionary<string, object>();

        if (string.IsNullOrEmpty(basePath))
            return ("", parameters);

        var normalizedPath = basePath.Trim('/');

        var clause = scope switch
        {
            QueryScope.Exact => GenerateExactClause(normalizedPath, parameters),
            QueryScope.Children => GenerateChildrenClause(normalizedPath, parameters),
            QueryScope.Descendants => GenerateDescendantsClause(normalizedPath, parameters),
            QueryScope.Subtree => GenerateSubtreeClause(normalizedPath, parameters),
            QueryScope.Ancestors => GenerateAncestorsClause(normalizedPath, parameters),
            QueryScope.AncestorsAndSelf => GenerateAncestorsAndSelfClause(normalizedPath, parameters),
            QueryScope.Hierarchy => GenerateHierarchyClause(normalizedPath, parameters),
            _ => ""
        };

        return (clause, parameters);
    }

    public (string Sql, Dictionary<string, object> Parameters) GenerateVectorSearchQuery(
        ParsedQuery? filterQuery,
        float[] queryVector,
        string? userId = null,
        int topK = 10)
    {
        var parameters = new Dictionary<string, object>();

        var sql = new StringBuilder(
            "SELECT n.id, n.namespace, n.name, n.node_type, n.description, " +
            "n.category, n.icon, n.display_order, n.last_modified, n.version, n.state, n.content, " +
            "n.desired_id FROM mesh_nodes n");

        var clauses = new List<string> { "n.embedding IS NOT NULL" };

        if (filterQuery != null)
        {
            var (whereClause, filterParams) = GenerateWhereClause(filterQuery, userId);
            if (!string.IsNullOrEmpty(whereClause))
            {
                // Strip "WHERE " prefix since we're building our own WHERE
                clauses.Add(whereClause[6..]);
                foreach (var (k, v) in filterParams)
                    parameters[k] = v;
            }
        }
        else if (!string.IsNullOrEmpty(userId))
        {
            clauses.Add(GenerateAccessControlClause(userId));
            parameters = new Dictionary<string, object>(_parameters);
        }

        sql.Append(" WHERE ");
        sql.Append(string.Join(" AND ", clauses));

        parameters["@queryVector"] = new Vector(queryVector);
        sql.Append(" ORDER BY n.embedding <=> @queryVector");
        sql.Append($" LIMIT {topK}");

        return (sql.ToString(), parameters);
    }

    #region Scope Clauses

    private static string GenerateExactClause(string path, Dictionary<string, object> parameters)
    {
        parameters["@scopePath"] = path;
        return "n.path = @scopePath";
    }

    private static string GenerateChildrenClause(string path, Dictionary<string, object> parameters)
    {
        parameters["@scopeNs"] = path;
        return "n.namespace = @scopeNs";
    }

    private static string GenerateDescendantsClause(string path, Dictionary<string, object> parameters)
    {
        parameters["@scopePrefix"] = $"{path}/";
        return "n.path LIKE @scopePrefix || '%'";
    }

    private static string GenerateSubtreeClause(string path, Dictionary<string, object> parameters)
    {
        parameters["@scopePath"] = path;
        parameters["@scopePrefix"] = $"{path}/";
        return "(n.path = @scopePath OR n.path LIKE @scopePrefix || '%')";
    }

    private static string GenerateAncestorsClause(string path, Dictionary<string, object> parameters)
    {
        var ancestors = GetAncestorPaths(path);
        if (ancestors.Length == 0)
            return "FALSE";

        var paramNames = new List<string>();
        for (var i = 0; i < ancestors.Length; i++)
        {
            var paramName = $"@ancestor{i}";
            parameters[paramName] = ancestors[i];
            paramNames.Add(paramName);
        }
        return $"n.path IN ({string.Join(", ", paramNames)})";
    }

    private static string GenerateAncestorsAndSelfClause(string path, Dictionary<string, object> parameters)
    {
        var ancestors = GetAncestorPaths(path);
        var allPaths = ancestors.Append(path).ToArray();

        var paramNames = new List<string>();
        for (var i = 0; i < allPaths.Length; i++)
        {
            var paramName = $"@ancestor{i}";
            parameters[paramName] = allPaths[i];
            paramNames.Add(paramName);
        }
        return $"n.path IN ({string.Join(", ", paramNames)})";
    }

    private static string GenerateHierarchyClause(string path, Dictionary<string, object> parameters)
    {
        var ancestors = GetAncestorPaths(path);

        var paramNames = new List<string>();
        for (var i = 0; i < ancestors.Length; i++)
        {
            var paramName = $"@ancestor{i}";
            parameters[paramName] = ancestors[i];
            paramNames.Add(paramName);
        }

        var selfParam = $"@ancestor{ancestors.Length}";
        parameters[selfParam] = path;
        paramNames.Add(selfParam);

        parameters["@scopePrefix"] = $"{path}/";

        var ancestorsClause = $"n.path IN ({string.Join(", ", paramNames)})";
        var descendantsClause = "n.path LIKE @scopePrefix || '%'";

        return $"({ancestorsClause} OR {descendantsClause})";
    }

    #endregion

    #region Filter Clauses

    private string GenerateNodeClause(QueryNode node)
    {
        return node switch
        {
            QueryComparison comparison => GenerateComparisonClause(comparison.Condition),
            QueryAnd and => GenerateAndClause(and),
            QueryOr or => GenerateOrClause(or),
            _ => ""
        };
    }

    private string GenerateComparisonClause(QueryCondition condition)
    {
        var selector = MapSelector(condition.Selector);

        string NextParam()
        {
            return $"@p{_paramIndex++}";
        }

        switch (condition.Operator)
        {
            case QueryOperator.Equal:
            {
                var paramName = NextParam();
                if (IsTextField(condition.Selector))
                {
                    _parameters[paramName] = condition.Value.ToLowerInvariant();
                    return $"LOWER({selector}) = {paramName}";
                }
                _parameters[paramName] = ConvertValue(condition.Value);
                return $"{selector} = {paramName}";
            }

            case QueryOperator.NotEqual:
            {
                var paramName = NextParam();
                if (IsTextField(condition.Selector))
                {
                    _parameters[paramName] = condition.Value.ToLowerInvariant();
                    return $"LOWER({selector}) != {paramName}";
                }
                _parameters[paramName] = ConvertValue(condition.Value);
                return $"{selector} != {paramName}";
            }

            case QueryOperator.GreaterThan:
            {
                var paramName = NextParam();
                _parameters[paramName] = ConvertValue(condition.Value);
                return IsJsonbField(condition.Selector)
                    ? $"CAST({selector} AS numeric) > {paramName}"
                    : $"{selector} > {paramName}";
            }

            case QueryOperator.LessThan:
            {
                var paramName = NextParam();
                _parameters[paramName] = ConvertValue(condition.Value);
                return IsJsonbField(condition.Selector)
                    ? $"CAST({selector} AS numeric) < {paramName}"
                    : $"{selector} < {paramName}";
            }

            case QueryOperator.GreaterOrEqual:
            {
                var paramName = NextParam();
                _parameters[paramName] = ConvertValue(condition.Value);
                return IsJsonbField(condition.Selector)
                    ? $"CAST({selector} AS numeric) >= {paramName}"
                    : $"{selector} >= {paramName}";
            }

            case QueryOperator.LessOrEqual:
            {
                var paramName = NextParam();
                _parameters[paramName] = ConvertValue(condition.Value);
                return IsJsonbField(condition.Selector)
                    ? $"CAST({selector} AS numeric) <= {paramName}"
                    : $"{selector} <= {paramName}";
            }

            case QueryOperator.Like:
            {
                var paramName = NextParam();
                var pattern = condition.Value.Replace("*", "%");
                if (!pattern.Contains('%'))
                    pattern = $"%{pattern}%";
                _parameters[paramName] = pattern;
                return $"{selector} ILIKE {paramName}";
            }

            case QueryOperator.In:
                var isTextIn = IsTextField(condition.Selector);
                var inParams = new List<string>();
                foreach (var value in condition.Values)
                {
                    var inParamName = NextParam();
                    _parameters[inParamName] = isTextIn ? value.ToLowerInvariant() : ConvertValue(value);
                    inParams.Add(inParamName);
                }
                return isTextIn
                    ? $"LOWER({selector}) IN ({string.Join(", ", inParams)})"
                    : $"{selector} IN ({string.Join(", ", inParams)})";

            case QueryOperator.NotIn:
                var isTextNotIn = IsTextField(condition.Selector);
                var notInParams = new List<string>();
                foreach (var value in condition.Values)
                {
                    var notInParamName = NextParam();
                    _parameters[notInParamName] = isTextNotIn ? value.ToLowerInvariant() : ConvertValue(value);
                    notInParams.Add(notInParamName);
                }
                return isTextNotIn
                    ? $"LOWER({selector}) NOT IN ({string.Join(", ", notInParams)})"
                    : $"{selector} NOT IN ({string.Join(", ", notInParams)})";

            default:
                return "";
        }
    }

    private string GenerateAndClause(QueryAnd and)
    {
        var clauses = and.Children
            .Select(GenerateNodeClause)
            .Where(c => !string.IsNullOrEmpty(c))
            .ToList();

        if (clauses.Count == 0) return "";
        if (clauses.Count == 1) return clauses[0];
        return $"({string.Join(" AND ", clauses)})";
    }

    private string GenerateOrClause(QueryOr or)
    {
        var clauses = or.Children
            .Select(GenerateNodeClause)
            .Where(c => !string.IsNullOrEmpty(c))
            .ToList();

        if (clauses.Count == 0) return "";
        if (clauses.Count == 1) return clauses[0];
        return $"({string.Join(" OR ", clauses)})";
    }

    private string GenerateTextSearchClause(string textSearch)
    {
        // Split into terms — ALL terms must match as substrings (mirrors InMemory QueryEvaluator behavior).
        // Uses ILIKE for substring/prefix matching instead of plainto_tsquery which only matches full words.
        var terms = textSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
            return "";

        var textExpr = "COALESCE(n.name,'') || ' ' || COALESCE(n.path,'') || ' ' || COALESCE(n.description,'') || ' ' || COALESCE(n.node_type,'')";
        var clauses = new List<string>();

        foreach (var term in terms)
        {
            var paramName = $"@p{_paramIndex++}";
            _parameters[paramName] = $"%{EscapeLikePattern(term)}%";
            clauses.Add($"{textExpr} ILIKE {paramName}");
        }

        return clauses.Count == 1 ? clauses[0] : $"({string.Join(" AND ", clauses)})";
    }

    /// <summary>
    /// Escapes special LIKE/ILIKE pattern characters (%, _, \) in user input.
    /// </summary>
    private static string EscapeLikePattern(string input)
    {
        return input
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
    }

    private string GenerateAccessControlClause(string userId)
    {
        var paramName = $"@acUser{_paramIndex++}";
        _parameters[paramName] = userId;
        // Anonymous users only get their own permissions (no Public inheritance).
        // All other users also inherit Public permissions as a baseline floor.
        // NodeType definitions (node_type = 'NodeType') are always publicly readable.
        var userList = userId == WellKnownUsers.Anonymous
            ? $"{paramName}"
            : $"{paramName}, 'Public'";
        return $"""
            (
                n.node_type = 'NodeType'
                OR
                (SELECT uep.is_allow
                 FROM user_effective_permissions uep
                 WHERE uep.user_id IN ({userList})
                   AND uep.permission = 'Read'
                   AND n.main_node LIKE uep.node_path_prefix || '%'
                 ORDER BY LENGTH(uep.node_path_prefix) DESC,
                          CASE WHEN uep.user_id = {paramName} THEN 0 ELSE 1 END
                 LIMIT 1) = true
            )
            """;
    }

    #endregion

    private static bool IsJsonbField(string selector) =>
        !PropertyMap.ContainsKey(selector);

    private static object ConvertValue(string value)
    {
        if (bool.TryParse(value, out var boolVal))
            return boolVal;

        if (long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var longVal))
            return longVal;

        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var doubleVal))
            return doubleVal;

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateVal))
            return dateVal;

        return value;
    }

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
