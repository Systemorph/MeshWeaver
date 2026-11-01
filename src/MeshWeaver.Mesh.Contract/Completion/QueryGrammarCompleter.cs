using MeshWeaver.Mesh.Services.LanguageServer;

namespace MeshWeaver.Mesh.Completion;

/// <summary>
/// A node a completion can name — a NodeType, a reference member, a path, a search hit.
/// </summary>
/// <param name="Path">The node's path.</param>
/// <param name="Name">Its display name, when it has one.</param>
/// <param name="NodeType">Its NodeType, when known.</param>
/// <param name="Icon">Its icon, when known.</param>
/// <param name="Score">The source's relevance score (higher is better), when the source ranks.</param>
public sealed record QueryNodeFact(string Path, string? Name, string? NodeType = null, string? Icon = null, double Score = 0);

/// <summary>
/// What the mesh told the completer about the query's context: the NodeType catalog, the content
/// schema of the NodeType(s) in context, and the members a reference property may hold. Each list is
/// filled only when the token under the caret needs it; an empty list means "nothing known", never an error.
/// </summary>
public sealed record QueryCompletionFacts
{
    /// <summary>No facts — the grammar alone.</summary>
    public static readonly QueryCompletionFacts None = new();

    /// <summary>NodeTypes (for <c>nodeType:</c> values).</summary>
    public IReadOnlyList<QueryNodeFact> NodeTypes { get; init; } = [];

    /// <summary>Content properties of the NodeType(s) in context (for <c>content.</c>, <c>sort:content.</c>).</summary>
    public IReadOnlyList<ContentPropertyFact> ContentProperties { get; init; } = [];

    /// <summary>Nodes a path-valued qualifier or a reference property can take.</summary>
    public IReadOnlyList<QueryNodeFact> ReferenceValues { get; init; } = [];

    /// <summary>
    /// The date a date-valued field completes with (<c>lastModified:&gt;=2026-11-01</c>). Null reads the
    /// UTC clock; a caller that owns a clock (<see cref="QueryCompleter"/>, a test) states it.
    /// </summary>
    public DateOnly? Today { get; init; }
}

/// <summary>
/// One suggestion for the token under the caret.
/// </summary>
/// <param name="Label">What the suggest list shows.</param>
/// <param name="InsertText">What replaces <see cref="QueryCompletionResult.ReplaceStart"/>..<see cref="QueryCompletionResult.ReplaceEnd"/>.</param>
/// <param name="Kind">The LSP kind (drives the icon).</param>
/// <param name="CategoryKey">Localization key of the group the item belongs to (shown on the right).</param>
/// <param name="DescriptionKey">Localization key of a platform-owned description (follows the viewer's language).</param>
/// <param name="Description">An authored description (a property's XML doc, a node's name) — rendered as written.</param>
/// <param name="Path">The node path, for items that name a node.</param>
/// <param name="Icon">The node icon, for items that name a node.</param>
/// <param name="Rank">Order key, higher first. The completer's ORDER is the ranking; clients must not re-sort.</param>
public sealed record QueryCompletionItem(
    string Label,
    string InsertText,
    CompletionKind Kind,
    string CategoryKey,
    string? DescriptionKey = null,
    string? Description = null,
    string? Path = null,
    string? Icon = null,
    int Rank = 0);

/// <summary>
/// The suggestions for one caret position, and the span an accepted one replaces.
/// </summary>
/// <param name="ReplaceStart">Start offset (in the visible text) of the span an accepted item replaces.</param>
/// <param name="ReplaceEnd">End offset of that span — the caret.</param>
/// <param name="Items">The suggestions, best first.</param>
public sealed record QueryCompletionResult(int ReplaceStart, int ReplaceEnd, IReadOnlyList<QueryCompletionItem> Items)
{
    /// <summary>No suggestions at <paramref name="caret"/>.</summary>
    /// <param name="caret">The caret offset.</param>
    /// <returns>An empty result anchored at the caret.</returns>
    public static QueryCompletionResult Empty(int caret) => new(caret, caret, []);
}

