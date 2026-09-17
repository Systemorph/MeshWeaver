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
    /// The partitions this batch was READ FROM — the denominator a zero is read against.
    ///
    /// <para>A partitioned backend answers a query from a SUBSET of the mesh: the one partition an
    /// anchor names, or — for a declared fan-out — the partitions it enumerates MINUS the system
    /// schemas it never enumerates (<c>admin</c>, <c>auth</c>, …) MINUS every partition the caller
    /// holds no read grant on (row-level security drops those branches before the SQL is written).
    /// An empty <see cref="Items"/> from such a read says "none in these", never "none exist"
    /// (MeshWeaver #4274, and the three ways a pre-deploy sweep's zero has been wrong). A provider
    /// that knows the set it read states it here, so an envelope built on this change can carry it.
    /// </para>
    ///
    /// <para><see langword="null"/> means the provider did NOT report — a static catalog, a
    /// pedestrian path walk, an older image — and MUST be surfaced as unknown, never as "all" or
    /// "none". The aggregator (<c>MeshQuery.MergeProviderObservables</c>) unions the lists of the
    /// providers that reported and stays null when none did. Meaningful on the Initial; live
    /// deltas leave it null.</para>
    /// </summary>
    public IReadOnlyList<string>? Partitions { get; init; }

    /// <summary>
    /// The providers that COMPLETED WITHOUT emitting an <see cref="QueryChangeType.Initial"/> and
    /// were counted as EMPTY so the merged query could proceed — never <see langword="null"/>-safe
    /// to ignore, and never the same statement as an empty <see cref="Items"/>.
    ///
    /// <para>🚨 <b>A snapshot naming any provider here is a FLOOR, not an answer.</b> "Nobody
    /// answered yet" and "there is nothing there" are different facts, and until this field existed
    /// they arrived in the same shape: <c>MeshQuery.MergeProviderObservables</c> counts a silent
    /// completion as an empty Initial <i>by contract</i> — the alternative starved the gate and hung
    /// every real-user search for 300 s — and the frame it produced then said "nothing matches" with
    /// nothing behind it. Downstream that is worse than a hang, because
    /// <c>MeshNodeStreamCache</c> caches the first frame in a <c>Replay(1)</c> chain it never
    /// rebuilds: the fabricated empty became the durable negative for the life of the PROCESS, and
    /// the one event that would refresh it — a change notification for a matching path — never fires
    /// for the most common writer, a reconcile re-writing an unchanged node (a NO-OP at the store,
    /// which publishes nothing). Measured on memex-cloud 2026-09-16: the plugin gate read durably
    /// present <c>_Access</c> grants and <c>_Policy</c> nodes as missing for 24 minutes after a
    /// restart, and reported them at Error as lost writes (MeshWeaver#4557, #1246).</para>
    ///
    /// <para><see langword="null"/> or empty means every provider answered — the snapshot is a real
    /// answer and may be cached and replayed like one. Sibling of <see cref="Partitions"/>: both
    /// exist so an empty result can be read against what actually produced it.</para>
    /// </summary>
    public IReadOnlyList<string>? SilentProviders { get; init; }

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
