using System.Reflection;

namespace MeshWeaver.Mesh;

/// <summary>
/// Represents a fully parsed query with reserved parameters.
/// </summary>
/// <param name="Filter">The parsed query AST (null if no filter conditions)</param>
/// <param name="TextSearch">Full-text search value (bare text in query)</param>
/// <param name="Path">Base path from path: qualifier (supports wildcards). Single-value form.</param>
/// <param name="Scope">Path scope from scope: qualifier</param>
/// <param name="OrderBy">Ordering clause from sort: qualifier</param>
/// <param name="Limit">Result limit from limit: qualifier</param>
/// <param name="Source">Data source from source: qualifier</param>
/// <param name="Select">Property names to project results onto (from select: qualifier)</param>
/// <param name="Context">Context for visibility filtering (from context: qualifier)</param>
/// <param name="IsMain">When true, filters to main nodes only (MainNode is null or equals Path)</param>
/// <param name="Paths">Multi-value path filter from <c>path:a|b|c</c> alternation. When set, backends should push down <c>WHERE path IN (...)</c>. <see cref="Path"/> is also set to the first value for back-compat with consumers that don't know about multi-path.</param>
/// <param name="IsContent">From <c>is:content</c> — the caller is listing CONTENT, so REGISTRATION nodes are not wanted: the in-memory type definitions, module definitions and partition declarations that <c>AddMeshNodes</c> contributes exist so the platform knows a type exists, and are what a create menu or an autocomplete offers, not something a person browses. A surface that lists things for a human says so on its own query rather than being enumerated in a central list of context names somewhere else.</param>
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
    bool? IsMain = null,
    IReadOnlyList<string>? Paths = null,
    bool? IsContent = null
)
{
    /// <summary>
    /// From <c>partitions:all</c> — the caller has EXPLICITLY asked to span every partition. Fan-out
    /// is opt-in: a storage provider refuses a query that names no partition and did not set this,
    /// because a silent cross-schema UNION locks every relation it touches and stalls unrelated
    /// pinned reads.
    ///
    /// <para>🚨 A FLAG, not a path value, on purpose. <c>path:*</c> reads like the same statement and
    /// is not: partition resolvers take a path's first segment as a schema NAME, so <c>path:*</c>
    /// pins the query to a partition literally called <c>*</c>, the statement dies on
    /// <c>relation "*.threads" does not exist</c>, and the caller gets an EMPTY result instead of an
    /// error.</para>
    ///
    /// <para>🚨 An init-only PROPERTY, not a positional parameter, also on purpose: a record's
    /// primary-constructor arity is binary contract, and adding a parameter — even one with a
    /// default — breaks every compiled caller. The <c>Public surface (binary compatibility)</c> gate
    /// catches it as <c>record ParsedQuery — arity</c>.</para>
    /// </summary>
    public bool CrossPartition { get; init; }

    /// <summary>
    /// The qualifier that sets <see cref="CrossPartition"/> — the EXPLICIT "span every partition"
    /// request, and the only thing that makes an otherwise unanchored query legal. Spelled once here
    /// so callers declare it by reference instead of by copied string.
    /// </summary>
    public const string CrossPartitionQualifier = "partitions:all";

    /// <summary>
    /// The ordering every clip site applies when the author wrote no <c>sort:</c> and the query
    /// carries no free-text term: newest first.
    /// </summary>
    /// <remarks>
    /// <para>🚨 <b>A capped result set is a partial answer, and which part survives the cap is
    /// decided HERE.</b> A filtered query — <c>nodeType:X</c>, <c>path:Y scope:descendants</c>,
    /// <c>partitions:all nodeType:Hosting/InstanceAction</c> — has no relevance signal to rank on,
    /// so before this default the clip was taken over whatever order each backend happened to
    /// enumerate: path-alphabetical on the in-memory walk, heap order on Postgres (no
    /// <c>ORDER BY</c> at all before <c>LIMIT</c>). A reader diagnosing an outage asks "what
    /// happened most recently", gets the first N rows of an arbitrary order, and reads the newest
    /// row in that page as the newest row there is. Measured (#4950): two truncated pages whose
    /// newest row was six days old, over a set whose actual newest rows explained the outage — the
    /// rows were simply not in the first 25. Ordered newest-first, a truncated page is still
    /// partial but it is the RIGHT part; unordered, it is a misleading answer that looks complete.
    /// This is the same rule as the coverage denominator (<c>partitions:all</c>, the
    /// <c>coverage.partitions</c> envelope): a zero or a cap is only readable against what the
    /// read actually spanned.</para>
    /// <para>The key is <c>lastModified</c> because it is the one field every backend resolves the
    /// same way — a typed column on Postgres (<c>n.last_modified</c>), a node field on the
    /// in-memory evaluator — and the one a diagnosing reader is asking about.</para>
    /// </remarks>
    public static readonly OrderByClause DefaultFilterOrdering = new("lastModified", Descending: true);

    /// <summary>
    /// The ordering a clip site APPLIES — resolved once, on the contract, so every backend clips
    /// over the same order. Read this, never <see cref="OrderBy"/>, at any point that takes a
    /// <c>Skip</c>/<c>Limit</c> window: the per-provider load cap
    /// (<c>StorageAdapterMeshQueryProvider</c>, the Postgres <c>ORDER BY … LIMIT</c>) and the
    /// merge (<c>MeshQuery.ClipMergedInitial</c>) each clip independently, and a default resolved
    /// at only one of them leaves the others clipping an arbitrary order first.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    ///   <item><see cref="OrderBy"/> when the author wrote <c>sort:</c> — intent always wins.</item>
    ///   <item><see langword="null"/> when the query carries a free-text term
    ///     (<see cref="TextSearch"/>): relevance IS the ordering there, and the providers' scores
    ///     rank it (see <c>QueryResultChange.Scores</c>).</item>
    ///   <item><see langword="null"/> for a non-default <see cref="Source"/>: <c>source:activity</c>
    ///     and <c>source:accessed</c> are change feeds whose provider ranks by the joined
    ///     satellite's recency, which is the right "newest" for that feed.</item>
    ///   <item>Otherwise <see cref="DefaultFilterOrdering"/> — newest first.</item>
    /// </list>
    /// <para><see cref="OrderBy"/> keeps meaning "what the author asked for": a surface that
    /// reports the applied ordering back to a reader prints this property and can say whether it
    /// was authored or defaulted by comparing the two.</para>
    /// </remarks>
    public OrderByClause? EffectiveOrderBy =>
        OrderBy
        ?? (string.IsNullOrEmpty(TextSearch) && Source == QuerySource.Default
            ? DefaultFilterOrdering
            : null);

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
    /// Extracts every value from <c>namespace:X</c> conditions in the filter.
    /// Used by the top-level aggregator to dispatch a query to the set of
    /// providers whose <c>Matches</c> predicate accepts any of these
    /// namespaces. Empty when the query has no namespace constraint
    /// (broadcast to all providers).
    /// </summary>
    public IReadOnlyList<string> ExtractNamespaces()
    {
        if (Filter == null) return Array.Empty<string>();
        var collected = new List<string>();
        ExtractNamespacesFromNode(Filter, collected);
        return collected;
    }

    private static void ExtractNamespacesFromNode(QueryNode node, List<string> collected)
    {
        switch (node)
        {
            // Equal → single namespace; In → a `namespace:A|B|C` alternation (membership filter,
            // produced by the parser for the agent/extensible-defaults union). Both forms carry the
            // namespace candidates the aggregator routes on, so collect Values for either operator.
            case QueryComparison c when c.Condition.Selector.Equals("namespace", StringComparison.OrdinalIgnoreCase)
                && c.Condition.Operator is QueryOperator.Equal or QueryOperator.In:
                collected.AddRange(c.Condition.Values);
                break;
            case QueryAnd and:
                foreach (var child in and.Children) ExtractNamespacesFromNode(child, collected);
                break;
            case QueryOr or:
                foreach (var child in or.Children) ExtractNamespacesFromNode(child, collected);
                break;
        }
    }

    /// <summary>
    /// Namespace filter values INCLUDING wildcard (<c>Like</c>) patterns — e.g. <c>{owner}/*_Thread</c>.
    /// <see cref="ExtractNamespaces"/> deliberately collects only concrete <c>Equal</c>/<c>In</c>
    /// namespaces (the aggregator ROUTES on those, and can't route a glob); this variant ALSO returns the
    /// <c>Like</c> glob patterns, for the live change-relevance filter that decides whether a CRUD event
    /// under a namespace should refresh a synced query (see <c>PathMatcher.ShouldNotifyForQuery</c>). The
    /// glob is preserved verbatim (the parser keeps the <c>*</c>), so a matcher can apply it directly.
    /// </summary>
    public IReadOnlyList<string> ExtractNamespacePatterns()
    {
        if (Filter == null) return Array.Empty<string>();
        var collected = new List<string>();
        ExtractNamespacePatternsFromNode(Filter, collected);
        return collected;
    }

    private static void ExtractNamespacePatternsFromNode(QueryNode node, List<string> collected)
    {
        switch (node)
        {
            case QueryComparison c when c.Condition.Selector.Equals("namespace", StringComparison.OrdinalIgnoreCase)
                && c.Condition.Operator is QueryOperator.Equal or QueryOperator.In or QueryOperator.Like:
                collected.AddRange(c.Condition.Values);
                break;
            case QueryAnd and:
                foreach (var child in and.Children) ExtractNamespacePatternsFromNode(child, collected);
                break;
            case QueryOr or:
                foreach (var child in or.Children) ExtractNamespacePatternsFromNode(child, collected);
                break;
        }
    }

    /// <summary>
    /// Whether this query says WHERE to look — it names a partition, or it explicitly asks to span
    /// them. The negation is an UNANCHORED query: one that a partitioned backend can answer only by
    /// a UNION over every partition it knows, and that it therefore refuses (the CI invariant) or
    /// serves from the partitions it happens to enumerate (a production host under
    /// <c>ServeAndReport</c>) — a partial answer with nothing on it to say so (MeshWeaver #4274).
    ///
    /// <para>Four ways to be specified, and a query needs exactly one:</para>
    /// <list type="bullet">
    /// <item><b>A concrete anchor</b> — <c>path:X/…</c>, or <c>namespace:X/…</c>, which the parser
    /// folds into the same <see cref="Path"/>. Pins to one partition.</item>
    /// <item><b>A multi-path anchor</b> — <c>path:A|B|C</c>, i.e. <see cref="Paths"/>. Pins to the
    /// set.</item>
    /// <item><b>The explicit request</b> — <c>partitions:all</c>, i.e. <see cref="CrossPartition"/>.
    /// 🚨 It is a FLAG, and <c>path:*</c> is NOT a synonym: partition resolution reads a path's first
    /// segment as a partition NAME, so <c>path:*</c> pins the query to a partition literally called
    /// <c>*</c> and the caller gets an EMPTY result rather than an error.</item>
    /// <item><b>A namespace filter</b> — the <c>namespace:A|B|C</c> membership form and the explicit
    /// wildcard <c>namespace:*/_Thread</c>. The parser keeps both as filters rather than a Path (see
    /// <c>QueryParser</c>), so Path is null and only the filter carries them. Missing this case would
    /// refuse the satellite browses that are the legitimate spanning reads. 🚨 The filter anchors
    /// the query only if EVERY <c>OR</c> branch carries one: <c>namespace:*/_Thread OR nodeType:Foo</c>
    /// is anchored on its left branch alone, and the right branch is exactly the unanchored read
    /// this predicate exists to name — counting the pattern anywhere in the tree would let it
    /// through, and a search would then state the pattern as its coverage while the store ran the
    /// full fan-out for the other branch.</item>
    /// </list>
    ///
    /// <para>🚨 This is THE definition, and there is one: the Postgres planner
    /// (<c>PostgreSqlPartitionedMeshQuery.IsSufficientlySpecified</c>, MeshWeaver.Plugins) decides
    /// whether to refuse on it, and <c>MeshOperations.Search</c> — the MCP <c>search</c> tool and the
    /// agents' <c>Search</c> — refuses on it BEFORE the query reaches any backend, so the two cannot
    /// drift the way the two executors of the query language once did (#3511). A routing rule
    /// (<c>nodeType:User</c> → the auth mirror) can still name a partition for a query this method
    /// calls unspecified; that resolution lives on <c>MeshConfiguration.ResolveRoutingHints</c>, and
    /// callers that honour it check it after this.</para>
    /// </summary>
    /// <returns>True when the query names where to look; false when it is unanchored.</returns>
    public bool IsSufficientlySpecified() =>
        CrossPartition
        || NamesConcretePartition(Path)
        || Paths is { Count: > 0 }
        || AnchoredByNamespaceFilter(Filter);

    /// <summary>
    /// Whether <paramref name="node"/> anchors the query through a namespace filter on EVERY path
    /// through it: an <c>OR</c> is anchored only when each branch is, an <c>AND</c> when any
    /// conjunct is, and a <c>namespace:</c> comparison (<c>Equal</c>, the <c>In</c> membership form,
    /// the <c>Like</c> wildcard) is the anchor itself. Anything else — a bare filter, a negated
    /// namespace — is not.
    /// </summary>
    private static bool AnchoredByNamespaceFilter(QueryNode? node) => node switch
    {
        QueryComparison c => c.Condition.Selector.Equals("namespace", StringComparison.OrdinalIgnoreCase)
                             && c.Condition.Operator is QueryOperator.Equal or QueryOperator.In or QueryOperator.Like,
        QueryAnd and => and.Children.Any(AnchoredByNamespaceFilter),
        QueryOr or => or.Children.Count > 0 && or.Children.All(AnchoredByNamespaceFilter),
        _ => false,
    };

    /// <summary>
    /// Whether <paramref name="path"/> NAMES a partition — a non-empty path whose first segment is
    /// not the wildcard. <c>path:*</c> looks like an anchor to a null-check and is not one (see
    /// <see cref="IsSufficientlySpecified"/>); counting it as specified would let exactly the
    /// silent-empty shape through the refusal built to stop it.
    /// </summary>
    private static bool NamesConcretePartition(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var trimmed = path.Trim('/');
        if (trimmed.Length == 0) return false;
        var slash = trimmed.IndexOf('/');
        var first = slash < 0 ? trimmed : trimmed[..slash];
        return first.Length > 0 && first != "*";
    }

    /// <summary>
    /// The partition(s) this query names, as written — the first path segment of the anchor, every
    /// distinct first segment of a multi-path anchor, or the namespace filter values verbatim
    /// (a <c>namespace:*/_Thread</c> pattern is reported as the pattern, because it does not name a
    /// partition). Empty for an unanchored query and for <c>partitions:all</c>, whose coverage only
    /// the backend that ran it can state. What <c>MeshOperations.Search</c> puts in its envelope's
    /// <c>coverage</c> when no provider reported the partitions it actually read.
    /// </summary>
    /// <returns>The named partitions or namespace patterns, in query order, distinct.</returns>
    public IReadOnlyList<string> NamedPartitions()
    {
        var named = new List<string>();
        if (Paths is { Count: > 0 })
        {
            foreach (var p in Paths)
                if (FirstSegmentOrNull(p) is { } seg) named.Add(seg);
        }
        else if (FirstSegmentOrNull(Path) is { } seg)
        {
            named.Add(seg);
        }
        foreach (var pattern in ExtractNamespacePatterns())
            named.Add(pattern);
        return named.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? FirstSegmentOrNull(string? path)
    {
        if (!NamesConcretePartition(path)) return null;
        var trimmed = path!.Trim('/');
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? trimmed : trimmed[..slash];
    }

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
    /// A change feed: MAIN content nodes INNER-JOINed with their <c>_Activity</c> satellites,
    /// ranked by the joined activity's recency. A node with no recorded activity does not appear.
    /// It does <b>not</b> imply a <c>nodeType:Activity</c> filter — the rows returned are the
    /// main nodes, and every other filter still applies.
    /// </summary>
    Activity,

    /// <summary>
    /// Nodes the current user has accessed, ordered by UserActivity last-access time.
    /// </summary>
    Accessed
}
