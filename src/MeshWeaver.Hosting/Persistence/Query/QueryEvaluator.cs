using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MeshWeaver.Data.Completion;
using MeshWeaver.Mesh;


namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Evaluates queries against objects in memory.
/// </summary>
public class QueryEvaluator
{
    private readonly FuzzyScorer _fuzzyScorer;

    /// <summary>
    /// Creates an in-memory query evaluator, using the supplied fuzzy scorer
    /// for text-search ranking or a default <c>FuzzyScorer</c> when none is
    /// provided.
    /// </summary>
    /// <param name="fuzzyScorer">Optional scorer for ranking text-search matches; a default is created when <see langword="null"/>.</param>
    public QueryEvaluator(FuzzyScorer? fuzzyScorer = null)
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
    /// For text search terms, we require substring containment (not fuzzy character matching).
    /// </summary>
    public int GetFuzzyScore(object obj, string? textSearch)
    {
        if (string.IsNullOrEmpty(textSearch))
            return 0;

        var searchableText = ExtractSearchableText(obj);
        if (string.IsNullOrEmpty(searchableText))
            return int.MinValue;

        // Split search into individual terms - ALL terms must be found as substrings
        var terms = textSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var term in terms)
        {
            // Require substring containment for each term (case-insensitive)
            if (!searchableText.Contains(term, StringComparison.OrdinalIgnoreCase))
                return int.MinValue;
        }

        // Use fuzzy scoring for ranking matched results
        var scored = _fuzzyScorer.Score(
            [searchableText],
            textSearch,
            s => s
        ).FirstOrDefault();

