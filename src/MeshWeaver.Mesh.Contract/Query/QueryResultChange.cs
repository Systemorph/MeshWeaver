namespace MeshWeaver.Mesh;

/// <summary>
/// Represents a change in query results for observable queries.
///
/// <para><b>Per-item ranking via <see cref="Scores"/>.</b> Providers MAY
/// attach a parallel array of numeric scores — one entry per item in
/// <see cref="Items"/> — so the <c>IMeshQueryProvider</c> fan-in can
/// merge hits from multiple backends into one ordered set. Higher score =
/// stronger match. The aggregator sorts the merged result by
/// <see cref="ParsedQuery.OrderBy"/> first (when present) and then by score
/// descending, so callers always get the most relevant hit at index 0
/// without re-ranking on their side.</para>
///
/// <para><b>What each provider's score means.</b> The contract is that the
/// scale is comparable ACROSS providers for a single query, not absolute:</para>
/// <list type="bullet">
///   <item><c>StaticNodeQueryProvider</c> — fzf-style fuzzy score from
///     <c>FuzzyScorer</c> when the query carries text search
///     (<see cref="ParsedQuery.TextSearch"/>); 0 for pure filter /
///     namespace queries.</item>
///   <item>PostgreSQL providers (<c>PostgreSqlMeshQuery</c>,
///     <c>PostgreSqlPartitionedMeshQuery</c>) — composite of:
///     name-prefix bonus (100), name-substring bonus (50), path-substring
///     bonus (30), <c>PathProximity</c> boost (max 40, decays with namespace
///     distance from the requesting hub), and the vector-search distance
///     (<c>&lt;=&gt;</c> cosine operator) when an embedding column is set.</item>
///   <item><c>StorageAdapterMeshQueryProvider</c> — scores match the PG
///     mesh query for parity; defaults to 0 on backends without a native
///     scoring layer.</item>
/// </list>
///
/// <para>Leave <see cref="Scores"/> null when scoring is genuinely
/// uninformative (e.g. a one-shot exact path probe); the aggregator then
/// falls back to insertion order — which preserves the provider's own
/// ordering for single-provider queries.</para>
/// </summary>
/// <typeparam name="T">The type of items in the result set.</typeparam>
public record QueryResultChange<T>
{
    /// <summary>
    /// The type of change that occurred.
    /// </summary>
    public QueryChangeType ChangeType { get; init; }

    /// <summary>
    /// The items affected by this change.
    /// For Initial/Reset: all matching items.
    /// For Added/Updated/Removed: only the changed items.
    /// </summary>
    public IReadOnlyList<T> Items { get; init; } = [];

    /// <summary>
    /// Optional parallel array of relevance scores, one per item in
    /// <see cref="Items"/>. When non-null its length MUST equal
    /// <c>Items.Count</c>. Higher = stronger match. The aggregator
    /// (<c>MeshQuery.ClipMergedInitial</c>) consults this AFTER any
    /// <see cref="ParsedQuery.OrderBy"/> clauses; with no OrderBy specified
    /// the score is the sole sort key (desc), so a provider can drive
    /// "best match first" ordering without the query author having to add
    /// <c>sort:</c>. <see langword="null"/> means the provider did not
    /// score this batch — aggregator preserves insertion order.
    /// </summary>
    public IReadOnlyList<double>? Scores { get; init; }

    /// <summary>
    /// The original query that produced this change.
    /// </summary>
    public ParsedQuery Query { get; init; } = null!;

    /// <summary>
    /// Monotonically increasing version number for ordering changes.
    /// </summary>
    public long Version { get; init; }

    /// <summary>
    /// Timestamp when this change was detected.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Types of changes that can occur in a query result set.
/// </summary>
public enum QueryChangeType
{
    /// <summary>Initial result set when subscription starts.</summary>
    Initial,

    /// <summary>New items were added that match the query.</summary>
    Added,

    /// <summary>Existing items were updated (still match the query).</summary>
    Updated,

    /// <summary>Items were removed or no longer match the query.</summary>
    Removed,

    /// <summary>Full reset - treat as new initial result set (e.g., after reconnection).</summary>
    Reset
}
