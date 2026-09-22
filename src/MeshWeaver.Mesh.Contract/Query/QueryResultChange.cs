namespace MeshWeaver.Mesh;

/// <summary>
/// Represents a change in query results for observable queries.
///
/// <para><b>Per-item ranking via <see cref="Scores"/>.</b> Providers MAY
/// attach a parallel array of numeric scores — one entry per item in
/// <see cref="Items"/> — so the <c>IMeshQueryProvider</c> fan-in can
/// merge hits from multiple backends into one ordered set. Higher score =
/// stronger match. The aggregator sorts the merged result by
/// <see cref="ParsedQuery.EffectiveOrderBy"/> first (an authored <c>sort:</c>, or newest-first
/// for a filter-only query) and by score descending when a free-text term makes relevance the
/// ordering, so callers always get the most relevant hit at index 0
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
    /// <see cref="ParsedQuery.EffectiveOrderBy"/> clause; for a free-text query no such clause
    /// resolves and the score is the sole sort key (desc), so a provider can drive
    /// "best match first" ordering without the query author having to add
    /// <c>sort:</c>. <see langword="null"/> means the provider did not
    /// score this batch — every item competes as 0 and the path tiebreak decides.
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
    /// Set by a PROVIDER on its own frame when it answered over reads it could not complete — it
    /// caught a store fault, dropped the rows it could not get, and is emitting what survived.
    ///
    /// <para>🚨 <b>This is the other half of <see cref="SilentProviders"/>, and it was the missing
    /// half.</b> That field names a provider that produced NO Initial; a provider that produces a
    /// SHORTER one is the same lie with a frame attached, and until this existed there was nothing
    /// on the wire to tell them apart. A provider that swallows a read fault into an empty result
    /// and still emits an Initial is not recorded as silent, so a downstream consumer that guards on
    /// <see cref="SilentProviders"/> — <c>PathResolutionService</c>'s floor refusal, the plugin
    /// gate's grant read — cannot fire and reads the gap as absence.</para>
    ///
    /// <para><b>Why it is a provider-side flag and not a provider name.</b> The aggregator already
    /// knows which stream it subscribed (<c>MeshQuery.MergeProviderObservables</c> carries the
    /// provider's name beside its observable), so a provider naming itself would put the same string
    /// in two places and let them drift. The provider states the FACT; the aggregator attaches the
    /// NAME and folds it into <see cref="SilentProviders"/>, which is what every existing consumer
    /// already reads.</para>
    ///
    /// <para><see langword="false"/> — the default — means every read behind this frame either
    /// succeeded or genuinely found nothing. It says nothing about OTHER providers: that is the
    /// merged frame's <see cref="SilentProviders"/>.</para>
    /// </summary>
    public bool SnapshotIncomplete { get; init; }

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