/// <summary>
/// The GRAMMAR half of query autocomplete: given the classified token under the caret and the facts the
/// mesh supplied, the words the query language accepts there. Pure and deterministic — every rule is a
/// unit test in <c>QueryGrammarCompletionTest</c>; the reactive half that gathers the facts and adds the
/// node and semantic suggestions is <see cref="QueryCompleter"/>.
/// </summary>
public static class QueryGrammarCompleter
{
    /// <summary>Category key: a reserved qualifier or node field.</summary>
    public const string QualifierCategory = "query.completion.category.qualifier";
    /// <summary>Category key: a fixed value of a qualifier.</summary>
    public const string ValueCategory = "query.completion.category.value";
    /// <summary>Category key: a content property.</summary>
    public const string ContentCategory = "query.completion.category.content";
    /// <summary>Category key: a NodeType.</summary>
    public const string NodeTypeCategory = "query.completion.category.nodeType";
    /// <summary>Category key: a node (a path, a reference member, a name match).</summary>
    public const string NodeCategory = "query.completion.category.node";
    /// <summary>Category key: a semantic (vector-index) match.</summary>
    public const string SemanticCategory = "query.completion.category.semantic";

    private const int GrammarRank = 3000;

    /// <summary>
    /// The grammar's suggestions for <paramref name="token"/>, best first, at most <paramref name="limit"/>.
    /// </summary>
    /// <param name="token">The token under the caret (<see cref="QueryCaret.At"/>).</param>
    /// <param name="facts">What the mesh supplied about the context.</param>
    /// <param name="limit">Maximum number of items.</param>
    /// <returns>The suggestions; empty when the grammar has nothing to say here.</returns>
    public static IReadOnlyList<QueryCompletionItem> Complete(QueryCaretToken token, QueryCompletionFacts facts, int limit = 20)
    {
        var items = token.Kind switch
        {
            QueryCaretKind.Keyword => Keywords(token, facts),
            QueryCaretKind.Value => Values(token, facts),
            QueryCaretKind.ContentProperty => ContentProperties(token.Prefix, facts, insertPrefix: "", GrammarRank),
            QueryCaretKind.ContentValue => ContentValues(token, facts),
            _ => [],
        };
        return items
            .OrderByDescending(i => i.Rank)
            .Take(Math.Max(0, limit))
            .ToArray();
    }

    /// <summary>
    /// How well <paramref name="candidate"/> matches what was typed: 3 a prefix, 2 a prefix of a
    /// path, dotted or spaced segment (<c>Oppo</c> in <c>Crm/Opportunity</c>), -1 no match — a mid-word
    /// substring is noise (<c>an</c> would offer <c>descendants</c>). Case-insensitive; an empty prefix
    /// matches everything at 1.
    /// </summary>
    /// <param name="prefix">What was typed.</param>
    /// <param name="candidate">The word offered.</param>
    /// <returns>The match score, or -1.</returns>
    public static int Match(string prefix, string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
            return -1;
        if (prefix.Length == 0)
            return 1;
        if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return 3;
        for (var idx = candidate.IndexOf(prefix, 1, StringComparison.OrdinalIgnoreCase);
             idx > 0;
             idx = idx + 1 < candidate.Length ? candidate.IndexOf(prefix, idx + 1, StringComparison.OrdinalIgnoreCase) : -1)
            if (candidate[idx - 1] is '/' or '.' or '_' or ' ' or '-')
                return 2;
        return -1;
    }

    /// <summary>
    /// How well a FIELD NAME matches what was typed — a contains match, ranked: 3 a prefix
    /// (<c>last</c> in <c>lastModified</c>), 2 the start of a camelCase word part or of a dotted, dashed or
    /// underscored segment (<c>date</c> in <c>createdDate</c>, <c>due</c> in <c>followUpDue</c>), 1 anywhere
    /// else in the name (<c>due</c> in <c>overdue</c>), -1 no match. Case-insensitive; an empty prefix
    /// matches everything at 1. Field names are few and the user asked for the field by a word it
    /// contains, so a mid-word hit is a real candidate here — unlike a fixed VALUE, where
    /// <see cref="Match"/> keeps the stricter rule.
    /// </summary>
    /// <param name="prefix">What was typed.</param>
    /// <param name="candidate">The field name offered.</param>
    /// <returns>The match score, or -1.</returns>
    public static int FieldMatch(string prefix, string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
            return -1;
        if (prefix.Length == 0)
            return 1;
        var best = -1;
        for (var idx = candidate.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
             idx >= 0;
             idx = idx + 1 < candidate.Length ? candidate.IndexOf(prefix, idx + 1, StringComparison.OrdinalIgnoreCase) : -1)
        {
            if (idx == 0)
                return 3;
            var atWordPart = candidate[idx - 1] is '/' or '.' or '_' or ' ' or '-'
                             || (char.IsUpper(candidate[idx]) && !char.IsUpper(candidate[idx - 1]));
            best = Math.Max(best, atWordPart ? 2 : 1);
        }
        return best;
    }

