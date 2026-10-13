using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            // A `*` in a NotEqual value is a negated wildcard (`-field:val*`, #6581): the
            // complement of the glob, never a literal compare against the pattern.
            QueryOperator.NotEqual => QueryWildcard.ContainsWildcard(condition.Value)
                ? QueryWildcard.IsNotLikeMatch(actualValue?.ToString(), condition.Value)
                : !CompareEqual(actualValue, condition.Value),
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
    public object? GetPropertyValue(object obj, string selector) => GetPropertyValue(obj, selector, rawJsonNumbers: false);

    /// <summary>
    /// <see cref="GetPropertyValue(object, string)"/>, optionally leaving a JSON NUMBER as its
    /// <see cref="JsonElement"/> — the sort path's read, so <see cref="QuerySortKey"/> can take the
    /// exact decimal (beyond <see cref="long"/>, high-precision fractions) rather than the double
    /// the filter path converts to.
    /// </summary>
    private object? GetPropertyValue(object obj, string selector, bool rawJsonNumbers)
    {
        var parts = selector.Split('.');

        var current = ResolveRootSelector(obj, parts[0], rawJsonNumbers);

        for (var i = 1; i < parts.Length; i++)
        {
            if (current == null)
                return null;

            TryGetDirectPropertyValue(current, parts[i], rawJsonNumbers, out current);
        }

        return current;
    }

    /// <summary>
    /// The first hop of a selector walk: the object's own property, else the same name read out of
    /// its <c>Content</c>. See <see cref="GetPropertyValue(object, string)"/> for why the order is this way round
    /// and why absence — not nullness — opens the fallback.
    /// </summary>
    private object? ResolveRootSelector(object obj, string propertyName, bool rawJsonNumbers = false)
    {
        if (TryGetDirectPropertyValue(obj, propertyName, rawJsonNumbers, out var value))
            return value;

        // The selector names nothing on the object. SQL would read it out of the content JSONB;
        // do the same, so one query means one thing on every backend.
        if (TryGetDirectPropertyValue(obj, ContentProperty, rawJsonNumbers, out var content) && content is not null)
        {
            TryGetDirectPropertyValue(content, propertyName, rawJsonNumbers, out var fromContent);
            return fromContent;
        }

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
        TryGetDirectPropertyValue(obj, propertyName, rawJsonNumbers: false, out var value);
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
    private bool TryGetDirectPropertyValue(object obj, string propertyName, bool rawJsonNumbers, out object? value)
    {
        // Handle $type as a special case - return the CLR type name
        if (propertyName == "$type")
        {
            value = obj.GetType().Name;
            return true;
        }

        if (obj is JsonElement jsonElement)
            return TryGetJsonPropertyValue(jsonElement, propertyName, rawJsonNumbers, out value);

        if (obj is JsonObject jsonObject)
            return TryGetJsonPropertyValue(jsonObject, propertyName, rawJsonNumbers, out value);

        if (obj is JsonNode)
        {
            value = null;
            return false;
        }

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
    private static bool TryGetJsonPropertyValue(JsonElement element, string propertyName, bool rawJsonNumbers, out object? value)
    {
        value = null;

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        // Try exact match first, then case-insensitive
        if (element.TryGetProperty(propertyName, out var prop))
        {
            value = JsonElementToObject(prop, rawJsonNumbers);
            return true;
        }

        // Case-insensitive search
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = JsonElementToObject(property.Value, rawJsonNumbers);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads as-written JSON content with the same exact-then-case-insensitive lookup and scalar
    /// conversion as a deserialized <see cref="JsonElement"/>. A present JSON null remains present.
    /// </summary>
    private static bool TryGetJsonPropertyValue(JsonObject jsonObject, string propertyName, bool rawJsonNumbers, out object? value)
    {
        if (jsonObject.TryGetPropertyValue(propertyName, out var propertyValue))
        {
            value = JsonNodeToObject(propertyValue, rawJsonNumbers);
            return true;
        }

        foreach (var property in jsonObject)
        {
            if (property.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = JsonNodeToObject(property.Value, rawJsonNumbers);
                return true;
            }
        }

        value = null;
        return false;
    }

    private static object? JsonNodeToObject(JsonNode? node, bool rawJsonNumbers) =>
        node is null ? null : JsonElementToObject(JsonSerializer.SerializeToElement(node), rawJsonNumbers);

    /// <summary>
    /// Converts a JsonElement to a .NET object.
    /// </summary>
    private static object? JsonElementToObject(JsonElement element, bool rawJsonNumbers = false)
    {
        if (rawJsonNumbers && element.ValueKind == JsonValueKind.Number)
            return element;
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            // (object) on each arm: a bare `? l : GetDouble()` types the conditional as DOUBLE and
            // silently widened every integer, so 2^53 + 1 read back as 2^53.
            JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
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

        if (obj is JsonNode jsonNode)
        {
            ExtractJsonStrings(JsonSerializer.SerializeToElement(jsonNode), sb, maxDepth, currentDepth);
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
    /// Orders results by a property — TYPED and with nulls last in both directions; the contract is
    /// <see cref="QuerySortKey"/>'s. Stable, so a caller that pre-orders by path keeps path as the
    /// tiebreak.
    /// </summary>
    /// <param name="results">The results to order</param>
    /// <param name="orderBy">The ordering clause (null means no ordering)</param>
    /// <returns>Ordered results</returns>
    public IEnumerable<T> OrderResults<T>(IEnumerable<T> results, OrderByClause? orderBy)
    {
        if (orderBy == null)
            return results;

        return results.OrderBy(x => GetSortKey(x!, orderBy.Property), QuerySortKey.Comparer(orderBy.Descending));
    }

    /// <summary>
    /// The canonical sort key (<see cref="QuerySortKey.Normalize"/>) of <paramref name="obj"/> under a
    /// <c>sort:</c> selector — a node field, a <c>content.a.b</c> path, a bare content field, or one of
    /// the allow-listed functions Postgres also accepts (<c>length(x)</c>, <c>lower(x)</c>,
    /// <c>upper(x)</c>; <c>sort:length(path)-desc</c> is the routing layer's deepest-prefix pick).
    /// </summary>
    /// <param name="obj">The row — usually a <see cref="MeshNode"/>.</param>
    /// <param name="selector">The selector as the query spelled it.</param>
    /// <returns>A number, a <see cref="string"/>, or <see langword="null"/>.</returns>
    public object? GetSortKey(object obj, string selector)
    {
        var open = selector.IndexOf('(');
        if (open > 0 && selector.EndsWith(')'))
        {
            var function = selector[..open].Trim();
            var inner = QuerySortKey.Normalize(ResolveSortSelector(obj, selector[(open + 1)..^1].Trim()));
            var text = inner switch
            {
                null => null,
                string s => s,
                _ => Convert.ToString(inner, CultureInfo.InvariantCulture),
            };
            if (function.Equals("length", StringComparison.OrdinalIgnoreCase))
                return text is null ? null : (double)text.Length;
            if (function.Equals("lower", StringComparison.OrdinalIgnoreCase))
                return text?.ToLowerInvariant();
            if (function.Equals("upper", StringComparison.OrdinalIgnoreCase))
                return text?.ToUpperInvariant();
        }

        return QuerySortKey.Normalize(ResolveSortSelector(obj, selector));
    }

    /// <summary>
    /// Whether a <c>sort:</c> selector reads the node's CONTENT rather than one of its own fields —
    /// in which case a <c>select:</c> projection that omits <c>content</c> must still carry it, or the
    /// merge layer's authoritative re-sort finds every key null and falls back to path order.
    /// </summary>
    /// <param name="selector">The sort selector, possibly a function call such as <c>lower(x)</c>.</param>
    /// <returns>True when the selector resolves into <see cref="MeshNode.Content"/>.</returns>
    public static bool SortReadsContent(string? selector)
    {
        if (string.IsNullOrEmpty(selector))
            return false;
        var open = selector.IndexOf('(');
        if (open > 0 && selector.EndsWith(')'))
            selector = selector[(open + 1)..^1].Trim();
        var root = selector.Split('.')[0];
        if (root.Equals(ContentProperty, StringComparison.OrdinalIgnoreCase))
            return true;
        var property = root.Equals("display_order", StringComparison.OrdinalIgnoreCase)
            ? "order"
            : root.Replace("_", "", StringComparison.Ordinal);
        return typeof(MeshNode).GetProperty(property,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is null;
    }

    /// <summary>
    /// Resolves a sort selector, accepting the snake_case COLUMN spellings Postgres maps
    /// (<c>last_modified</c>, <c>display_order</c>, <c>node_type</c>, …) for the node field they name,
    /// so one <c>sort:</c> means one thing on every backend.
    /// </summary>
    private object? ResolveSortSelector(object obj, string selector)
    {
        var property = selector.ToLowerInvariant() switch
        {
            "last_modified" => "lastModified",
            "display_order" => "order",
            "node_type" => "nodeType",
            "main_node" => "mainNode",
            "desired_id" => "desiredId",
            "created_by" => "createdBy",
            "created_date" => "createdDate",
            "last_modified_by" => "lastModifiedBy",
            _ => selector,
        };
        return GetPropertyValue(obj, property, rawJsonNumbers: true);
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
