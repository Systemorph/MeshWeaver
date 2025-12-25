using System.Globalization;
using System.Text;
using MeshWeaver.Mesh.Query;

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

        var sql = $"SELECT * FROM {alias} {whereClause}".Trim();

        return (sql, parameters);
    }

    private string GenerateNodeClause(RsqlNode node, string alias)
    {
        return node switch
        {
            RsqlComparison comparison => GenerateComparisonClause(comparison.Condition, alias),
            RsqlAnd and => GenerateAndClause(and, alias),
            RsqlOr or => GenerateOrClause(or, alias),
            _ => ""
        };
    }

    private string GenerateComparisonClause(RsqlCondition condition, string alias)
    {
        var selector = $"{alias}.{condition.Selector}";
        var paramName = $"@p{_paramIndex++}";

        switch (condition.Operator)
        {
            case RsqlOperator.Equal:
                _parameters[paramName] = ConvertValue(condition.Value);
                return $"{selector} = {paramName}";

            case RsqlOperator.NotEqual:
                _parameters[paramName] = ConvertValue(condition.Value);
                return $"{selector} != {paramName}";

            case RsqlOperator.GreaterThan:
                _parameters[paramName] = ConvertValue(condition.Value);
                return $"{selector} > {paramName}";

            case RsqlOperator.LessThan:
                _parameters[paramName] = ConvertValue(condition.Value);
                return $"{selector} < {paramName}";

            case RsqlOperator.GreaterOrEqual:
                _parameters[paramName] = ConvertValue(condition.Value);
                return $"{selector} >= {paramName}";

            case RsqlOperator.LessOrEqual:
                _parameters[paramName] = ConvertValue(condition.Value);
                return $"{selector} <= {paramName}";

            case RsqlOperator.Like:
                // Use CONTAINS for like queries (case-insensitive)
                var pattern = condition.Value.Trim('*');
                _parameters[paramName] = pattern;
                return $"CONTAINS({selector}, {paramName}, true)";

            case RsqlOperator.In:
                // Generate IN clause with multiple parameters
                var inParams = new List<string>();
                foreach (var value in condition.Values)
                {
                    var inParamName = $"@p{_paramIndex++}";
                    _parameters[inParamName] = ConvertValue(value);
                    inParams.Add(inParamName);
                }
                return $"{selector} IN ({string.Join(", ", inParams)})";

            case RsqlOperator.NotIn:
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

    private string GenerateAndClause(RsqlAnd and, string alias)
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

    private string GenerateOrClause(RsqlOr or, string alias)
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
        // For text search, we search common string fields
        // In a real implementation, you might want to configure which fields to search
        // or use Cosmos DB full-text search capabilities
        var paramName = $"@p{_paramIndex++}";
        _parameters[paramName] = textSearch.ToLowerInvariant();

        // Search in common fields - customize based on your schema
        var searchClauses = new[]
        {
            $"CONTAINS(LOWER({alias}.name ?? \"\"), {paramName})",
            $"CONTAINS(LOWER({alias}.description ?? \"\"), {paramName})",
            $"CONTAINS(LOWER({alias}.nodeType ?? \"\"), {paramName})"
        };

        return $"({string.Join(" OR ", searchClauses)})";
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
