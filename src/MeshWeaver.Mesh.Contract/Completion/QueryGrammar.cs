namespace MeshWeaver.Mesh.Completion;

/// <summary>
/// One word the query language knows: a qualifier, a node field, or a qualifier's value.
/// </summary>
/// <param name="Name">The word as typed (<c>nodeType</c>, <c>descendants</c>).</param>
/// <param name="DescriptionKey">Localization key of the one-line description shown beside it.</param>
public sealed record QueryGrammarWord(string Name, string DescriptionKey);

/// <summary>
/// The vocabulary of the mesh query language, as <see cref="QueryParser"/> reads it — the single list
/// the completer offers from. Every entry here is something the parser acts on; a word the parser
/// ignores (an unknown <c>scope:</c> value silently becomes <c>exact</c>) must never be suggested.
/// <para>🚨 When the parser learns a qualifier or a value, add it here in the same change —
/// <c>QueryGrammarCompletionTest.EveryReservedQualifierIsOffered</c> walks the parser's reserved set.</para>
/// </summary>
public static class QueryGrammar
{
    /// <summary><c>nodeType:</c> — filter by NodeType path.</summary>
    public const string NodeType = "nodeType";
    /// <summary><c>namespace:</c> — anchor on a namespace.</summary>
    public const string Namespace = "namespace";
    /// <summary><c>path:</c> — anchor on a path.</summary>
    public const string Path = "path";
    /// <summary><c>basePath:</c> — everything under a path.</summary>
    public const string BasePath = QueryParser.BasePathQualifier;
    /// <summary><c>scope:</c> — how far from the anchor to look.</summary>
    public const string Scope = "scope";
    /// <summary><c>sort:</c> — ordering.</summary>
    public const string Sort = "sort";
    /// <summary><c>limit:</c> — page size.</summary>
    public const string Limit = "limit";
    /// <summary><c>select:</c> — projection.</summary>
    public const string Select = "select";
    /// <summary><c>partitions:</c> — declared fan-out.</summary>
    public const string Partitions = "partitions";
    /// <summary><c>source:</c> — change-feed source.</summary>
    public const string Source = "source";
    /// <summary><c>is:</c> — surface flags.</summary>
    public const string Is = "is";
    /// <summary><c>context:</c> — visibility context.</summary>
    public const string Context = "context";

    private const string K = "query.completion.";

    /// <summary>Reserved qualifiers, in the order they are offered.</summary>
    public static readonly IReadOnlyList<QueryGrammarWord> Qualifiers =
    [
        new(NodeType, K + "nodeType"),
        new(Namespace, K + "namespace"),
        new(Scope, K + "scope"),
        new(Sort, K + "sort"),
        new(Path, K + "path"),
        new(BasePath, K + "basePath"),
        new(Partitions, K + "partitions"),
        new(Limit, K + "limit"),
        new(Select, K + "select"),
        new(Is, K + "is"),
        new(Source, K + "source"),
        new(Context, K + "context"),
    ];

    /// <summary>
    /// <see cref="MeshNode"/> fields every node has and every executor filters and sorts on.
    /// </summary>
    public static readonly IReadOnlyList<QueryGrammarWord> NodeFields =
    [
        new("name", K + "field.name"),
        new("id", K + "field.id"),
        new("category", K + "field.category"),
        new("lastModified", K + "field.lastModified"),
        new("createdDate", K + "field.createdDate"),
        new("createdBy", K + "field.createdBy"),
        new("lastModifiedBy", K + "field.lastModifiedBy"),
    ];

    /// <summary>
    /// The <see cref="NodeFields"/> that hold a point in time — their values complete as an ordered
    /// comparison against a date (<c>lastModified:&gt;=2026-11-01</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> DateFields = ["lastModified", "createdDate"];

    /// <summary>
    /// The ordered comparisons a date value completes with, in the order they are offered: the operator
    /// as typed and the localization key of its meaning (<c>&gt;</c> = after).
    /// </summary>
    public static readonly IReadOnlyList<QueryGrammarWord> DateComparisons =
    [
        new(">", K + "date.after"),
        new("<", K + "date.before"),
        new(">=", K + "date.onOrAfter"),
        new("<=", K + "date.onOrBefore"),
    ];