    /// <summary>
    /// How well a NODE matches what was typed for a reference value: a case-insensitive contains match on
    /// its name OR its path, ranked 3 a prefix, 2 the start of a word or path segment, 1 anywhere else,
    /// -1 neither. An empty prefix matches everything at 1.
    /// </summary>
    /// <param name="prefix">What was typed.</param>
    /// <param name="node">The node offered.</param>
    /// <returns>The match score, or -1.</returns>
    public static int NodeMatch(string prefix, QueryNodeFact node)
        => Math.Max(FieldMatch(prefix, node.Name), FieldMatch(prefix, node.Path));

    /// <summary>
    /// <paramref name="value"/> as it must be written in a query: bare when it is one plain token, quoted as a
    /// literal (<see cref="QueryParser.QuoteLiteral"/>) when it carries whitespace or query syntax.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="quoted">The caret is already inside an open quote — always quote.</param>
    /// <returns>The text to insert.</returns>
    public static string ValueText(string value, bool quoted = false)
        => quoted || value.Any(c => char.IsWhiteSpace(c) || c is '|' or '*' or '(' or ')' or '"' or '\'' or ':')
            ? QueryParser.QuoteLiteral(value)
            : value;

    private static IEnumerable<QueryCompletionItem> Keywords(QueryCaretToken token, QueryCompletionFacts facts)
    {
        var prefix = token.Prefix;
        foreach (var q in QueryGrammar.Qualifiers)
        {
            // A negated term is a FILTER exclusion; only qualifiers that filter can be negated.
            if (token.Negated && !(q.Name is QueryGrammar.NodeType or QueryGrammar.Namespace or QueryGrammar.Path))
                continue;
            var m = FieldMatch(prefix, q.Name);
            if (m < 0) continue;
            yield return new QueryCompletionItem(q.Name + ":", q.Name + ":", CompletionKind.Keyword,
                QualifierCategory, q.DescriptionKey, Rank: GrammarRank + 100 * m);
        }
        foreach (var f in QueryGrammar.NodeFields)
        {
            var m = FieldMatch(prefix, f.Name);
            if (m < 0) continue;
            yield return new QueryCompletionItem(f.Name + ":", f.Name + ":", CompletionKind.Field,
                QualifierCategory, f.DescriptionKey, Rank: GrammarRank + 100 * m - 10);
        }
        var contentWord = QueryCaret.ContentPrefix.TrimEnd('.');
        var cm = FieldMatch(prefix, contentWord);
        if (cm >= 0)
            yield return new QueryCompletionItem(QueryCaret.ContentPrefix, QueryCaret.ContentPrefix, CompletionKind.Module,
                ContentCategory, "query.completion.content", Rank: GrammarRank + 100 * cm - 20);
        // Typing a content property's name directly offers `content.<name>:` (below the qualifiers).
        if (prefix.Length > 0)
            foreach (var item in ContentProperties(prefix, facts, QueryCaret.ContentPrefix, GrammarRank - 200))
                yield return item;
    }

    private static IEnumerable<QueryCompletionItem> Values(QueryCaretToken token, QueryCompletionFacts facts)
    {
        var field = token.Field ?? "";
        IReadOnlyList<QueryGrammarWord>? fixedWords = field.ToLowerInvariant() switch
        {
            "scope" => QueryGrammar.ScopeValues,
            "limit" => QueryGrammar.LimitValues,
            "partitions" => QueryGrammar.PartitionValues,
            "is" => QueryGrammar.IsValues,
            "source" => QueryGrammar.SourceValues,
            "select" => QueryGrammar.SelectColumns,
            _ => null,
        };
        if (fixedWords is not null)
            return Fixed(token, fixedWords);
        if (field.Equals(QueryGrammar.Sort, StringComparison.OrdinalIgnoreCase))
            return SortValues(token, facts);
        if (QueryGrammar.IsDateField(field))
            return Dates(token, facts);
        if (field.Equals(QueryGrammar.NodeType, StringComparison.OrdinalIgnoreCase))
            return Nodes(token, facts.NodeTypes, NodeTypeCategory, CompletionKind.Class);
        if (QueryGrammar.TakesPath(field))
            return Nodes(token, facts.ReferenceValues, NodeCategory, CompletionKind.Folder);
        return [];
    }

    private static IEnumerable<QueryCompletionItem> Fixed(QueryCaretToken token, IReadOnlyList<QueryGrammarWord> words)
    {
        var order = 0;
        foreach (var w in words)
        {
            order++;
            var m = Match(token.Prefix, w.Name);
            if (m < 0) continue;
            yield return new QueryCompletionItem(w.Name, ValueText(w.Name, token.Quoted), CompletionKind.EnumMember,
                ValueCategory, w.DescriptionKey, Rank: GrammarRank + 100 * m - order);
        }
    }