        return scored?.Score ?? 0;
    }

    /// <summary>
    /// Evaluates a query AST node against an object.
    /// </summary>
    private bool EvaluateNode(object obj, QueryNode node)
    {
        return node switch
        {
            QueryComparison comparison => EvaluateComparison(obj, comparison.Condition),
            QueryAnd and => and.Children.All(child => EvaluateNode(obj, child)),
            QueryOr or => or.Children.Any(child => EvaluateNode(obj, child)),
            _ => true
        };
    }

    /// <summary>
    /// Evaluates a single comparison condition against an object.
    /// </summary>
    private bool EvaluateComparison(object obj, QueryCondition condition)
    {
        var actualValue = GetPropertyValue(obj, condition.Selector);
        return condition.Operator switch
        {
            QueryOperator.Equal => CompareEqual(actualValue, condition.Value),
            QueryOperator.NotEqual => !CompareEqual(actualValue, condition.Value),
            QueryOperator.GreaterThan => CompareNumeric(actualValue, condition.Value) > 0,
            QueryOperator.LessThan => CompareNumeric(actualValue, condition.Value) < 0,
            QueryOperator.GreaterOrEqual => CompareNumeric(actualValue, condition.Value) >= 0,
            QueryOperator.LessOrEqual => CompareNumeric(actualValue, condition.Value) <= 0,
            QueryOperator.In => condition.Values.Any(v => CompareEqual(actualValue, v)),
            QueryOperator.NotIn => !condition.Values.Any(v => CompareEqual(actualValue, v)),
            QueryOperator.Like => CompareWildcard(actualValue, condition.Value),
            _ => false
        };
    }

    /// <summary>
    /// The property a selector falls back into when it names nothing on the object itself.
    /// Resolved case-insensitively like every other selector hop, so it matches
    /// <see cref="MeshNode.Content"/> however the query spelled it.
    /// </summary>
    private const string ContentProperty = "Content";

    /// <summary>
    /// Gets a property value from an object, supporting nested properties (e.g., "address.city"),
    /// and — on the FIRST hop only — falling back into the object's <c>Content</c> when the
    /// selector names nothing on the object itself.
    ///
    /// <para>🚨 <b>The fallback is what makes this evaluator agree with the SQL provider</b>
    /// (Systemorph/MeshWeaver#3511). A query is one language with two implementations: this one,
    /// which every in-memory, FileSystem and static-node host runs, and
    /// <c>PostgreSqlSqlGenerator.MapSelector</c>, which every portal runs. The SQL side resolves a
    /// selector as <i>known column, else <c>content.X</c> walk, else <c>n.content-&gt;&gt;'X'</c></i>
    /// — so an unknown selector reads the content JSONB there. Without the fallback this side
    /// answered <c>null</c> instead, and the two providers silently disagreed about which selectors
    /// exist: <c>compilationStatus:Error</c> discriminated on a portal (measured on a live mesh
    /// 2026-09-07: 5 Error, 195 Ok, disjoint) and matched NOTHING here, which is the shape where
    /// "it never ran" and "it passed" paint the same colour.</para>
    ///
    /// <para><b>Ordering — the object's own property WINS, content is only the fallback</b>, which
    /// is <c>MapSelector</c>'s order (<c>PropertyMap</c> is consulted before the
    /// <c>n.content-&gt;&gt;</c> default) and the only order that changes nothing that works today.
    /// Content-first would silently re-point every live query whose selector names both — and
    /// <c>name</c>, <c>description</c>, <c>category</c>, <c>icon</c>, <c>order</c>, <c>state</c> and
    /// <c>version</c> are common content field names, so <c>name:Foo</c> would start filtering on
    /// the content's name for a large part of the mesh. This way round, the ONLY selectors whose
    /// resolution moves are the ones that resolved to <c>null</c> before — i.e. the ones that
    /// matched nothing.</para>
    ///
    /// <para>🚨 <b>The fallback keys on the property being ABSENT, never on its value being
    /// null</b> — see <see cref="TryGetDirectPropertyValue"/>. A node with no description must keep
    /// answering <c>null</c> for <c>description</c> rather than reaching into its content, because
    /// SQL answers the <c>n.description</c> column there and never falls through.</para>
    ///
    /// <para><b>First hop only</b>, again for parity: SQL's default is a single root-level
    /// <c>n.content-&gt;&gt;'X'</c>, so <c>content.status</c> walks <c>Content</c> then
    /// <c>status</c> and a miss deeper in the walk stays a miss rather than re-entering some
    /// nested object's own <c>Content</c>.</para>
    /// </summary>
    public object? GetPropertyValue(object obj, string selector)
    {
        var parts = selector.Split('.');

        var current = ResolveRootSelector(obj, parts[0]);

        for (var i = 1; i < parts.Length; i++)
        {
            if (current == null)
                return null;

            current = GetDirectPropertyValue(current, parts[i]);
        }

        return current;
    }

    /// <summary>
    /// The first hop of a selector walk: the object's own property, else the same name read out of
    /// its <c>Content</c>. See <see cref="GetPropertyValue"/> for why the order is this way round
    /// and why absence — not nullness — opens the fallback.
    /// </summary>
    private object? ResolveRootSelector(object obj, string propertyName)
    {
        if (TryGetDirectPropertyValue(obj, propertyName, out var value))
            return value;

        // The selector names nothing on the object. SQL would read it out of the content JSONB;
        // do the same, so one query means one thing on every backend.
        if (TryGetDirectPropertyValue(obj, ContentProperty, out var content) && content is not null)
            return GetDirectPropertyValue(content, propertyName);

        return null;
    }

    /// <summary>
    /// Gets a direct property value from an object; <see langword="null"/> when the object carries
    /// no such property, which is indistinguishable from a property whose value IS null. Callers
    /// that must tell those apart — the content fallback does — use
    /// <see cref="TryGetDirectPropertyValue"/>.
    /// </summary>
    private object? GetDirectPropertyValue(object obj, string propertyName)
    {
        TryGetDirectPropertyValue(obj, propertyName, out var value);
        return value;
    }

    /// <summary>
    /// Resolves <paramref name="propertyName"/> on <paramref name="obj"/>, reporting whether the
    /// property EXISTS separately from what it holds.
    ///
    /// <para>The distinction is the whole point: <c>false</c> means "this object has no such
    /// member", which is what opens the content fallback, while <c>true</c> with a
    /// <see langword="null"/> value means "it has one and it is empty", which must NOT.</para>
    /// </summary>
    private bool TryGetDirectPropertyValue(object obj, string propertyName, out object? value)
    {
        // Handle $type as a special case - return the CLR type name
        if (propertyName == "$type")
        {
            value = obj.GetType().Name;
            return true;
        }

        if (obj is JsonElement jsonElement)
            return TryGetJsonPropertyValue(jsonElement, propertyName, out value);

        // Use reflection for regular objects
        var type = obj.GetType();
        var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property is null)
        {
            value = null;
            return false;
        }

        value = property.GetValue(obj);
        return true;
    }

    /// <summary>
    /// Gets a property value from a JsonElement, reporting presence separately from content — the
    /// JSON counterpart of <see cref="TryGetDirectPropertyValue"/>. A JSON <c>null</c> is PRESENT.
    /// </summary>
    private static bool TryGetJsonPropertyValue(JsonElement element, string propertyName, out object? value)
    {
        value = null;

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        // Try exact match first, then case-insensitive
        if (element.TryGetProperty(propertyName, out var prop))
        {
            value = JsonElementToObject(prop);
            return true;
        }

        // Case-insensitive search
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = JsonElementToObject(property.Value);
                return true;
            }
        }

        return false;
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
    /// Compares a value against a <see cref="QueryOperator.Like"/> pattern, in the query language's
    /// one wildcard vocabulary: <c>*</c> (see <see cref="QueryWildcard"/>).
    ///
    /// <para>🚨 <b>Issue #1235.</b> This used to hand-roll four cases — leading star, trailing star,
    /// both, neither — off <c>pattern.Trim('*')</c>. That made it wrong twice over. It understood
    /// only <c>*</c> while <see cref="QueryParser"/> emitted a wildcard NAMESPACE as SQL's <c>%</c>,
    /// so <c>%/Source</c> hit neither star branch and fell through to an equality test against the
    /// literal string — a wildcard-namespace filter matched NOTHING in memory, silently. And
    /// <c>Trim</c> cannot see an INTERIOR wildcard, so the two-wildcard pattern #1232 introduced
    /// (<c>*/Source/*</c>) was mangled as well. Both are now one call into the shared glob, which
    /// handles any number of wildcards and is the same code <c>PathMatcher</c> runs — the fork
    /// existed because there were two readers of one vocabulary.</para>
    ///
    /// <para>A pattern with NO wildcard is a CONTAINS test, matching what the SQL generators do
    /// (they wrap a bare pattern as <c>%p%</c> before binding it to <c>ILIKE</c>), so the in-memory
    /// and SQL paths give the same answer for the hand-built filters that use that shape.</para>
    /// </summary>
    private static bool CompareWildcard(object? actual, string pattern) =>
        actual != null && QueryWildcard.IsLikeMatch(actual.ToString(), pattern);

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

        // For MeshNode, explicitly include Path (computed property) for path-based search
        if (obj is MeshNode meshNode)
        {
            sb.Append(meshNode.Path).Append(' ');
        }

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
    public IEnumerable<T> OrderResults<T>(IEnumerable<T> results, OrderByClause? orderBy)
    {
        if (orderBy == null)
            return results;

        return orderBy.Descending
            ? results.OrderByDescending(x => GetComparableValue(GetPropertyValue(x!, orderBy.Property)), SelectorValueComparer.Instance)
            : results.OrderBy(x => GetComparableValue(GetPropertyValue(x!, orderBy.Property)), SelectorValueComparer.Instance);
    }

    /// <summary>
    /// Orders sort keys TOTALLY — <c>Comparer&lt;object&gt;.Default</c> is not, and a sort key
    /// read out of a node's content is not type-uniform.
    ///
    /// <para>🚨 <b>Why this is required rather than defensive.</b> <c>sort:</c> resolves through
    /// <see cref="GetPropertyValue"/>, so the content fallback widened it from "node fields and
    /// explicitly dotted <c>content.X</c>" to "any selector". JSON has no schema: the same key is
    /// a string on one node and a number on another, and <c>Comparer&lt;object&gt;.Default</c>
    /// answers that pair with <c>InvalidOperationException: Failed to compare two elements in the
    /// array</c> — so a widened selector would turn a merely differently-ordered query into a
    /// FAILED one, on the merge path (<c>MeshQuery.ClipMergedInitial</c>) that every backend runs.
    /// The same latent hole already swallowed <c>int</c> against <c>long</c>. Postgres has no
    /// equivalent hazard: <c>n.content-&gt;&gt;'X'</c> is always TEXT.</para>
    ///
    /// <para><b>Uniform keys keep their existing order exactly</b> — same type and
    /// <see cref="IComparable"/> is dispatched first and untouched, so strings still sort as
    /// strings and ticks still sort chronologically. Only pairs the default comparer REFUSED are
    /// newly decided: mixed numerics numerically, and anything else by its invariant string form.</para>
    /// </summary>
    private sealed class SelectorValueComparer : IComparer<object?>
    {
        public static readonly SelectorValueComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            // Nulls first, matching the default comparer's placement of an absent key.
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            // Uniform keys: the pre-existing behaviour, unchanged.
            if (x.GetType() == y.GetType() && x is IComparable sameType)
                return sameType.CompareTo(y);

            // Mixed numerics (int vs long, long vs double): compare as numbers, not as text.
            if (IsNumeric(x) && IsNumeric(y))
                return Convert.ToDouble(x, CultureInfo.InvariantCulture)
                    .CompareTo(Convert.ToDouble(y, CultureInfo.InvariantCulture));

            // Genuinely different kinds — a string against a number. Ordinal on the invariant
            // form: deterministic, and never resolved from the ambient culture.
            return string.CompareOrdinal(
                Convert.ToString(x, CultureInfo.InvariantCulture),
                Convert.ToString(y, CultureInfo.InvariantCulture));
        }

        private static bool IsNumeric(object value) =>
            value is int or long or double or decimal or float or short or byte or uint or ulong;
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