    /// <summary>Localization key of the literal contains filter (<c>content.x:*typed*</c>).</summary>
    public const string ContainsKey = K + "contains";

    /// <summary>The <c>scope:</c> values the parser maps (canonical spellings only, no aliases).</summary>
    public static readonly IReadOnlyList<QueryGrammarWord> ScopeValues =
    [
        new("children", K + "scope.children"),
        new("descendants", K + "scope.descendants"),
        new("subtree", K + "scope.subtree"),
        new("ancestors", K + "scope.ancestors"),
        new("ancestorsAndSelf", K + "scope.ancestorsAndSelf"),
        new("hierarchy", K + "scope.hierarchy"),
        new("nextLevel", K + "scope.nextLevel"),
        new("exact", K + "scope.exact"),
    ];

    /// <summary>Sort selectors every node supports, besides <c>content.&lt;property&gt;</c>.</summary>
    public static readonly IReadOnlyList<QueryGrammarWord> SortFields =
    [
        new("name", K + "field.name"),
        new("lastModified", K + "field.lastModified"),
        new(SubtreeSort.SubtreeLastModified, K + "sort.subtreeLastModified"),
        new("path", K + "sort.path"),
        new("order", K + "sort.order"),
    ];

    /// <summary>The two sort directions, appended as <c>-asc</c> / <c>-desc</c>.</summary>
    public static readonly IReadOnlyList<QueryGrammarWord> SortDirections =
    [
        new("desc", K + "sort.desc"),
        new("asc", K + "sort.asc"),
    ];

    /// <summary>Useful <c>limit:</c> values; <c>all</c> is the enumeration spelling.</summary>
    public static readonly IReadOnlyList<QueryGrammarWord> LimitValues =
    [
        new("10", K + "limit.n"),
        new("20", K + "limit.n"),
        new("50", K + "limit.n"),
        new("100", K + "limit.n"),
        new("all", K + "limit.all"),
    ];

    /// <summary>The one <c>partitions:</c> value.</summary>
    public static readonly IReadOnlyList<QueryGrammarWord> PartitionValues = [new("all", K + "partitions")];

    /// <summary>The <c>is:</c> flags.</summary>
    public static readonly IReadOnlyList<QueryGrammarWord> IsValues =
    [
        new("main", K + "is.main"),
        new("content", K + "is.content"),
    ];

    /// <summary>The <c>source:</c> feeds.</summary>
    public static readonly IReadOnlyList<QueryGrammarWord> SourceValues =
    [
        new("activity", K + "source.activity"),
        new("accessed", K + "source.accessed"),
    ];

    /// <summary>Columns a <c>select:</c> projection can name (plus <c>content</c>, which loads the payload).</summary>
    public static readonly IReadOnlyList<QueryGrammarWord> SelectColumns =
    [
        new("name", K + "field.name"),
        new("path", K + "sort.path"),
        new("nodeType", K + "nodeType"),
        new("icon", K + "select.icon"),
        new("content", K + "select.content"),
        new("lastModified", K + "field.lastModified"),
    ];

    /// <summary>True when <paramref name="field"/> is a node field that holds a point in time (<see cref="DateFields"/>).</summary>
    /// <param name="field">The field as typed.</param>
    /// <returns>Whether its values are dates.</returns>
    public static bool IsDateField(string field)
        => DateFields.Contains(field, StringComparer.OrdinalIgnoreCase);

    /// <summary>Qualifiers whose value is a node path (completed from the mesh, not from a fixed list).</summary>
    public static bool TakesPath(string field)
        => field.Equals(Namespace, StringComparison.OrdinalIgnoreCase)
           || field.Equals(Path, StringComparison.OrdinalIgnoreCase)
           || field.Equals(BasePath, StringComparison.OrdinalIgnoreCase);
}
