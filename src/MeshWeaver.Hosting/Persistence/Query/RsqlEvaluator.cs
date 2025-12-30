using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MeshWeaver.AI.Completion;
using MeshWeaver.Mesh.Query;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Evaluates RSQL queries against objects in memory.
/// </summary>
public class RsqlEvaluator
{
    private readonly FuzzyScorer _fuzzyScorer;

    public RsqlEvaluator(FuzzyScorer? fuzzyScorer = null)
    {
        _fuzzyScorer = fuzzyScorer ?? new FuzzyScorer();
    }

    /// <summary>
    /// Evaluates if an object matches the parsed query.
    /// </summary>
    public bool Matches(object obj, ParsedQuery query)
    {
        // If there's a filter, evaluate it
        if (query.Filter != null && !EvaluateNode(obj, query.Filter))
            return false;

        // If there's a text search, check it matches
        if (!string.IsNullOrEmpty(query.TextSearch))
        {
            var score = GetFuzzyScore(obj, query.TextSearch);
            if (score <= int.MinValue)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Gets the fuzzy search score for an object (higher is better match).
    /// Returns int.MinValue if no match.
    /// </summary>
    public int GetFuzzyScore(object obj, string? textSearch)
    {
        if (string.IsNullOrEmpty(textSearch))
            return 0;

        var searchableText = ExtractSearchableText(obj);
        if (string.IsNullOrEmpty(searchableText))
            return int.MinValue;

        var scored = _fuzzyScorer.Score(
            [searchableText],
            textSearch,
            s => s
        ).FirstOrDefault();

        return scored?.Score ?? int.MinValue;
    }

    /// <summary>
    /// Evaluates an RSQL AST node against an object.
    /// </summary>
    private bool EvaluateNode(object obj, RsqlNode node)
    {
        return node switch
        {
            RsqlComparison comparison => EvaluateComparison(obj, comparison.Condition),
            RsqlAnd and => and.Children.All(child => EvaluateNode(obj, child)),
            RsqlOr or => or.Children.Any(child => EvaluateNode(obj, child)),
            _ => true
        };
    }

    /// <summary>
    /// Evaluates a single comparison condition against an object.
    /// </summary>
    private bool EvaluateComparison(object obj, RsqlCondition condition)
    {
        var actualValue = GetPropertyValue(obj, condition.Selector);
        return condition.Operator switch
        {
            RsqlOperator.Equal => CompareEqual(actualValue, condition.Value),
            RsqlOperator.NotEqual => !CompareEqual(actualValue, condition.Value),
            RsqlOperator.GreaterThan => CompareNumeric(actualValue, condition.Value) > 0,
            RsqlOperator.LessThan => CompareNumeric(actualValue, condition.Value) < 0,
            RsqlOperator.GreaterOrEqual => CompareNumeric(actualValue, condition.Value) >= 0,
            RsqlOperator.LessOrEqual => CompareNumeric(actualValue, condition.Value) <= 0,
            RsqlOperator.In => condition.Values.Any(v => CompareEqual(actualValue, v)),
            RsqlOperator.NotIn => !condition.Values.Any(v => CompareEqual(actualValue, v)),
            RsqlOperator.Like => CompareWildcard(actualValue, condition.Value),
            _ => false
        };
    }

    /// <summary>
    /// Gets a property value from an object, supporting nested properties (e.g., "address.city").
    /// </summary>
    public object? GetPropertyValue(object obj, string selector)
    {
        var current = obj;
        var parts = selector.Split('.');

        foreach (var part in parts)
        {
            if (current == null)
                return null;

            current = GetDirectPropertyValue(current, part);
        }

        return current;
    }

    /// <summary>
    /// Gets a direct property value from an object.
    /// </summary>
    private object? GetDirectPropertyValue(object obj, string propertyName)
    {
        if (obj is JsonElement jsonElement)
        {
            return GetJsonPropertyValue(jsonElement, propertyName);
        }

        // Use reflection for regular objects
        var type = obj.GetType();
        var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return property?.GetValue(obj);
    }

    /// <summary>
    /// Gets a property value from a JsonElement.
    /// </summary>
    private object? GetJsonPropertyValue(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        // Try exact match first, then case-insensitive
        if (element.TryGetProperty(propertyName, out var prop))
            return JsonElementToObject(prop);

        // Case-insensitive search
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                return JsonElementToObject(property.Value);
        }

        return null;
    }

    /// <summary>
    /// Converts a JsonElement to a .NET object.
    /// </summary>
    private static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => element,
            JsonValueKind.Array => element,
            _ => null
        };
    }

    /// <summary>
    /// Compares two values for equality (case-insensitive for strings).
    /// </summary>
    private static bool CompareEqual(object? actual, string expected)
    {
        if (actual == null)
            return string.IsNullOrEmpty(expected);

        var actualStr = actual.ToString();
        return actualStr?.Equals(expected, StringComparison.OrdinalIgnoreCase) ?? false;
    }

    /// <summary>
    /// Compares two values numerically. Returns -1, 0, or 1.
    /// Also handles dates.
    /// </summary>
    private static int CompareNumeric(object? actual, string expected)
    {
        if (actual == null)
            return -1;

        // Try numeric comparison
        if (TryParseNumber(actual, out var actualNum) && TryParseNumber(expected, out var expectedNum))
        {
            return actualNum.CompareTo(expectedNum);
        }

        // Try date comparison
        if (TryParseDate(actual, out var actualDate) && TryParseDate(expected, out var expectedDate))
        {
            return actualDate.CompareTo(expectedDate);
        }

        // Fall back to string comparison
        var actualStr = actual.ToString() ?? "";
        return string.Compare(actualStr, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Compares a value against a wildcard pattern (* for any characters).
    /// </summary>
    private static bool CompareWildcard(object? actual, string pattern)
    {
        if (actual == null)
            return false;

        var actualStr = actual.ToString() ?? "";

        // Convert wildcard pattern to simple contains check
        // *value* = contains, value* = starts with, *value = ends with
        var trimmedPattern = pattern.Trim('*');
        var startsWithStar = pattern.StartsWith('*');
        var endsWithStar = pattern.EndsWith('*');

        if (startsWithStar && endsWithStar)
        {
            return actualStr.Contains(trimmedPattern, StringComparison.OrdinalIgnoreCase);
        }
        else if (startsWithStar)
        {
            return actualStr.EndsWith(trimmedPattern, StringComparison.OrdinalIgnoreCase);
        }
        else if (endsWithStar)
        {
            return actualStr.StartsWith(trimmedPattern, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            return actualStr.Equals(trimmedPattern, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool TryParseNumber(object? value, out double result)
    {
        result = 0;
        if (value == null)
            return false;

        if (value is double d)
        {
            result = d;
            return true;
        }

        if (value is int i)
        {
            result = i;
            return true;
        }

        if (value is long l)
        {
            result = l;
            return true;
        }

        if (value is decimal dec)
        {
            result = (double)dec;
            return true;
        }

        return double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParseDate(object? value, out DateTimeOffset result)
    {
        result = default;
        if (value == null)
            return false;

        if (value is DateTimeOffset dto)
        {
            result = dto;
            return true;
        }

        if (value is DateTime dt)
        {
            result = new DateTimeOffset(dt);
            return true;
        }

        return DateTimeOffset.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    /// <summary>
    /// Extracts searchable text from all string properties of an object.
    /// </summary>
    private string ExtractSearchableText(object obj)
    {
        var sb = new StringBuilder();
        ExtractStrings(obj, sb, maxDepth: 3);
        return sb.ToString();
    }

    private void ExtractStrings(object? obj, StringBuilder sb, int maxDepth, int currentDepth = 0)
    {
        if (obj == null || currentDepth >= maxDepth)
            return;

        if (obj is string str)
        {
            sb.Append(str).Append(' ');
            return;
        }

        if (obj is JsonElement jsonElement)
        {
            ExtractJsonStrings(jsonElement, sb, maxDepth, currentDepth);
            return;
        }

        // Reflect over properties
        var type = obj.GetType();
        if (type.IsPrimitive || type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            try
            {
                if (property.GetMethod?.GetParameters().Length > 0)
                    continue; // Skip indexers
                var value = property.GetValue(obj);
                if (value is string s)
                {
                    sb.Append(s).Append(' ');
                }
                else if (value != null && !property.PropertyType.IsPrimitive)
                {
                    ExtractStrings(value, sb, maxDepth, currentDepth + 1);
                }
            }
            catch
            {
                // Ignore inaccessible properties
            }
        }
    }

    private void ExtractJsonStrings(JsonElement element, StringBuilder sb, int maxDepth, int currentDepth)
    {
        if (currentDepth >= maxDepth)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                sb.Append(element.GetString()).Append(' ');
                break;
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    ExtractJsonStrings(prop.Value, sb, maxDepth, currentDepth + 1);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ExtractJsonStrings(item, sb, maxDepth, currentDepth + 1);
                }
                break;
        }
    }

    /// <summary>
    /// Orders results by a property.
    /// </summary>
    /// <param name="results">The results to order</param>
    /// <param name="orderBy">The ordering clause (null means no ordering)</param>
    /// <returns>Ordered results</returns>
    public IEnumerable<T> OrderResults<T>(IEnumerable<T> results, OrderByClause? orderBy) where T : notnull
    {
        if (orderBy == null)
            return results;

        return orderBy.Descending
            ? results.OrderByDescending(x => GetComparableValue(GetPropertyValue(x, orderBy.Property)))
            : results.OrderBy(x => GetComparableValue(GetPropertyValue(x, orderBy.Property)));
    }

    /// <summary>
    /// Converts a property value to a comparable form for sorting.
    /// </summary>
    private static object? GetComparableValue(object? value)
    {
        if (value == null)
            return null;

        // Handle DateTimeOffset specially for proper sorting
        if (value is DateTimeOffset dto)
            return dto.UtcTicks;

        if (value is DateTime dt)
            return dt.Ticks;

        // For strings, return as-is for case-insensitive comparison
        if (value is string)
            return value;

        // For numbers, return as-is
        if (value is int or long or double or decimal or float)
            return value;

        // Default: convert to string
        return value.ToString();
    }

    /// <summary>
    /// Applies limit to results.
    /// </summary>
    /// <param name="results">The results to limit</param>
    /// <param name="limit">Maximum number of results (null means no limit)</param>
    /// <returns>Limited results</returns>
    public IEnumerable<T> LimitResults<T>(IEnumerable<T> results, int? limit)
    {
        return limit.HasValue && limit.Value > 0 ? results.Take(limit.Value) : results;
    }
}