    private static IEnumerable<QueryCompletionItem> SortValues(QueryCaretToken token, QueryCompletionFacts facts)
    {
        var fields = QueryGrammar.SortFields
            .Select(f => (Name: f.Name, Key: (string?)f.DescriptionKey, Description: (string?)null))
            .Concat(facts.ContentProperties
                .Where(p => p.JsonType is not ("object" or "array"))
                .Select(p => (Name: QueryCaret.ContentPrefix + p.Name, Key: (string?)null, p.Description)))
            .ToArray();

        // `sort:lastModified-` → the directions of THAT field.
        var dash = token.Prefix.LastIndexOf('-');
        if (dash > 0)
        {
            var baseField = token.Prefix[..dash];
            var dirPrefix = token.Prefix[(dash + 1)..];
            var known = fields.FirstOrDefault(f => f.Name.Equals(baseField, StringComparison.OrdinalIgnoreCase));
            if (known.Name is not null)
            {
                var order = 0;
                foreach (var d in QueryGrammar.SortDirections)
                {
                    order++;
                    if (Match(dirPrefix, d.Name) < 0) continue;
                    var text = known.Name + "-" + d.Name;
                    yield return new QueryCompletionItem(text, text, CompletionKind.EnumMember, ValueCategory,
                        d.DescriptionKey, Rank: GrammarRank + 300 - order);
                }
                yield break;
            }
        }

        var i = 0;
        foreach (var f in fields)
        {
            i++;
            var m = FieldMatch(token.Prefix, f.Name);
            if (m < 0) continue;
            var kind = f.Name.StartsWith(QueryCaret.ContentPrefix, StringComparison.Ordinal)
                ? CompletionKind.Property : CompletionKind.Field;
            var category = kind == CompletionKind.Property ? ContentCategory : ValueCategory;
            yield return new QueryCompletionItem(f.Name, f.Name, kind, category, f.Key, f.Description,
                Rank: GrammarRank + 100 * m - 2 * i);
            yield return new QueryCompletionItem(f.Name + "-desc", f.Name + "-desc", kind, category,
                "query.completion.sort.desc", f.Description, Rank: GrammarRank + 100 * m - 2 * i - 1);
        }
    }

    private static IEnumerable<QueryCompletionItem> Nodes(
        QueryCaretToken token, IReadOnlyList<QueryNodeFact> nodes, string category, CompletionKind kind)
    {
        var i = 0;
        foreach (var n in nodes)
        {
            i++;
            var m = Math.Max(Match(token.Prefix, n.Path), Match(token.Prefix, n.Name));
            if (m < 0) continue;
            yield return new QueryCompletionItem(n.Path, ValueText(n.Path, token.Quoted), kind, category,
                Description: n.Name is { Length: > 0 } name && !name.Equals(n.Path, StringComparison.Ordinal) ? name : null,
                Path: n.Path, Icon: n.Icon, Rank: GrammarRank + 100 * m - i);
        }
    }

    private static IEnumerable<QueryCompletionItem> ContentProperties(
        string prefix, QueryCompletionFacts facts, string insertPrefix, int baseRank)
    {
        var i = 0;
        foreach (var p in facts.ContentProperties)
        {
            i++;
            // A property is also found by the KIND of value it holds: `date` / `time` find the
            // date-valued properties (`format: date-time`) whatever they are called.
            var m = Math.Max(FieldMatch(prefix, p.Name),
                prefix.Length > 0 && p.Format is { } format && format.Contains(prefix, StringComparison.OrdinalIgnoreCase) ? 1 : -1);
            if (m < 0) continue;
            var text = insertPrefix + p.Name + ":";
            yield return new QueryCompletionItem(text, text, CompletionKind.Property, ContentCategory,
                Description: Describe(p), Rank: baseRank + 100 * m - i);
        }
    }

    private static IEnumerable<QueryCompletionItem> ContentValues(QueryCaretToken token, QueryCompletionFacts facts)
    {
        var prop = facts.ContentProperties.FirstOrDefault(p =>
            p.Name.Equals(token.Field, StringComparison.OrdinalIgnoreCase));
        // No schema read for the property: nothing is known of its values, but the contains filter is
        // still a well-formed query over whatever it holds.
        if (prop is null)
            return Contains(token, null);
        if (IsDate(prop))
            return Dates(token, facts);
        if (prop.EnumValues.Count > 0)
            return Fixed(token, prop.EnumValues.Select(v => new QueryGrammarWord(v, "query.completion.enumMember")).ToArray());
        if (prop.IsBoolean)
            return Fixed(token, [new("true", "query.completion.boolean"), new("false", "query.completion.boolean")]);
        var contains = Contains(token, prop);
        if (prop.ReferenceQueries.Count > 0)
            return contains.Concat(References(token, facts.ReferenceValues));
        return contains;
    }

    /// <summary>
    /// The literal contains FILTER for what was typed — <c>content.cedent:ha</c> offers <c>*ha*</c> first.
    /// The query language's <c>field:value</c> is an exact match by contract; <c>*x*</c> is its contains
    /// spelling (a <c>Like</c> glob every executor evaluates), so this gives the user the contains query
    /// without changing what <c>field:value</c> means. Offered only for a text value typed bare (no
    /// operator, no quote, no query syntax).
    /// </summary>
    private static IEnumerable<QueryCompletionItem> Contains(QueryCaretToken token, ContentPropertyFact? prop)
    {
        var p = token.Prefix;
        if (p.Length == 0 || token.Quoted || token.Operator.Length > 0
            || prop is { IsBoolean: true } or { IsNumber: true } or { EnumValues.Count: > 0 }
            || !p.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/'))
            yield break;
        var text = QueryWildcard.Wildcard + p + QueryWildcard.Wildcard;
        yield return new QueryCompletionItem(text, text, CompletionKind.Operator, ValueCategory,
            QueryGrammar.ContainsKey, Rank: GrammarRank + 1000);
    }

    /// <summary>
    /// The members a reference property may hold, matched by CONTAINS on the member's name or path
    /// (<see cref="NodeMatch"/>), labelled with the member's name; accepting one inserts the stored value
    /// — the path — quoted per the literal rules.
    /// </summary>
    private static IEnumerable<QueryCompletionItem> References(QueryCaretToken token, IReadOnlyList<QueryNodeFact> nodes)
    {
        var i = 0;
        foreach (var n in nodes)
        {
            i++;
            var m = NodeMatch(token.Prefix, n);
            if (m < 0) continue;
            var named = n.Name is { Length: > 0 } name && !name.Equals(n.Path, StringComparison.Ordinal);
            yield return new QueryCompletionItem(named ? n.Name! : n.Path, ValueText(n.Path, token.Quoted),
                CompletionKind.Reference, NodeCategory, Description: named ? n.Path : null,
                Path: n.Path, Icon: n.Icon, Rank: GrammarRank + 100 * m - i);
        }
    }

    private static bool IsDate(ContentPropertyFact prop)
        => prop.Format is "date" or "date-time";

    /// <summary>
    /// A date value with nothing typed yet (or only an operator): the ordered comparisons against today's
    /// date as an ISO literal — <c>&gt;2026-11-01</c> after, <c>&lt;</c> before, <c>&gt;=</c> on or after,
    /// <c>&lt;=</c> on or before. After a typed <c>&gt;</c> / <c>&lt;</c> the date is offered for that
    /// operator and for its <c>=</c> extension; after <c>&gt;=</c> / <c>&lt;=</c>, for that operator alone.
    /// No relative-date syntax: the parser compares literals.
    /// </summary>
    private static IEnumerable<QueryCompletionItem> Dates(QueryCaretToken token, QueryCompletionFacts facts)
    {
        if (token.Prefix.Length > 0 || token.Quoted)
            yield break;
        var date = (facts.Today ?? DateOnly.FromDateTime(DateTime.UtcNow))
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var order = 0;
        foreach (var c in QueryGrammar.DateComparisons)
        {
            order++;
            if (!c.Name.StartsWith(token.Operator, StringComparison.Ordinal))
                continue;
            var insert = c.Name[token.Operator.Length..] + date;
            yield return new QueryCompletionItem(c.Name + date, insert, CompletionKind.Operator, ValueCategory,
                c.DescriptionKey, Rank: GrammarRank + 300 - order);
        }
    }

    private static string? Describe(ContentPropertyFact p)
    {
        var type = p.EnumValues.Count > 0 ? string.Join(" | ", p.EnumValues.Take(6)) + (p.EnumValues.Count > 6 ? " | …" : "")
            : p.ReferenceQueries.Count > 0 ? "→ " + p.ReferenceQueries[0]
            : p.Format ?? p.JsonType;
        return (type, p.Description) switch
        {
            (null, null) => null,
            (null, var d) => d,
            (var t, null) => t,
            var (t, d) => t + " — " + d,
        };
    }
}
