using System.Reactive;
using MeshWeaver.Hosting.Embeddings;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// PostgreSQL implementation of IStorageAdapter.
/// Stores MeshNodes in mesh_nodes table and partition objects in partition_objects table.
/// When a PartitionDefinition with TableMappings is provided, satellite nodes are routed
/// to their dedicated tables based on path pattern matching.
/// </summary>
public class PostgreSqlStorageAdapter : IScopedQueryStorageAdapter, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly PartitionDefinition? _partitionDefinition;
    private readonly string? _schemaName;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;
    // Per-adapter READ pool (the pg-read:{adapter} IIoPool). Bounds concurrent READS below the
    // shared connection-pool size so a synced-query read fan-out storm can't drain the pool and
    // starve writes (writes stay ungated and always have headroom). This IS the former hand-woven
    // ReadConcurrencyGate — its SemaphoreSlim folded into the one sanctioned IIoPool primitive, so
    // there is no standalone semaphore anywhere. Unbounded fallback when no registry is wired
    // (in-memory / tests): reads still offload off the hub scheduler, just without the cap.
    private readonly IIoPool _readPool;
    // The pg:{adapter} write I/O pool — every WRITE DB round-trip runs inside it (Invoke), never a
    // bare Observable.FromAsync. Unbounded fallback when no registry is wired.
    //
    // 🚨 WRITES ONLY, and that boundary is load-bearing (issues #1310/#1312/#1313/#1316). Reads —
    // Read, ReadMany, Exists, FindBestPrefixMatch, ResolvePath, GetPartitionObjects,
    // GetPartitionMaxTimestamp, ListPartitionSubPaths — used to run here too, which was wrong twice
    // over. This pool is capped at ONE (IoPoolNames.PostgresAdapterPrefix: "the gate IS the
    // connection"), so filing a read here means either every single-node read in the silo
    // serialises behind one connection (once the pool is wired) or — as was actually the case
    // while nobody passed `ioPool:` — the hottest read path in the portal runs completely
    // UNBOUNDED against a shared 50-connection NpgsqlDataSource. It was the latter: per-node-hub
    // activation seeds (MeshNodeTypeSource.DurableSeed), URL resolution (ResolvePath), write-guard
    // probes and the per-path Read fan-out inside StorageAdapterMeshQueryProvider all bypassed the
    // pg-read: cap that exists precisely to bound them, and memex-cloud duly hit "the connection
    // pool has been exhausted (currently 50)".
    //
    // Keeping reads OFF this pool is also what makes the cap-1 write gate safe: a read issued from
    // inside a write (PartitionWriteGuardValidator's probe, Write's refused-upsert re-read) would
    // otherwise be a same-pool re-entry on a cap-1 gate — the one documented way to deadlock an
    // IIoPool (ControlledIoPooling.md → "Never let an IIoPool call resolve an observable that
    // itself acquires the same pool").
    private readonly IIoPool _ioPool;
    // NOT a Subject<T>. Subject fan-out is synchronous and ordered, so the first observer that
    // throws aborts delivery to every observer after it — and the publish sites used to wrap that
    // in `catch { /* best-effort */ }`, turning a starved subscriber into silence. See
    // IsolatedChangeFeed for the failure this caused (a permanently stale security fold).
    private readonly IsolatedChangeFeed _changes;

    // Per-adapter cache of "does {schema}.content_chunks exist?" — drives whether the vector search
    // UNIONs the indexed-content branch (DocumentPaths-resolved Document rows). INSTANCE field (never
    // static — the no-static-state rule): its lifetime is this adapter's. Only TRUE is cached
    // (permanently — a content index is never dropped under us); a FALSE/missing schema is NOT cached
    // so a partition that LATER gains content is picked up on the next search. The probe itself is a
    // sub-millisecond to_regclass() catalog lookup run inside the pooled READ leaf.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _contentChunksExists = new(StringComparer.Ordinal);

    /// <summary>The underlying Npgsql data source (connection pool) this adapter reads and writes through.</summary>
    public NpgsqlDataSource DataSource => _dataSource;

    /// <summary>
    /// The Postgres schema this adapter is scoped to (from its <see cref="PartitionDefinition"/>),
    /// or null for the unscoped/public single-schema adapter. Lets
    /// <see cref="PostgreSqlPartitionedVersionQuery"/> read the partition's schema-qualified
    /// <c>mesh_node_history</c> through the same schema the router reads <c>mesh_nodes</c> from.
    /// </summary>
    internal string? SchemaName => _schemaName;

    /// <inheritdoc />
    /// <remarks>
    /// Surfaces the PG <c>LISTEN/NOTIFY</c> change feed — a
    /// <see cref="PostgreSqlChangeListener"/> background service publishes here
    /// for every row committed to <c>mesh_nodes</c> (and satellite tables),
    /// so synced-query subscribers see writes from any process in the cluster.
    /// </remarks>
    public IObservable<DataChangeNotification> Changes => _changes;

    /// <summary>
    /// Internal hook for <see cref="PostgreSqlChangeListener"/> to push
    /// LISTEN/NOTIFY events into the adapter's <see cref="Changes"/> feed.
    /// </summary>
    internal IObserver<DataChangeNotification> ChangeObserver => _changes;

    /// <summary>
    /// Initializes the storage adapter over a data source, optionally scoped to a single partition schema.
    /// </summary>
    /// <param name="dataSource">The Npgsql data source used for all reads and writes.</param>
    /// <param name="embeddingProvider">Optional embedding provider used to populate the vector column on write; defaults to a no-op provider.</param>
    /// <param name="partitionDefinition">Optional partition definition; when set, table references are scoped to its schema.</param>
    /// <param name="logger">Optional logger for read/write diagnostics.</param>
    /// <param name="readPool">Optional per-adapter read pool bounding concurrent reads below the connection-pool size.</param>
    /// <param name="ioPool">Optional per-adapter write pool (capped at one connection) serializing writes.</param>
    public PostgreSqlStorageAdapter(
        NpgsqlDataSource dataSource,
        IEmbeddingProvider? embeddingProvider = null,
        PartitionDefinition? partitionDefinition = null,
        Microsoft.Extensions.Logging.ILogger? logger = null,
        IIoPool? readPool = null,
        IIoPool? ioPool = null)
    {
        _dataSource = dataSource;
        _embeddingProvider = embeddingProvider ?? NullEmbeddingProvider.Instance;
        _partitionDefinition = partitionDefinition;
        _schemaName = partitionDefinition?.Schema;
        _logger = logger;
        _changes = new IsolatedChangeFeed(logger, partitionDefinition?.Schema ?? "public");
        _readPool = readPool ?? IoPool.Unbounded;
        _ioPool = ioPool ?? IoPool.Unbounded;
    }

    /// <summary>
    /// Test seam (<c>InternalsVisibleTo</c>): the per-adapter WRITE pool actually in force
    /// (<c>pg:{adapter}</c>, cap 1) — or <see cref="IoPool.Unbounded"/> when no pool was wired.
    /// <para>Exposed because "the bound is not wired" is invisible from behaviour until the shared
    /// connection pool is already exhausted in production: an unwired adapter works perfectly under
    /// test load and only fails at 50 concurrent connections. See
    /// <c>PartitionAdapterIoPoolWiringTests</c>.</para>
    /// </summary>
    internal IIoPool WritePool => _ioPool;

    /// <summary>
    /// Test seam (<c>InternalsVisibleTo</c>): the per-adapter READ pool actually in force
    /// (<c>pg-read:{adapter}</c>) — or <see cref="IoPool.Unbounded"/> when no pool was wired.
    /// </summary>
    internal IIoPool ReadPool => _readPool;

    /// <summary>
    /// Pumps a read <see cref="IAsyncEnumerable{T}"/> through the per-adapter READ pool
    /// (<c>pg-read:{adapter}</c>), bounding concurrent reads below the connection-pool size so a
    /// fan-out storm can't starve writes. The pool's <see cref="IIoPool.InvokeStream{T}"/> holds
    /// ONE slot for the whole enumeration (acquired off the caller's scheduler, released when the
    /// enumeration completes / errors / is cancelled) — exactly the former <c>ReadConcurrencyGate</c>
    /// slot semantics, now backed by the one sanctioned <see cref="IIoPool"/> semaphore. The
    /// observable is bridged back to <see cref="IAsyncEnumerable{T}"/> via an unbounded
    /// <see cref="System.Threading.Channels.Channel{T}"/> so callers' existing <c>await foreach</c>
    /// shape is unchanged. The reader's rows arrive on a ThreadPool worker; this method only relays.
    /// </summary>
    private async IAsyncEnumerable<T> ReadPooled<T>(
        Func<CancellationToken, IAsyncEnumerable<T>> source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<T>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        var subscription = _readPool.InvokeStream(source).Subscribe(
            item => channel.Writer.TryWrite(item),
            ex => channel.Writer.TryComplete(ex),
            () => channel.Writer.TryComplete());

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            // Unsubscribe releases the held read-pool slot (the InvokeStream enumeration is
            // cancelled) even when the caller breaks out of the await foreach early.
            subscription.Dispose();
        }
    }

    /// <summary>Empty async sequence — for the no-op query branch (no slot taken).</summary>
    private static IAsyncEnumerable<T> EmptyAsync<T>()
        => System.Linq.AsyncEnumerable.Empty<T>();

    /// <summary>
    /// Returns a schema-qualified table reference for use in SQL.
    /// When a schema is set, returns "schema"."table"; otherwise just "table".
    /// </summary>
    private string QualifyTable(string table)
        => string.IsNullOrEmpty(_schemaName) ? $"\"{table}\"" : $"\"{_schemaName}\".\"{table}\"";

    /// <summary>
    /// Resolves a schema-qualified table name for a given path and optional nodeType.
    /// Checks path-based satellite routing first, then falls back to nodeType-based routing.
    /// </summary>
    private string ResolveTable(string path, string? nodeType = null)
    {
        string table;
        if (_partitionDefinition == null)
            table = "mesh_nodes";
        else
        {
            table = _partitionDefinition.ResolveTable(path);
            if (table == "mesh_nodes" && !string.IsNullOrEmpty(nodeType))
                table = _partitionDefinition.ResolveTableByNodeType(nodeType);
        }
        return QualifyTable(table);
    }

    // Projection for the node-level sync claim: the real column when reading mesh_nodes (the
    // only decouplable table), else the Include (0) default so single-table reads and UNION
    // branches over satellite tables — which don't carry the column — keep a stable shape.
    private static string SyncBehaviorCol(string qualifiedTable) =>
        qualifiedTable.Contains("mesh_nodes", StringComparison.OrdinalIgnoreCase)
            ? "sync_behavior"
            : "0 AS sync_behavior";

    // Authorship columns exist only on mesh_nodes; satellite selects emit typed NULLs so
    // ReadMeshNode finds the columns by name either way (like SyncBehaviorCol).
    private static string AuthorCols(string qualifiedTable) =>
        qualifiedTable.Contains("mesh_nodes", StringComparison.OrdinalIgnoreCase)
            ? "created_by, last_modified_by, created_date"
            : "NULL::text AS created_by, NULL::text AS last_modified_by, NULL::timestamptz AS created_date";

    // ExcludeFromContext lives only on mesh_nodes (like authorship): the instance-level
    // context opt-outs ("header", "search", "create") are a main-node concern; satellite
    // selects emit a typed NULL so ReadMeshNode finds the column by name either way.
    private static string ExcludeCol(string qualifiedTable) =>
        qualifiedTable.Contains("mesh_nodes", StringComparison.OrdinalIgnoreCase)
            ? "exclude_from_context"
            : "NULL::text[] AS exclude_from_context";

    private static string NormalizePath(string? path) =>
        path?.Trim('/') ?? "";

    private static (string Namespace, string Id) SplitPath(string normalizedPath)
    {
        var lastSlash = normalizedPath.LastIndexOf('/');
        var ns = lastSlash > 0 ? normalizedPath[..lastSlash] : "";
        var id = lastSlash > 0 ? normalizedPath[(lastSlash + 1)..] : normalizedPath;
        return (ns, id);
    }

    // null Select → caller didn't project → fetch all columns (existing behavior).
    // non-null Select → caller opted into projection → fetch column only if listed.
    private static bool SelectorAsksFor(IReadOnlyList<string>? select, string column)
        => select is null || select.Any(s => s.Equals(column, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the exception is the Postgres "relation / schema does not exist"
    /// error (<c>42P01</c>, undefined_table). Since the partition router resolves a
    /// path's first segment to a schema <i>synchronously</i> (no existence probe),
    /// a READ can legitimately target a schema that was never created — there's
    /// simply nothing to read. Every read method swallows this and returns the
    /// empty result (null / empty / false) instead of faulting. A WRITE to an
    /// unprovisioned partition, by contrast, lets <c>42P01</c> propagate — that fault
    /// IS the "no partition, no write" refusal (the router no longer lazily creates a
    /// schema; provisioning is eager, gated to partition-owning creates).
    /// </summary>
    private static bool IsUndefinedTable(Exception ex)
        => ex is PostgresException pg && pg.SqlState == "42P01";

    // Transient Postgres connectivity faults (connection dropped / endpoint momentarily
    // unreachable) get a SHORT bounded retry at the read leaf so a sub-second-to-second blip
    // (the common case during a host reboot or failover) never surfaces as a hard read fault.
    // A LONGER outage exhausts these retries and the fault propagates — the upstream
    // MeshNodeStreamCache classifies it as transient (IsTransientDatabaseFailure) and its ≤60s
    // breaker self-heals the activation instead of wedging it until a manual recycle (the memex
    // AgenticEngineering incident, 2026-07-23). Deliberately short (≤3 retries, 200→800ms) so it
    // never approaches the 60s SubscribeRequest budget. READS only — writes are never
    // auto-retried (no idempotency key ⇒ replay risk).
    private const int MaxTransientReadRetries = 3;

    private static readonly HashSet<string> TransientPostgresSqlStates = new(StringComparer.Ordinal)
    {
        "57P01", "57P02", "57P03",                     // admin/crash shutdown, cannot_connect_now (startup)
        "53300", "53400",                              // too_many_connections, configuration_limit_exceeded
        "08000", "08001", "08003", "08004", "08006",   // connection_exception family
        "40001", "40P01",                              // serialization_failure, deadlock_detected (retryable)
    };

    /// <summary>
    /// True when <paramref name="ex"/> (or an inner exception) is a TRANSIENT Postgres
    /// connectivity fault worth a bounded retry — a dropped/unreachable connection, a server
    /// restart/failover, connection/pool exhaustion, or a serialization/deadlock race — as
    /// opposed to a real query/schema error. A <see cref="PostgresException"/> with a
    /// non-transient <c>SqlState</c> (e.g. <c>42P01</c> undefined_table, <c>23505</c> unique
    /// violation, a syntax error) is NOT transient and must propagate. A bare
    /// <see cref="NpgsqlException"/> that is not a server <see cref="PostgresException"/> is a
    /// client/connection failure ("Failed to connect", "Timeout during connection attempt") and
    /// IS transient.
    /// </summary>
    internal static bool IsTransientConnectionFault(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            switch (e)
            {
                case TimeoutException:
                case System.Net.Sockets.SocketException:
                    return true;
                case PostgresException pg when TransientPostgresSqlStates.Contains(pg.SqlState):
                    return true;
                case PostgresException:
                    break; // a real server error (42P01, 23505, syntax, …) — not transient
                case NpgsqlException:
                    return true; // connection-layer Npgsql failure (not a server error)
            }
        }
        return false;
    }

    /// <summary>Bounded exponential backoff for the transient-read retry: 200ms, 400ms, 800ms (capped).</summary>
    internal static TimeSpan TransientReadBackoff(int attempt)
        => TimeSpan.FromMilliseconds(Math.Min(200 * Math.Pow(2, attempt), 800));

    /// <summary>
    /// Wraps a cold read observable with a bounded exponential-backoff retry that fires ONLY on
    /// <see cref="IsTransientConnectionFault"/>. Any non-transient error (and any transient fault
    /// past <see cref="MaxTransientReadRetries"/>) propagates unchanged so the caller's existing
    /// <c>.Catch(IsUndefinedTable …)</c> and the upstream breaker still see the real exception.
    /// </summary>
    private IObservable<T> WithTransientRetry<T>(Func<IObservable<T>> read, string op)
        => RetryTransientReads(read, IsTransientConnectionFault, MaxTransientReadRetries,
            TransientReadBackoff,
            (ex, attempt, delay) => _logger?.LogWarning(ex,
                "PostgreSqlStorageAdapter: transient DB fault on {Op}, attempt {Attempt}/{Max}, retrying in {Delay}ms",
                op, attempt, MaxTransientReadRetries, delay.TotalMilliseconds),
            scheduler: null);

    /// <summary>
    /// Deterministically testable core of <see cref="WithTransientRetry{T}"/>: re-subscribes the
    /// cold <paramref name="read"/> on a transient fault with <paramref name="backoff"/> delay, up
    /// to <paramref name="maxRetries"/>, then lets the fault propagate. Split out (with an injectable
    /// <paramref name="scheduler"/>) so tests can drive the backoff with a <c>TestScheduler</c> —
    /// mirrors <c>RoutingGrain.DeliverToGrainObservable</c>.
    /// </summary>
    internal static IObservable<T> RetryTransientReads<T>(
        Func<IObservable<T>> read,
        Func<Exception, bool> isTransient,
        int maxRetries,
        Func<int, TimeSpan> backoff,
        Action<Exception, int, TimeSpan>? onRetry = null,
        IScheduler? scheduler = null)
    {
        var sch = scheduler ?? Scheduler.Default;
        return Observable.Defer(read)
            .RetryWhen(errors => errors
                .Select((ex, i) => (Exception: ex, Attempt: i))
                .SelectMany(t =>
                {
                    if (t.Attempt >= maxRetries || !isTransient(t.Exception))
                        return Observable.Throw<long>(t.Exception);
                    var delay = backoff(t.Attempt);
                    onRetry?.Invoke(t.Exception, t.Attempt + 1, delay);
                    return Observable.Timer(delay, sch);
                }));
    }

    /// <inheritdoc />
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => WithTransientRetry(() => _readPool.Invoke(ct => ReadAsyncCore(path, options, ct)), "Read");

    private async Task<MeshNode?> ReadAsyncCore(string path, JsonSerializerOptions options, CancellationToken ct)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath))
            return null;

        var (ns, id) = SplitPath(normalizedPath);

        var table = ResolveTable(normalizedPath);
        try
        {
            await using var cmd = _dataSource.CreateCommand(
                $"SELECT id, namespace, name, description, node_type, category, icon, display_order, " +
                $"last_modified, version, state, content, desired_id, main_node, {SyncBehaviorCol(table)}, {AuthorCols(table)}, {ExcludeCol(table)} " +
                $"FROM {table} WHERE namespace = $1 AND id = $2");
            cmd.Parameters.AddWithValue(ns);
            cmd.Parameters.AddWithValue(id);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            return ReadMeshNode(reader, options);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Half-provisioned partition: the schema exists (so PgPartitionCache.Probe
            // reported Exists on information_schema.schemata and routed us here) but its
            // mesh_nodes / satellite table was never created. There is no node to read →
            // null, NOT an error. Without this guard the create existence-check
            // (HandleCreateNodeRequest → persistence.Read) faults with 42P01 BEFORE
            // SpaceTopLevelValidator can provision the tables, so a top-level Space can
            // never be (re)created over a bare schema — the prod Systemorph-space bug
            // (2026-06-02): `systemorph` schema present, zero tables, space invisible.
            _logger?.LogDebug(ex,
                "Read on {Table} for '{Path}' hit undefined_table (42P01); treating as no node " +
                "(bare/half-provisioned partition).",
                table, normalizedPath);
            return null;
        }
    }

    /// <summary>
    /// Batched override of <see cref="IStorageAdapter.ReadMany"/> — multi-path
    /// probes (URL resolver's <c>path:a|b|c</c> longest-prefix search,
    /// activity bulk reads) become ONE SQL query instead of N. Groups input
    /// paths by (table, namespace) so a mixed batch with rows in different
    /// tables / namespaces still runs as one query per (table, namespace)
    /// group rather than per-path.
    /// </summary>
    // Pump inside the IIoPool (InvokeStream) — never Observable.Create(async ...),
    // which starts the pump (incl. the synchronous grouping prologue and the
    // command construction) on the SUBSCRIBER's thread; under a hub/grain
    // subscriber that is the grain-wedge / dropped-initial-emission defect
    // (see PartitionObjectsSubscriberIndependenceTest for the repro shape).
    public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
        => _readPool.InvokeStream(ct => ReadManyAsyncCore(paths, options, ct));

    private async IAsyncEnumerable<MeshNode> ReadManyAsyncCore(
        IReadOnlyCollection<string> paths,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Normalize + drop empties up front. Group by (table, namespace)
        // so each PG round-trip is `WHERE namespace = $1 AND id IN (...)`
        // — the cheapest shape for the indexed (namespace, id) PK.
        var groups = paths
            .Select(NormalizePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p =>
            {
                var (ns, id) = SplitPath(p);
                var table = ResolveTable(p);
                return (table, ns, id);
            })
            .GroupBy(t => (t.table, t.ns))
            .ToList();

        foreach (var group in groups)
        {
            var table = group.Key.table;
            var ns = group.Key.ns;
            var ids = group.Select(t => t.id).Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length == 0)
                continue;

            // Build the parameter placeholder list ($2, $3, …) for the
            // IN clause; the first parameter is the namespace.
            var placeholders = string.Join(", ",
                Enumerable.Range(2, ids.Length).Select(i => $"${i}"));
            await using var cmd = _dataSource.CreateCommand(
                $"SELECT id, namespace, name, description, node_type, category, icon, display_order, " +
                $"last_modified, version, state, content, desired_id, main_node, {SyncBehaviorCol(table)}, {AuthorCols(table)}, {ExcludeCol(table)} " +
                $"FROM {table} WHERE namespace = $1 AND id IN ({placeholders})");
            cmd.Parameters.AddWithValue(ns);
            foreach (var id in ids)
                cmd.Parameters.AddWithValue(id);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                yield return ReadMeshNode(reader, options);
            }
        }
    }

    /// <inheritdoc />
    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => _ioPool.Invoke<MeshNode?>(async ct =>
        {
            var applied = await WriteAsyncCore(node, options, ct).ConfigureAwait(false);
            if (!applied)
            {
                // The version-conditional upsert left the row alone: it already carries a HIGHER
                // MeshNode.Version (#971). Emit the STORED row — that is what the write-integrity
                // chain merges the losing write into — and publish NOTHING, because nothing changed
                // and a notification carrying the losing node would hand every subscriber the stale
                // state the store just rejected.
                var stored = await ReadAsyncCore(node.Path, options, ct).ConfigureAwait(false);
                _logger?.LogWarning(
                    "[PostgreSqlStorageAdapter] write to {Path} at Version={IncomingVersion} was REFUSED by the "
                    + "version condition; the durable row is at Version={StoredVersion}. MeshNode.Version is the "
                    + "owner's monotonic persistence clock, so the losing write is a stale snapshot — it is merged "
                    + "into durable truth by the write-integrity chain, never applied over it.",
                    node.Path, node.Version, stored?.Version);
                return stored ?? node;
            }
            // Fire the in-process Changes feed so same-process synced-query
            // subscribers re-emit without waiting for the PG NOTIFY round-trip.
            // PostgreSqlChangeListener still publishes for cross-process; the
            // listener's pg_notify dedup (PostgreSqlExtensions LISTEN/NOTIFY
            // dedup) makes the double-fire idempotent.
            // No try/catch: IsolatedChangeFeed already isolates and LOGS a faulty observer, so a
            // throw escaping here would be a bug in the feed itself, not a subscriber's fault to
            // swallow.
            _changes.OnNext(DataChangeNotification.Updated(
                string.IsNullOrEmpty(node.Path) ? node.Id : node.Path, node));
            return node;
        });

    /// <inheritdoc />
    /// <remarks>
    /// Windows the nodes by TARGET TABLE and sends one <see cref="NpgsqlBatch"/> per window:
    /// N upserts, one round-trip, one implicit transaction each. The per-node SQL is the exact
    /// same upsert <see cref="Write"/> uses (both go through <see cref="BuildUpsertAsync"/>), so
    /// batching changes only how many times we cross the wire.
    ///
    /// <para>Windowing is by table, not merely by partition, because <see cref="ResolveTable"/>
    /// routes satellite node types to their own tables — a course's subtree can span several — and
    /// a batch may only carry commands for one target. Grouping preserves first-seen order so the
    /// windows themselves stay in caller order.</para>
    ///
    /// <para>🚨 The <see cref="Changes"/> feed is published in the CALLER's original order, after
    /// all windows commit — never in per-window order. Those notifications are what wake per-node
    /// hubs, and callers order parents before children precisely so a child's hub never activates
    /// against a cold parent. Storage has no ordering to lose inside a transaction; the change feed
    /// does.</para>
    /// </remarks>
    public IObservable<IReadOnlyList<MeshNode>> WriteMany(
        IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
    {
        if (nodes.Count == 0)
            return Observable.Return<IReadOnlyList<MeshNode>>([]);
        if (nodes.Count == 1)
            return Write(nodes.First(), options)
                .Select(n => (IReadOnlyList<MeshNode>)(n is null ? [] : new[] { n }));

        return _ioPool.Invoke<IReadOnlyList<MeshNode>>(async ct =>
        {
            var ordered = nodes.ToList();

            // Build every upsert first (this is where the embedding calls happen), keeping the
            // caller's order, then window by target table.
            var built = new List<(MeshNode Node, string Table, string Sql, IReadOnlyList<NpgsqlParameter> Parameters)>(ordered.Count);
            foreach (var node in ordered)
            {
                var (sql, parameters) = await BuildUpsertAsync(node, options).ConfigureAwait(false);
                built.Add((node, ResolveTable(node.Path, node.NodeType), sql, parameters));
            }

            // Each window is its own implicit transaction, so a later window failing does NOT roll
            // back an earlier one. Track what actually committed: those rows are in storage, and
            // storage that no hub has been told about is worse than a clean failure — the node is
            // there, every per-node hub and synced-query subscriber still believes it is not, and
            // nothing forces a refresh. So the committed set is ANNOUNCED on both paths.
            var committed = new HashSet<string>(StringComparer.Ordinal);
            // Paths whose version-conditional upsert was REFUSED (#971) — the durable row already
            // carries a higher MeshNode.Version. They are neither committed nor a failure: the batch
            // succeeded, this node's write simply did not apply, so it must not be announced on the
            // change feed and the caller must be handed the DURABLE row rather than the loser.
            var refused = new List<MeshNode>();
            try
            {
                foreach (var window in built.GroupBy(b => b.Table, StringComparer.Ordinal))
                {
                    await using var batch = _dataSource.CreateBatch();
                    var items = window.ToList();
                    foreach (var item in items)
                    {
                        var command = new NpgsqlBatchCommand(item.Sql);
                        foreach (var parameter in item.Parameters)
                            command.Parameters.Add(parameter);
                        batch.BatchCommands.Add(command);
                    }
                    await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    for (var i = 0; i < items.Count; i++)
                    {
                        if (batch.BatchCommands[i].Rows > 0)
                            committed.Add(items[i].Node.Path);
                        else
                            refused.Add(items[i].Node);
                    }
                }
            }
            catch
            {
                PublishChanges(ordered, committed);   // announce what landed, then fail
                throw;
            }

            PublishChanges(ordered, committed);
            if (refused.Count == 0)
                return ordered;

            _logger?.LogWarning(
                "[PostgreSqlStorageAdapter] {Count} node(s) in a batch write were REFUSED by the version "
                + "condition — the durable rows are newer: {Paths}. The stored rows are returned so the "
                + "write-integrity chain merges into durable truth instead of overwriting it.",
                refused.Count, string.Join(", ", refused.Select(n => $"{n.Path}@v{n.Version}")));

            // Hand back durable truth for the refused paths, in the caller's original order.
            var stored = new Dictionary<string, MeshNode>(StringComparer.Ordinal);
            await foreach (var durable in ReadManyAsyncCore(
                               [.. refused.Select(n => n.Path)], options, ct).ConfigureAwait(false))
                stored[durable.Path] = durable;
            return [.. ordered.Select(n => stored.TryGetValue(n.Path, out var d) ? d : n)];
        });
    }

    /// <summary>
    /// Publishes the in-process <see cref="Changes"/> feed for the nodes that COMMITTED, in the
    /// CALLER's order — never window order. Those notifications are what wake per-node hubs, and
    /// callers order parents before children so a child's hub never activates against a cold parent.
    /// Best-effort by contract: the feed must never turn a successful write into a failure.
    /// </summary>
    private void PublishChanges(IReadOnlyList<MeshNode> ordered, IReadOnlySet<string> committed)
    {
        foreach (var node in ordered)
        {
            if (!committed.Contains(node.Path))
                continue;
            _changes.OnNext(DataChangeNotification.Updated(
                string.IsNullOrEmpty(node.Path) ? node.Id : node.Path, node));
        }
    }

    /// <summary>
    /// Runs the one-node upsert and reports whether it APPLIED. The upsert is version-conditional
    /// (see <see cref="BuildUpsertAsync"/>), so a row count of zero is not an error — it is the store
    /// refusing a write whose <see cref="MeshNode.Version"/> is below the durable row's (#971).
    /// </summary>
    private async Task<bool> WriteAsyncCore(
        MeshNode node, JsonSerializerOptions options, CancellationToken ct, long? expectedVersion = null)
    {
        var (sql, parameters) = await BuildUpsertAsync(node, options, expectedVersion).ConfigureAwait(false);
        await using var cmd = _dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
            cmd.Parameters.Add(parameter);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The atomic cross-replica compare-and-set: ONE statement whose row count is the verdict.
    /// Row-level locking serialises concurrent upserts on the same key, so of N callers that each
    /// read the row at <c>expectedVersion</c> exactly one sees a row count of 1 — that is the whole
    /// exclusivity guarantee, and it holds across processes, silos and Orleans clusters because it
    /// is the database, not any in-process gate, that decides. <c>expectedVersion == 0</c> compiles
    /// to <c>ON CONFLICT … DO NOTHING</c> ("insert only if absent"); anything else adds
    /// <c>WHERE target.version = @expected</c>. The change feed fires only for the winner.
    /// </remarks>
    public IObservable<bool?> WriteIfVersion(
        MeshNode node, long expectedVersion, JsonSerializerOptions options)
        => _ioPool.Invoke<bool?>(async ct =>
        {
            var applied = await WriteAsyncCore(node, options, ct, expectedVersion).ConfigureAwait(false);
            if (!applied)
                return false;
            _changes.OnNext(DataChangeNotification.Updated(
                string.IsNullOrEmpty(node.Path) ? node.Id : node.Path, node));
            return true;
        });

    /// <summary>
    /// One positional upsert parameter, with its PostgreSQL type stated explicitly.
    ///
    /// <para>🚨 Never <c>AddWithValue</c> here. Npgsql sends a parameter whose type it does not know
    /// — which is every <c>DBNull</c> — with the unspecified OID 0, delegating the decision to the
    /// SERVER, which infers it from where the parameter is USED. The compare-and-set branch below
    /// deliberately binds parameters it never references (<c>created_by</c> and <c>created_date</c>
    /// are immutable, so the UPDATE omits them while the positional layout still carries them), and
    /// for those there is nothing to infer from: PostgreSQL rejects the whole statement with
    /// <c>42P18: could not determine data type of parameter $n</c>, naming whichever untyped
    /// unreferenced parameter comes first. That is not a hypothetical — it wedged a production
    /// portal's readiness, because the build-claim lock is written by the framework itself and so
    /// carries no authorship at all, and every arbitration pass past the first (insert-only, where
    /// every parameter IS referenced) failed forever: no builder elected, no NodeType bake, no
    /// ready pod.</para>
    ///
    /// <para>Stating the type removes the dependence on inference for EVERY parameter and every
    /// branch, so no later change to which columns a statement mentions can re-arm this. Fixing
    /// only the two columns that happen to be unreferenced today would leave that trap set.</para>
    /// </summary>
    /// <param name="type">The column's PostgreSQL type.</param>
    /// <param name="value">The value, or <c>null</c> for SQL NULL.</param>
    /// <returns>An unnamed (positional) parameter carrying an explicit type.</returns>
    private static NpgsqlParameter Typed(NpgsqlDbType type, object? value)
        => new() { NpgsqlDbType = type, Value = value ?? DBNull.Value };

    /// <summary>
    /// The <c>embedding</c> parameter. pgvector's type has no <see cref="NpgsqlDbType"/> member, so
    /// it is named directly — the data sources this adapter is built over all register the mapping
    /// (<c>UseVector()</c>), and naming it means a NULL embedding is typed exactly like a present
    /// one instead of relying on the column context.
    /// </summary>
    /// <param name="embedding">The embedding, or <c>null</c> when none was generated.</param>
    /// <returns>An unnamed (positional) <c>vector</c> parameter.</returns>
    private static NpgsqlParameter TypedVector(float[]? embedding)
        => new() { DataTypeName = "vector", Value = embedding is null ? DBNull.Value : new Vector(embedding) };

    /// <summary>
    /// The upsert for ONE node: its SQL text and its positional parameters, in order.
    /// Shared by <see cref="WriteAsyncCore"/> (one command) and <see cref="WriteMany"/>
    /// (one <see cref="NpgsqlBatchCommand"/> per node) so the two paths can never drift —
    /// in particular the ON CONFLICT set, which deliberately omits created_by/created_date
    /// so an update preserves the original author.
    /// </summary>
    /// <param name="node">The node to upsert.</param>
    /// <param name="options">Serializer options for the content payload.</param>
    /// <param name="expectedVersion">
    /// <c>null</c> — the ordinary monotonic condition (<c>target.version &lt;= EXCLUDED.version</c>),
    /// which APPLIES at equal versions because re-persisting an unchanged node is legitimate.
    /// Non-null switches the statement to compare-and-set for
    /// <see cref="WriteIfVersion"/>: <c>0</c> means "only if no row exists"
    /// (<c>ON CONFLICT … DO NOTHING</c>), anything else means "only while the row still carries
    /// exactly this version". The equality is what makes the write EXCLUSIVE rather than merely
    /// non-regressing — see the contract note on <see cref="IStorageAdapter.WriteIfVersion"/>.
    /// </param>
    private async Task<(string Sql, IReadOnlyList<NpgsqlParameter> Parameters)> BuildUpsertAsync(
        MeshNode node, JsonSerializerOptions options, long? expectedVersion = null)
    {
        var ns = node.Namespace ?? "";

        var contentJson = node.Content != null
            ? JsonSerializer.Serialize(node.Content, node.Content.GetType(), options)
            : null;

        // 🚨 Refuse a payload jsonb provably cannot hold, BEFORE the round-trip (#1449). `content` is
        // bound as `$12::jsonb`, and jsonb stores DECODED text — PostgreSQL text cannot contain a NUL
        // byte, so the server rejects `\u0000` with `22P05: unsupported Unicode escape sequence` and
        // a DETAIL that connection policy redacts. That error names neither the node nor the field,
        // and on the WriteMany path it fails the whole batch while naming none of its members. This
        // check is the same statement's own precondition, in the one method both paths share, so the
        // two can never disagree about what is storable. It never truncates or rewrites the content:
        // the value is unstorable by construction, so the only honest outcomes are "store it" and
        // "say exactly what is wrong with it".
        //
        // Deliberately AHEAD of the embedding call below: that call can be an EXTERNAL
        // round-trip, and a write that is already doomed must not pay for one — nor, on the
        // batch path, for one per node before the batch dies. Serialization has to happen
        // first regardless; the check itself is the cheap part.
        if (UnstorableContentException.IsUnstorable(contentJson))
            throw UnstorableContentException.NulInContent(node.Path, contentJson!);

        // Generate embedding
        var embeddingText = string.Join(" ",
            new[] { node.Name, node.NodeType }
                .Where(s => !string.IsNullOrEmpty(s)));
        var embeddingVector = await _embeddingProvider.GenerateEmbeddingAsync(embeddingText).ConfigureAwait(false);

        var table = ResolveTable(node.Path, node.NodeType);
        // sync_behavior lives only on mesh_nodes (the sole decouplable table); satellite
        // tables don't carry it, so write/update it only when targeting mesh_nodes.
        var writeSync = table.Contains("mesh_nodes", StringComparison.OrdinalIgnoreCase);
        var syncInsertCol = writeSync ? ", sync_behavior" : "";
        var syncInsertVal = writeSync ? ", $16" : "";
        var syncUpdate = writeSync ? ",\n                sync_behavior = EXCLUDED.sync_behavior" : "";
        // Authorship columns live only on mesh_nodes (like sync_behavior). created_by /
        // created_date are IMMUTABLE — set once at INSERT, never in the ON CONFLICT SET —
        // so an update preserves the original creator; only last_modified_by is refreshed.
        var authorInsertCol = writeSync ? ", created_by, last_modified_by, created_date" : "";
        var authorInsertVal = writeSync ? ", $17, $18, $19" : "";
        var authorUpdate = writeSync ? ",\n                last_modified_by = EXCLUDED.last_modified_by" : "";
        // exclude_from_context lives only on mesh_nodes (like sync_behavior/authorship).
        var excludeInsertCol = writeSync ? ", exclude_from_context" : "";
        var excludeInsertVal = writeSync ? ", $20" : "";
        var excludeUpdate = writeSync ? ",\n                exclude_from_context = EXCLUDED.exclude_from_context" : "";
        // 🚨 `AS target` + the trailing WHERE make this a version-CONDITIONAL upsert (#971): the row is
        // left untouched when it already carries a HIGHER MeshNode.Version than the incoming node.
        // MeshNode.Version is the owner's forward-only revision counter, so a regressing write is a
        // stale snapshot about to destroy acknowledged data — and this predicate is the ONLY thing that
        // stops it cross-replica. The in-process high-water filter in MonotonicWriteGuardStorageAdapter
        // is empty on a freshly started pod, so that pod's FIRST write to a path used to be guarded by
        // nothing at all: not the empty mark, not the (previously unconditional) UPDATE. Equal versions
        // still apply — re-persisting an unchanged node is a legitimate, common shape. The alias is
        // required: inside ON CONFLICT DO UPDATE the target row is referenced by its range-table name,
        // and `{table}` is schema-qualified.
        var parameters = new List<NpgsqlParameter>(21)
        {
            Typed(NpgsqlDbType.Text, ns),
            Typed(NpgsqlDbType.Text, node.Id),
            Typed(NpgsqlDbType.Text, node.Name),
            Typed(NpgsqlDbType.Text, node.Description),
            Typed(NpgsqlDbType.Text, node.NodeType),
            Typed(NpgsqlDbType.Text, node.Category),
            Typed(NpgsqlDbType.Text, node.Icon),
            Typed(NpgsqlDbType.Integer, node.Order),
            Typed(NpgsqlDbType.TimestampTz,
                node.LastModified == default ? DateTimeOffset.UtcNow : node.LastModified),
            Typed(NpgsqlDbType.Bigint, node.Version),
            Typed(NpgsqlDbType.Smallint, (short)node.State),
            Typed(NpgsqlDbType.Text, contentJson),   // bound as text, cast to jsonb in the statement
            Typed(NpgsqlDbType.Text, node.DesiredId),
            TypedVector(embeddingVector),
            Typed(NpgsqlDbType.Text, node.MainNode),
        };

        // $16–$20 — only bound when the target is mesh_nodes (see writeSync above).
        if (writeSync)
        {
            parameters.Add(Typed(NpgsqlDbType.Smallint, (short)node.SyncBehavior));
            parameters.Add(Typed(NpgsqlDbType.Text, node.CreatedBy));
            parameters.Add(Typed(NpgsqlDbType.Text, node.LastModifiedBy));
            parameters.Add(Typed(NpgsqlDbType.TimestampTz,
                node.CreatedDate == default ? null : node.CreatedDate));
            parameters.Add(Typed(NpgsqlDbType.Array | NpgsqlDbType.Text,
                node.ExcludeFromContext is { Count: > 0 } efc ? efc.ToArray() : null));
        }

        // The conflict action. Built AFTER the parameter list so the compare-and-set predicate can
        // bind the next free positional slot ($16 or $21, depending on writeSync).
        //   null  → the ordinary monotonic guard: apply unless the row is already NEWER.
        //   0     → insert-only: exclusive create, the row must not exist.
        //   v     → compare-and-set: apply only while the row still carries exactly v.
        // 🚨 expectedVersion > 0 is a PLAIN UPDATE, never an upsert. "The row still carries exactly
        // v" is false when there is no row at all, and an INSERT ... ON CONFLICT would have
        // RESURRECTED a deleted row and reported success — which for the build claim means a
        // heartbeat racing a release re-creates the lock its holder just dropped and blocks the next
        // candidate for the whole staleness budget. The in-memory adapter refuses the same case;
        // the two backends must not disagree about what compare-and-set means.
        if (expectedVersion is > 0)
        {
            parameters.Add(Typed(NpgsqlDbType.Bigint, expectedVersion.Value));
            // Mirrors the ON CONFLICT SET list exactly — created_by / created_date stay untouched
            // (insert-only there, absent here), so authorship survives a compare-and-set too.
            // 🚨 That makes $17 and $19 BOUND BUT NEVER REFERENCED in this statement. Legal only
            // because every parameter carries an explicit type (see Typed): an untyped one here has
            // no usage to infer from and 42P18s the whole statement. Do not switch this list back
            // to AddWithValue, and do not "tidy" the layout by assuming a bound parameter is used.
            var casSync = writeSync ? ",\n                sync_behavior = $16" : "";
            var casAuthor = writeSync ? ",\n                last_modified_by = $18" : "";
            var casExclude = writeSync ? ",\n                exclude_from_context = $20" : "";
            return (
                $"""
                UPDATE {table} SET
                    name = $3,
                    description = $4,
                    node_type = $5,
                    category = $6,
                    icon = $7,
                    display_order = $8,
                    last_modified = $9,
                    version = $10,
                    state = $11,
                    content = $12::jsonb,
                    desired_id = $13,
                    embedding = $14,
                    main_node = $15{casSync}{casAuthor}{casExclude}
                WHERE namespace = $1 AND id = $2 AND version = ${parameters.Count}
                """,
                parameters);
        }

        var conflict = expectedVersion == 0
            // Insert-only: an exclusive create, and the row must not already exist.
            ? "ON CONFLICT (namespace, id) DO NOTHING"
            : $"""
                ON CONFLICT (namespace, id) DO UPDATE SET
                    name = EXCLUDED.name,
                    description = EXCLUDED.description,
                    node_type = EXCLUDED.node_type,
                    category = EXCLUDED.category,
                    icon = EXCLUDED.icon,
                    display_order = EXCLUDED.display_order,
                    last_modified = EXCLUDED.last_modified,
                    version = EXCLUDED.version,
                    state = EXCLUDED.state,
                    content = EXCLUDED.content,
                    desired_id = EXCLUDED.desired_id,
                    embedding = EXCLUDED.embedding,
                    main_node = EXCLUDED.main_node{syncUpdate}{authorUpdate}{excludeUpdate}
                WHERE target.version <= EXCLUDED.version
                """;

        var sql =
            $"""
            INSERT INTO {table} AS target (namespace, id, name, description, node_type, category, icon, display_order,
                                    last_modified, version, state, content, desired_id, embedding, main_node{syncInsertCol}{authorInsertCol}{excludeInsertCol})
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12::jsonb, $13, $14, $15{syncInsertVal}{authorInsertVal}{excludeInsertVal})
            {conflict}
            """;

        return (sql, parameters);
    }

    /// <inheritdoc />
    public IObservable<string> Delete(string path)
        => _ioPool.Invoke(async ct =>
        {
            await DeleteAsyncCore(path, ct).ConfigureAwait(false);
            _changes.OnNext(DataChangeNotification.Deleted(path));
            return path;
        });

    /// <inheritdoc />
    /// <remarks>
    /// Strict semantics via the DELETE row count: the single SQL statement is the
    /// atomic cross-replica "first delete wins" gate (two concurrent consumers of
    /// the same row get exactly one <c>true</c> between them — row-level locking
    /// serialises the deletes). The change notification fires only for the winner.
    /// </remarks>
    public IObservable<bool> DeleteIfExists(string path)
        => _ioPool.Invoke(async ct =>
        {
            var removed = await DeleteAsyncCore(path, ct).ConfigureAwait(false) > 0;
            if (removed)
            {
                _changes.OnNext(DataChangeNotification.Deleted(path));
            }
            return removed;
        });

    private async Task<int> DeleteAsyncCore(string path, CancellationToken ct)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath))
            return 0;

        var (ns, id) = SplitPath(normalizedPath);

        var table = ResolveTable(normalizedPath);
        await using var cmd = _dataSource.CreateCommand(
            $"DELETE FROM {table} WHERE namespace = $1 AND id = $2");
        cmd.Parameters.AddWithValue(ns);
        cmd.Parameters.AddWithValue(id);

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // Child-listing is a READ → runs in the read pool (pg-read:{adapter}), bounded below the
    // connection-pool size, NOT the cap-1 write pool (which would serialise it behind writes).
    /// <inheritdoc />
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
        => _readPool.Invoke(ct => ListChildPathsAsyncCore(parentPath, ct))
            .Catch<(IEnumerable<string>, IEnumerable<string>), Exception>(ex => IsUndefinedTable(ex)
                ? Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], []))
                : Observable.Throw<(IEnumerable<string>, IEnumerable<string>)>(ex));

    private async Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsyncCore(
        string? parentPath,
        CancellationToken ct)
    {
        var normalizedParent = NormalizePath(parentPath);

        var table = ResolveTable(normalizedParent);
        await using var cmd = _dataSource.CreateCommand(
            $"SELECT id, namespace FROM {table} WHERE namespace = $1");
        cmd.Parameters.AddWithValue(normalizedParent);

        var paths = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var ns = reader.GetString(1);
            var nodePath = string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}";
            paths.Add(nodePath);
        }

        return (paths, Enumerable.Empty<string>());
    }

    /// <summary>
    /// Native authoritative descendant enumeration: ONE round-trip UNION across the
    /// partition's primary <c>mesh_nodes</c> table AND every satellite table named in
    /// <see cref="PartitionDefinition.TableMappings"/> (threads, access, activities,
    /// annotations, code, …), matching every row whose namespace equals the root or is
    /// prefixed by it. This is what the recursive-delete planner and its post-delete
    /// verification run on — the interface default's <see cref="ListChildPaths"/> walk
    /// cannot work here because the PG child listing is a flat single-table
    /// namespace-equality scan with no directory levels, so it would never descend into
    /// node-less intermediate segments (<c>{path}/_Thread/{id}</c>,
    /// <c>{nodeType}/Release/{version}</c>) — exactly the rows that survived (issue #839).
    /// An absent (never-provisioned) schema means nothing to enumerate → empty.
    /// </summary>
    public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
        => _readPool.Invoke(ct => ListDescendantPathsAsyncCore(rootPath, ct))
            .Catch<IReadOnlyCollection<string>, Exception>(ex => IsUndefinedTable(ex)
                ? Observable.Return<IReadOnlyCollection<string>>([])
                : Observable.Throw<IReadOnlyCollection<string>>(ex));

    private async Task<IReadOnlyCollection<string>> ListDescendantPathsAsyncCore(
        string rootPath, CancellationToken ct)
    {
        var normalizedRoot = NormalizePath(rootPath);

        // Primary + every distinct satellite table (case-insensitive dedup —
        // multiple suffixes can map to the same table, e.g. _Comment / _Approval /
        // _Tracking all → annotations). Same table set as ResolvePathAsyncCore.
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mesh_nodes" };
        if (_partitionDefinition?.TableMappings is { } mappings)
            foreach (var t in mappings.Values)
                if (!string.IsNullOrEmpty(t))
                    tables.Add(t);

        // A descendant row's namespace either equals the root (direct children) or is
        // prefixed by `{root}/`. 🚨 The prefix predicate is LIKE with an explicit
        // ESCAPE and an escaped pattern: mesh paths routinely contain `_`
        // (`X/_Thread`), which is a single-char LIKE wildcard — unescaped it would
        // match sibling subtrees (`X/aThread/…`) into the DELETION plan.
        var branches = tables.Select(t =>
        {
            var qualified = string.IsNullOrEmpty(_schemaName)
                ? $"\"{t}\""
                : $"\"{_schemaName}\".\"{t}\"";
            return string.IsNullOrEmpty(normalizedRoot)
                ? $"SELECT namespace, id FROM {qualified}"
                : $"SELECT namespace, id FROM {qualified} WHERE namespace = $1 OR namespace LIKE $2 ESCAPE '\\'";
        });
        var sql = string.Join("\n UNION ALL\n", branches);

        await using var cmd = _dataSource.CreateCommand(sql);
        if (!string.IsNullOrEmpty(normalizedRoot))
        {
            cmd.Parameters.AddWithValue(normalizedRoot);
            cmd.Parameters.AddWithValue(EscapeLikePattern(normalizedRoot) + "/%");
        }

        var paths = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var ns = reader.GetString(0);
            var id = reader.GetString(1);
            paths.Add(string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}");
        }

        return paths;
    }

    /// <summary>
    /// Escapes the LIKE wildcards (<c>\</c>, <c>%</c>, <c>_</c>) in a literal path so it can
    /// be used as a LIKE prefix with <c>ESCAPE '\'</c>.
    /// </summary>
    private static string EscapeLikePattern(string literal)
        => literal
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

    /// <inheritdoc />
    public IObservable<bool> Exists(string path)
        => WithTransientRetry(() => _readPool.Invoke(ct => ExistsAsyncCore(path, ct)), "Exists")
            .Catch<bool, Exception>(ex => IsUndefinedTable(ex)
                ? Observable.Return(false)
                : Observable.Throw<bool>(ex));

    private async Task<bool> ExistsAsyncCore(string path, CancellationToken ct)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath))
            return false;

        var (ns, id) = SplitPath(normalizedPath);

        var table = ResolveTable(normalizedPath);
        await using var cmd = _dataSource.CreateCommand(
            $"SELECT 1 FROM {table} WHERE namespace = $1 AND id = $2 LIMIT 1");
        cmd.Parameters.AddWithValue(ns);
        cmd.Parameters.AddWithValue(id);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => WithTransientRetry(() => _readPool.Invoke(ct => FindBestPrefixMatchAsyncCore(fullPath, options, ct)), "FindBestPrefixMatch")
            .Catch<(MeshNode?, int), Exception>(ex => IsUndefinedTable(ex)
                ? Observable.Return<(MeshNode?, int)>((null, 0))
                : Observable.Throw<(MeshNode?, int)>(ex));

    private async Task<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatchAsyncCore(
        string fullPath, JsonSerializerOptions options, CancellationToken ct)
    {
        var normalizedPath = NormalizePath(fullPath);
        if (string.IsNullOrEmpty(normalizedPath))
            return (null, 0);

        // Single SQL query: find the node whose path is the longest prefix of the input.
        // Matches exact path or any ancestor (input starts with path + '/').
        // Ordered by path length descending to get the deepest (most specific) match first.
        var table = ResolveTable(normalizedPath);
        await using var cmd = _dataSource.CreateCommand(
            $"SELECT id, namespace, name, description, node_type, category, icon, display_order, " +
            $"last_modified, version, state, content, desired_id, main_node, {SyncBehaviorCol(table)}, {AuthorCols(table)}, {ExcludeCol(table)} " +
            $"FROM {table} WHERE $1 = path OR $1 LIKE path || '/%' " +
            $"ORDER BY LENGTH(path) DESC LIMIT 1");
        cmd.Parameters.AddWithValue(normalizedPath);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return (null, 0);

        var node = ReadMeshNode(reader, options);
        var matchedSegments = node.Path.Split('/').Length;
        return (node, matchedSegments);
    }

    /// <summary>
    /// Resolves the closest-matching MeshNode for <paramref name="fullPath"/>
    /// across the partition's primary <c>mesh_nodes</c> table AND every
    /// satellite table named in <see cref="PartitionDefinition.TableMappings"/>
    /// in a SINGLE round-trip. The UNION emits the longest-path match across
    /// all tables; the outer ORDER BY picks the deepest one. Old multi-step
    /// resolver took up to 1+N+N queries — this replaces it with one.
    /// Contract: <c>PathResolutionTests</c>.
    /// </summary>
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => WithTransientRetry(() => _readPool.Invoke(ct => ResolvePathAsyncCore(fullPath, options, ct)), "ResolvePath")
            .Catch<(MeshNode?, int), Exception>(ex => IsUndefinedTable(ex)
                ? Observable.Return<(MeshNode?, int)>((null, 0))
                : Observable.Throw<(MeshNode?, int)>(ex));

    private async Task<(MeshNode? Node, int MatchedSegments)> ResolvePathAsyncCore(
        string fullPath, JsonSerializerOptions options, CancellationToken ct)
    {
        var normalizedPath = NormalizePath(fullPath);
        if (string.IsNullOrEmpty(normalizedPath))
            return (null, 0);

        // Build the set of tables to query: primary + every distinct
        // satellite table named in TableMappings (case-insensitive dedup —
        // multiple suffixes can map to the same table, e.g. _Comment /
        // _Approval / _Tracking all → annotations).
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mesh_nodes" };
        if (_partitionDefinition?.TableMappings is { } mappings)
            foreach (var t in mappings.Values)
                if (!string.IsNullOrEmpty(t))
                    tables.Add(t);

        // Single CTE-based query: each UNION-ALL branch selects from one
        // table; the outer ORDER BY + LIMIT picks the deepest path-prefix
        // match across all tables. The path-prefix predicate is identical
        // per branch; Postgres' planner can use the path index on each
        // table. One round-trip regardless of satellite table count.
        var unionBranches = new List<string>(tables.Count);
        foreach (var t in tables)
        {
            var qualified = string.IsNullOrEmpty(_schemaName)
                ? $"\"{t}\""
                : $"\"{_schemaName}\".\"{t}\"";
            unionBranches.Add(
                $"SELECT id, namespace, name, description, node_type, category, icon, display_order, " +
                $"last_modified, version, state, content, desired_id, main_node, {SyncBehaviorCol(qualified)}, {AuthorCols(qualified)}, {ExcludeCol(qualified)} " +
                $"FROM {qualified} " +
                $"WHERE $1 = path OR $1 LIKE path || '/%'");
        }
        var sql =
            "WITH candidates AS (\n" +
            string.Join("\n UNION ALL\n", unionBranches) +
            "\n) " +
            "SELECT * FROM candidates " +
            "ORDER BY LENGTH(CASE WHEN namespace = '' THEN id ELSE namespace || '/' || id END) DESC " +
            "LIMIT 1";

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(normalizedPath);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return (null, 0);

        var node = ReadMeshNode(reader, options);
        var matchedSegments = node.Path.Split('/').Length;
        return (node, matchedSegments);
    }

    #region Partition Storage

    // Pump inside the IIoPool (InvokeStream) — never Observable.Create(async ...),
    // which starts the pump on the subscriber's scheduler. This is the
    // virtual-data-source load that runs at hub init — the exact grain-wedge
    // edge (see PartitionObjectsSubscriberIndependenceTest for the repro shape).
    /// <inheritdoc />
    public IObservable<object> GetPartitionObjects(
        string nodePath, string? subPath, JsonSerializerOptions options)
        => _readPool.InvokeStream(ct => GetPartitionObjectsAsyncCore(nodePath, subPath, options, ct))
            .Catch<object, Exception>(ex => IsUndefinedTable(ex)
                // Absent schema (router resolved synchronously, schema never
                // created) → nothing to read. Complete empty, don't fault.
                ? Observable.Empty<object>()
                : Observable.Throw<object>(ex));

    private async IAsyncEnumerable<object> GetPartitionObjectsAsyncCore(
        string nodePath, string? subPath, JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var partitionKey = GetPartitionStorageKey(nodePath, subPath);

        var poTable = QualifyTable("partition_objects");
        await using var cmd = _dataSource.CreateCommand(
            $"SELECT data, type_name FROM {poTable} WHERE partition_key = $1");
        cmd.Parameters.AddWithValue(partitionKey);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var json = reader.GetString(0);
            json = EnsureTypeDiscriminatorFirst(json);
            var typeName = reader.IsDBNull(1) ? null : reader.GetString(1);

            Type? type = null;
            if (typeName != null)
                type = Type.GetType(typeName);

            if (type != null)
            {
                var obj = JsonSerializer.Deserialize(json, type, options);
                if (obj != null)
                    yield return obj;
            }
            else
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(json, options);
                yield return doc;
            }
        }
    }

    /// <inheritdoc />
    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => _ioPool.Invoke(async ct => { await SavePartitionObjectsAsyncCore(nodePath, subPath, objects, options, ct).ConfigureAwait(false); return Unit.Default; });

    private async Task SavePartitionObjectsAsyncCore(
        string nodePath,
        string? subPath,
        IReadOnlyCollection<object> objects,
        JsonSerializerOptions options,
        CancellationToken ct = default)
    {
        var partitionKey = GetPartitionStorageKey(nodePath, subPath);

        await DeletePartitionObjectsAsyncCore(nodePath, subPath, ct).ConfigureAwait(false);

        foreach (var obj in objects)
        {
            var id = GetObjectId(obj);
            var json = JsonSerializer.Serialize(obj, obj.GetType(), options);
            var typeName = obj.GetType().AssemblyQualifiedName;

            var poTable = QualifyTable("partition_objects");
            await using var cmd = _dataSource.CreateCommand(
                $"""
                INSERT INTO {poTable} (id, partition_key, type_name, data, last_modified)
                VALUES ($1, $2, $3, $4::jsonb, $5)
                ON CONFLICT (partition_key, id) DO UPDATE SET
                    type_name = EXCLUDED.type_name,
                    data = EXCLUDED.data,
                    last_modified = EXCLUDED.last_modified
                """);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(partitionKey);
            cmd.Parameters.AddWithValue((object?)typeName ?? DBNull.Value);
            cmd.Parameters.AddWithValue(json);
            cmd.Parameters.AddWithValue(DateTimeOffset.UtcNow);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => _ioPool.Invoke(async ct => { await DeletePartitionObjectsAsyncCore(nodePath, subPath, ct).ConfigureAwait(false); return Unit.Default; })
            .Catch<Unit, Exception>(ex => IsUndefinedTable(ex)
                ? Observable.Return(Unit.Default)
                : Observable.Throw<Unit>(ex));

    private async Task DeletePartitionObjectsAsyncCore(
        string nodePath,
        string? subPath = null,
        CancellationToken ct = default)
    {
        var partitionKey = GetPartitionStorageKey(nodePath, subPath);

        var poTable = QualifyTable("partition_objects");
        await using var cmd = _dataSource.CreateCommand(
            $"DELETE FROM {poTable} WHERE partition_key = $1");
        cmd.Parameters.AddWithValue(partitionKey);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => _readPool.Invoke(ct => GetPartitionMaxTimestampAsyncCore(nodePath, subPath, ct))
            .Catch<DateTimeOffset?, Exception>(ex => IsUndefinedTable(ex)
                ? Observable.Return<DateTimeOffset?>(null)
                : Observable.Throw<DateTimeOffset?>(ex));

    private async Task<DateTimeOffset?> GetPartitionMaxTimestampAsyncCore(
        string nodePath,
        string? subPath = null,
        CancellationToken ct = default)
    {
        var partitionKey = GetPartitionStorageKey(nodePath, subPath);

        var poTable = QualifyTable("partition_objects");
        await using var cmd = _dataSource.CreateCommand(
            $"SELECT MAX(last_modified) FROM {poTable} WHERE partition_key = $1");
        cmd.Parameters.AddWithValue(partitionKey);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is DateTimeOffset dto)
            return dto;
        if (result is DateTime dt)
            return new DateTimeOffset(dt, TimeSpan.Zero);
        return null;
    }

    /// <inheritdoc />
    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => _readPool.Invoke(ct => ListPartitionSubPathsAsyncCore(nodePath, ct))
            .Catch<IEnumerable<string>, Exception>(ex => IsUndefinedTable(ex)
                ? Observable.Return(Enumerable.Empty<string>())
                : Observable.Throw<IEnumerable<string>>(ex));

    private async Task<IEnumerable<string>> ListPartitionSubPathsAsyncCore(string nodePath, CancellationToken ct)
    {
        var prefix = NormalizePath(nodePath) + "/";

        var poTable = QualifyTable("partition_objects");
        await using var cmd = _dataSource.CreateCommand(
            $"""
            SELECT DISTINCT
                CASE WHEN position('/' in substring(partition_key from length($1) + 1)) > 0
                     THEN substring(partition_key from length($1) + 1 for position('/' in substring(partition_key from length($1) + 1)) - 1)
                     ELSE substring(partition_key from length($1) + 1)
                END AS sub_path
            FROM {poTable}
            WHERE partition_key LIKE $2
            """);
        cmd.Parameters.AddWithValue(prefix);
        cmd.Parameters.AddWithValue(prefix + "%");

        var subPaths = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var sub = reader.GetString(0);
            if (!string.IsNullOrEmpty(sub))
                subPaths.Add(sub);
        }

        return subPaths;
    }

    #endregion

    #region Query Support

    /// <summary>
    /// Queries nodes using parsed query, translated to PostgreSQL SQL. The reader pump runs in the
    /// per-adapter READ pool (<c>pg-read:{adapter}</c>) via <see cref="ReadPooled{T}"/> — one
    /// pooled slot for the whole enumeration, bounding read fan-out below the connection-pool size.
    /// </summary>
    public IAsyncEnumerable<MeshNode> QueryNodesAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        string? userId = null,
        string? basePath = null,
        string? activityUserId = null,
        IReadOnlyCollection<string>? excludedNodeTypes = null,
        CancellationToken ct = default)
        => ReadPooled(
            c => QueryNodesInnerAsync(query, options, userId, basePath, activityUserId, excludedNodeTypes, c),
            ct);

    private async IAsyncEnumerable<MeshNode> QueryNodesInnerAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        string? userId,
        string? basePath,
        string? activityUserId,
        IReadOnlyCollection<string>? excludedNodeTypes,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var includeContent = SelectorAsksFor(query.Select, "content");
        // One branch per table this query must cover. A primary-table query means "all content",
        // so it unions the CONTENT satellite tables (Source/Test → code) — see ResolveQueryTables.
        var tables = ResolveQueryTables(query, basePath);
        var (sql, parameters) = tables.Count == 1
            ? BuildSingleQuerySql(query, options, userId, basePath, activityUserId, excludedNodeTypes,
                includeContent, tables[0])
            : BuildUnionAcrossTablesSql(query, options, userId, basePath, activityUserId, excludedNodeTypes,
                includeContent, tables);

        if (_logger?.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug) == true)
        {
            _logger.LogDebug("SQL: {Sql}", sql);
            foreach (var (name, value) in parameters)
                _logger.LogDebug("  Param {Name} = {Value}", name, value);
        }

        await using var cmd = _dataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            var p = new NpgsqlParameter(name, value ?? DBNull.Value);
            cmd.Parameters.Add(p);
        }

        // Open the reader in its own try/catch: an absent schema (the router
        // resolves the schema synchronously, so a query can target a schema that
        // was never created) faults at ExecuteReaderAsync with 42P01 — treat that
        // as "no rows". `yield return` can't live inside a catch-bearing try, so
        // the open is separated from the read loop.
        NpgsqlDataReader? reader;
        try
        {
            reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUndefinedTable(ex))
        {
            yield break;
        }

        try
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                yield return ReadMeshNode(reader, options);
            }
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }

    }

    /// <summary>
    /// Multi-query UNION variant of <see cref="QueryNodesAsync(ParsedQuery, JsonSerializerOptions, string?, string?, string?, IReadOnlyCollection{string}?, CancellationToken)"/>.
    /// Generates one SELECT per parsed query (with disjoint <c>@qI_*</c> parameter names),
    /// joins them with <c>UNION ALL</c>, and wraps the result in a
    /// <c>SELECT DISTINCT ON (path)</c> so dedup is path-keyed — not row-keyed
    /// like a plain <c>UNION</c> would be. Two queries that match the same
    /// MeshNode but observe slightly-different metadata (concurrent writer
    /// touching <c>last_modified</c> mid-query) collapse to ONE row, with
    /// the most recently modified version winning the tie-break.
    ///
    /// Single round-trip, server-side dedup. Used by SyncedQueryMeshNodes
    /// via <see cref="MeshQueryRequest.FromQueries"/>.
    ///
    /// <para>Each parsed query is run through the existing single-query SQL
    /// generator + scope-clause logic; the only new work is param-name
    /// disambiguation by query index (single regex pass — see comment below
    /// for why this can't be a sequence of <c>string.Replace</c> calls).</para>
    /// </summary>
    public IAsyncEnumerable<MeshNode> QueryNodesAsync(
        IReadOnlyList<ParsedQuery> queries,
        JsonSerializerOptions options,
        string? userId = null,
        string? basePath = null,
        string? activityUserId = null,
        IReadOnlyCollection<string>? excludedNodeTypes = null,
        CancellationToken ct = default)
    {
        if (queries == null || queries.Count == 0)
            return EmptyAsync<MeshNode>();
        // Single-query: delegate to the single-query overload — itself pooled via ReadPooled. We
        // must NOT wrap this in our own ReadPooled too: that would hold a pg-read slot while the
        // delegate acquires a SECOND, the one same-pool nesting that can deadlock the gate.
        if (queries.Count == 1)
            return QueryNodesAsync(queries[0], options, userId, basePath, activityUserId, excludedNodeTypes, ct);
        // Multi-query UNION: ONE pooled slot for the whole reader enumeration.
        return ReadPooled(
            c => QueryNodesUnionInnerAsync(queries, options, userId, basePath, activityUserId, excludedNodeTypes, c),
            ct);
    }

    private async IAsyncEnumerable<MeshNode> QueryNodesUnionInnerAsync(
        IReadOnlyList<ParsedQuery> queries,
        JsonSerializerOptions options,
        string? userId,
        string? basePath,
        string? activityUserId,
        IReadOnlyCollection<string>? excludedNodeTypes,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var unionedSelects = new List<string>(queries.Count);
        var unionedParams = new Dictionary<string, object>(StringComparer.Ordinal);

        // UNION ALL requires column shape to match across all branches, so the
        // content-skip optimization is all-or-nothing: every query's Select must
        // be set and exclude "content" before we can emit NULL::jsonb instead of
        // n.content. A single query with Select=null (or with "content" listed)
        // forces the full column for the whole union.
        var includeContent = queries.Any(q => SelectorAsksFor(q.Select, "content"));

        for (var qi = 0; qi < queries.Count; qi++)
        {
            // Each query expands to its table branches (primary + content satellites — see
            // ResolveQueryTables), so the multi-query union is as complete as the single-query path.
            var queryTables = ResolveQueryTables(queries[qi], basePath);
            for (var ti = 0; ti < queryTables.Count; ti++)
            {
                var (perSql, perParams) = BuildSingleQuerySql(
                    queries[qi], options, userId, basePath, activityUserId, excludedNodeTypes,
                    includeContent, queryTables[ti]);
                // Disambiguate param names across the union: rename every @<name> token
                // referenced in this per-branch SQL to @qItJ_<name>. We use a single regex
                // pass keyed on the param-name word boundary so we don't mangle adjacent
                // tokens. A naive sequence of `string.Replace` calls is order-dependent:
                // with params @p and @p1, replacing @p first inside an already-rewritten
                // @q0_p1 would mangle it into @q0_q0_p1. Regex.Replace also gates on
                // `perParams.ContainsKey` so we don't accidentally rewrite @-sigils that
                // appear inside string literals or JSONB path expressions.
                var prefix = $"q{qi}t{ti}_";
                var renamedSql = System.Text.RegularExpressions.Regex.Replace(
                    perSql,
                    @"@([A-Za-z_]\w*)",
                    m => perParams.ContainsKey("@" + m.Groups[1].Value)
                        ? "@" + prefix + m.Groups[1].Value
                        : m.Value);
                foreach (var (k, v) in perParams)
                    unionedParams["@" + prefix + k.TrimStart('@')] = v;
                unionedSelects.Add($"({renamedSql})");
            }
        }

        // UNION ALL preserves both branches' rows; DISTINCT ON (namespace, id)
        // collapses duplicates by node identity with last_modified DESC as the
        // tie-breaker (newest version wins). MeshNode.Path = namespace + '/' + id,
        // so (namespace, id) is the path-keyed dedup column set — the SELECTs
        // don't project a literal `path` column. Plain `UNION` would dedup full
        // rows only: two queries observing the same node at slightly different
        // last_modified would BOTH appear, defeating the "one row per path"
        // contract.
        var unionAllInner = string.Join(" UNION ALL ", unionedSelects);
        var sql =
            $"SELECT DISTINCT ON (namespace, id) * FROM ({unionAllInner}) AS unioned " +
            "ORDER BY namespace, id, last_modified DESC";

        if (_logger?.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug) == true)
        {
            _logger.LogDebug("UNION SQL ({Count} queries): {Sql}", queries.Count, sql);
            foreach (var (name, value) in unionedParams)
                _logger.LogDebug("  Param {Name} = {Value}", name, value);
        }

        await using var cmd = _dataSource.CreateCommand(sql);
        foreach (var (name, value) in unionedParams)
            cmd.Parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));

        // Absent schema → 42P01 at open → no rows (see single-query overload).
        NpgsqlDataReader? reader;
        try
        {
            reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUndefinedTable(ex))
        {
            yield break;
        }

        await using (reader)
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                yield return ReadMeshNode(reader, options);
        }
    }

    /// <summary>
    /// Resolves the table a query targets: path-based satellite routing first (a "Source"/"_Thread"
    /// segment in the path), then a nodeType-based redirect when the path resolves to mesh_nodes but
    /// the nodeType filter maps to a satellite (satellite tables are the source of truth).
    /// </summary>
    private (string RawTable, bool SatelliteRedirect) ResolveQueryTable(ParsedQuery query, string? basePath)
    {
        var effectivePath = query.Path ?? basePath;
        string rawTable;
        if (!string.IsNullOrEmpty(effectivePath))
            rawTable = _partitionDefinition?.ResolveTable(effectivePath) ?? "mesh_nodes";
        else
            rawTable = _partitionDefinition?.ResolveTableByNodeType(query.ExtractNodeType()) ?? "mesh_nodes";

        var satelliteRedirect = false;
        if (rawTable == "mesh_nodes" && _partitionDefinition != null)
        {
            var satelliteTable = _partitionDefinition.ResolveTableByNodeType(query.ExtractNodeType());
            if (satelliteTable != null && satelliteTable != "mesh_nodes")
            {
                rawTable = satelliteTable;
                satelliteRedirect = true;
            }
        }
        return (rawTable, satelliteRedirect);
    }

    /// <summary>
    /// Every table branch the query must cover. A query that targets the PRIMARY table means "all
    /// content" — it additionally covers the CONTENT satellite tables (non-underscore segments:
    /// <c>Source</c>/<c>Test</c> → <c>code</c>), whose rows are primary content stored outside
    /// mesh_nodes. Without this a partition-rooted <c>scope:descendants</c> query silently omits
    /// every Code node — observed live as a Space GitSync-exported WITHOUT any of its C# sources.
    /// Metadata satellites (<c>_Thread</c>, <c>_Activity</c>, …) stay excluded: they are
    /// governance data reached via their own segment paths or nodeType filters. Activity/accessed
    /// source queries keep their single JOIN-shaped branch.
    /// </summary>
    private IReadOnlyList<(string RawTable, bool SatelliteRedirect)> ResolveQueryTables(
        ParsedQuery query, string? basePath)
    {
        var primary = ResolveQueryTable(query, basePath);
        if (primary.RawTable != "mesh_nodes" || primary.SatelliteRedirect
            || query.Source != QuerySource.Default
            || _partitionDefinition?.TableMappings is not { } mappings)
            return [primary];

        var contentTables = mappings
            .Where(kv => kv.Key.Length > 0 && kv.Key[0] != '_'
                         && !string.Equals(kv.Value, "mesh_nodes", StringComparison.Ordinal))
            .Select(kv => kv.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (contentTables.Length == 0)
            return [primary];

        var tables = new List<(string, bool)>(1 + contentTables.Length) { primary };
        tables.AddRange(contentTables.Select(t => (t, false)));
        return tables;
    }

    /// <summary>
    /// UNION ALL of the same query against several tables (the primary + the content satellites),
    /// deduped by node identity, with the query's presentation ORDER BY / text-rank / LIMIT
    /// re-applied on the OUTSIDE (each branch's ORDER BY is scoped inside its union arm; the
    /// DISTINCT ON wrap re-orders by identity — same technique as
    /// <see cref="PostgreSqlSqlGenerator.GenerateCrossSchemaSelectQuery"/>).
    /// </summary>
    private (string Sql, Dictionary<string, object> Parameters) BuildUnionAcrossTablesSql(
        ParsedQuery query,
        JsonSerializerOptions options,
        string? userId,
        string? basePath,
        string? activityUserId,
        IReadOnlyCollection<string>? excludedNodeTypes,
        bool includeContent,
        IReadOnlyList<(string RawTable, bool SatelliteRedirect)> tables)
    {
        var selects = new List<string>(tables.Count);
        var parameters = new Dictionary<string, object>(StringComparer.Ordinal);
        for (var ti = 0; ti < tables.Count; ti++)
        {
            var (perSql, perParams) = BuildSingleQuerySql(
                query, options, userId, basePath, activityUserId, excludedNodeTypes, includeContent, tables[ti]);
            // Disambiguate param names across branches (same regex approach as the multi-query
            // union — see QueryNodesUnionInnerAsync for why sequential Replace calls are unsafe).
            var prefix = $"t{ti}_";
            var renamed = System.Text.RegularExpressions.Regex.Replace(
                perSql,
                @"@([A-Za-z_]\w*)",
                m => perParams.ContainsKey("@" + m.Groups[1].Value)
                    ? "@" + prefix + m.Groups[1].Value
                    : m.Value);
            foreach (var (k, v) in perParams)
                parameters["@" + prefix + k.TrimStart('@')] = v;
            selects.Add($"({renamed})");
        }

        var sql = $"SELECT DISTINCT ON (namespace, id) * FROM ({string.Join(" UNION ALL ", selects)}) AS unioned "
                  + "ORDER BY namespace, id, last_modified DESC";

        if (query.OrderBy != null)
        {
            var direction = query.OrderBy.Descending ? "DESC" : "ASC";
            var orderCol = PostgreSqlSqlGenerator.MapOrderByForUnionWrap(query.OrderBy.Property);
            sql = $"SELECT * FROM ({sql}) combined ORDER BY {orderCol} {direction}";
        }
        else if (!string.IsNullOrEmpty(query.TextSearch))
        {
            parameters["@u_scoreText"] = query.TextSearch;
            sql = $"SELECT * FROM ({sql}) combined ORDER BY (CASE " +
                  "WHEN LOWER(COALESCE(name,'')) = LOWER(@u_scoreText) THEN 1000 " +
                  "WHEN LOWER(COALESCE(name,'')) LIKE LOWER(@u_scoreText) || '%' THEN 600 " +
                  "WHEN LOWER(COALESCE(id,'')) LIKE LOWER(@u_scoreText) || '%' THEN 500 " +
                  "WHEN LOWER(COALESCE(name,'')) LIKE '%' || LOWER(@u_scoreText) || '%' THEN 300 " +
                  "WHEN LOWER(COALESCE(id,'')) LIKE '%' || LOWER(@u_scoreText) || '%' THEN 200 " +
                  "WHEN LOWER(COALESCE(description,'')) LIKE '%' || LOWER(@u_scoreText) || '%' THEN 100 " +
                  "ELSE 0 END) DESC, last_modified DESC NULLS LAST";
        }

        if (query.Limit.HasValue)
            sql += $" LIMIT {query.Limit.Value}";

        return (sql, parameters);
    }

    /// <summary>
    /// Builds one table branch's SELECT + scope-clause SQL, returning the (sql, parameters) pair
    /// instead of executing. Shared by the single-query path, the content-satellite union
    /// (<see cref="BuildUnionAcrossTablesSql"/>) and the multi-query UNION path so per-branch SQL
    /// stays bug-compatible everywhere. <paramref name="table"/> selects the branch's table;
    /// null resolves it from the query (<see cref="ResolveQueryTable"/>).
    /// </summary>
    private (string Sql, Dictionary<string, object> Parameters) BuildSingleQuerySql(
        ParsedQuery query,
        JsonSerializerOptions options,
        string? userId,
        string? basePath,
        string? activityUserId,
        IReadOnlyCollection<string>? excludedNodeTypes,
        bool includeContent = true,
        (string RawTable, bool SatelliteRedirect)? table = null)
    {
        var effectivePath = query.Path ?? basePath;
        var (rawTable, satelliteRedirect) = table ?? ResolveQueryTable(query, basePath);
        var tableName = QualifyTable(rawTable);
        var activityTable = QualifyTable(_partitionDefinition?.ResolveTableByNodeType("Activity") ?? "mesh_nodes");
        var userActivityTable = QualifyTable(_partitionDefinition?.ResolveTableByNodeType("UserActivity") ?? "mesh_nodes");

        var generator = new PostgreSqlSqlGenerator { SchemaName = _schemaName };
        var (sql, parameters) = generator.GenerateSelectQuery(query, userId, activityUserId, tableName,
            activityTable, userActivityTable, excludedNodeTypes, includeContent);
        // An EMPTY-but-set path (`namespace:` → Path == "" + Children) is the "root-level rows
        // only" query (the home catalog's partition-roots leg) — it must still push down
        // `n.namespace = ''` rather than silently dropping the scope and returning every depth.
        if (!string.IsNullOrEmpty(effectivePath)
            || (effectivePath is not null && query.Scope == QueryScope.Children)
            || (query.Paths is { Count: > 1 }))
        {
            var (scopeClause, scopeParams) = query.Paths is { Count: > 1 }
                ? generator.GenerateScopeClause(query.Paths, query.Scope, useMainNode: satelliteRedirect, qualifiedTable: tableName)
                : generator.GenerateScopeClause(effectivePath, query.Scope, useMainNode: satelliteRedirect, qualifiedTable: tableName);

            if (!string.IsNullOrEmpty(scopeClause))
            {
                foreach (var (k, v) in scopeParams)
                    parameters[k] = v;

                if (sql.Contains("WHERE"))
                    sql = sql.Replace("WHERE", $"WHERE {scopeClause} AND");
                else if (sql.Contains("ORDER BY"))
                    sql = sql.Replace("ORDER BY", $"WHERE {scopeClause} ORDER BY");
                else
                    sql += $" WHERE {scopeClause}";
            }
        }

        return (sql, parameters);
    }

    /// <summary>
    /// Performs vector similarity search. Reader pump runs in the per-adapter READ pool
    /// (<c>pg-read:{adapter}</c>) via <see cref="ReadPooled{T}"/>.
    /// </summary>
    public IAsyncEnumerable<MeshNode> VectorSearchAsync(
        float[] queryVector,
        JsonSerializerOptions options,
        ParsedQuery? filter = null,
        string? userId = null,
        string? namespacePath = null,
        int topK = 10,
        string? lexicalTerm = null,
        CancellationToken ct = default)
        => ReadPooled(
            c => VectorSearchInnerAsync(queryVector, options, filter, userId, namespacePath, topK, lexicalTerm, c),
            ct);

    private async IAsyncEnumerable<MeshNode> VectorSearchInnerAsync(
        float[] queryVector,
        JsonSerializerOptions options,
        ParsedQuery? filter,
        string? userId,
        string? namespacePath,
        int topK,
        string? lexicalTerm,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Does this schema carry a content index? If so, the vector search UNIONs each file's best
        // chunk in as a synthetic Document row. Probe + cache (instance, TRUE-only) so a partition that
        // later gains content is picked up; the catalog lookup runs in THIS pooled READ leaf.
        var includeContentChunks = await ContentChunksExistAsync(ct).ConfigureAwait(false);

        var generator = new PostgreSqlSqlGenerator { SchemaName = _schemaName };
        var (sql, parameters) = generator.GenerateVectorSearchQuery(
            filter, queryVector, userId, topK, lexicalTerm,
            namespacePath: string.IsNullOrEmpty(namespacePath) ? null : NormalizePath(namespacePath),
            includeContentChunks: includeContentChunks);

        await using var cmd = _dataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            var p = value is Vector v
                ? new NpgsqlParameter(name, v)
                : new NpgsqlParameter(name, value ?? DBNull.Value);
            cmd.Parameters.Add(p);
        }

        // Absent schema → 42P01 at open → no rows (see QueryNodesAsync).
        NpgsqlDataReader? reader;
        try
        {
            reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUndefinedTable(ex))
        {
            yield break;
        }

        await using (reader)
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                yield return ReadMeshNode(reader, options);
            }
        }
    }

    /// <summary>
    /// Whether <c>"{schema}".content_chunks</c> exists, so the vector search can UNION the indexed-content
    /// branch in (each file's best chunk → its <c>Document</c> node, per <c>DocumentPaths.For/Slug</c>).
    /// Cached in the instance <see cref="_contentChunksExists"/> map: a TRUE result is cached permanently
    /// (a content index is not dropped under us); FALSE / absent is NOT cached, so a partition that later
    /// gains content is picked up on the next search. The probe is a single sub-millisecond
    /// <c>to_regclass()</c> catalog lookup; it runs inside the caller's pooled READ leaf. A schemaless
    /// adapter (no per-partition schema) has no per-schema content table — returns false without a probe.
    /// </summary>
    private async Task<bool> ContentChunksExistAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_schemaName))
            return false;
        if (_contentChunksExists.TryGetValue(_schemaName, out var cached))
            return cached;

        bool exists;
        try
        {
            await using var cmd = _dataSource.CreateCommand(
                $"SELECT to_regclass('\"{_schemaName}\".content_chunks') IS NOT NULL");
            exists = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;
        }
        catch (Exception ex) when (IsUndefinedTable(ex))
        {
            exists = false;
        }

        // Only cache the positive — leave a negative uncached so a later content gain is seen.
        if (exists)
            _contentChunksExists[_schemaName] = true;
        return exists;
    }

    /// <summary>
    /// Queries nodes across multiple schemas using a single UNION ALL query.
    /// Much more efficient than per-schema fan-out: one connection, one round-trip.
    /// Reader pump runs in the per-adapter READ pool (<c>pg-read:{adapter}</c>) via
    /// <see cref="ReadPooled{T}"/>.
    /// </summary>
    public IAsyncEnumerable<MeshNode> QueryNodesAcrossSchemasAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        IReadOnlyList<string> schemas,
        string? userId = null,
        CancellationToken ct = default)
        => schemas.Count == 0
            ? EmptyAsync<MeshNode>()
            : ReadPooled(c => QueryNodesAcrossSchemasInnerAsync(query, options, schemas, userId, c), ct);

    private async IAsyncEnumerable<MeshNode> QueryNodesAcrossSchemasInnerAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        IReadOnlyList<string> schemas,
        string? userId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var generator = new PostgreSqlSqlGenerator();
        var (sql, parameters) = generator.GenerateCrossSchemaSelectQuery(query, schemas, userId);

        if (_logger?.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug) == true)
            _logger.LogDebug("Cross-schema SQL ({SchemaCount} schemas): {Sql}", schemas.Count, sql);

        await using var cmd = _dataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            var p = new NpgsqlParameter(name, value ?? DBNull.Value);
            cmd.Parameters.Add(p);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return ReadMeshNode(reader, options);
        }
    }

    #endregion

    private static MeshNode ReadMeshNode(NpgsqlDataReader reader, JsonSerializerOptions options)
    {
        var id = reader.GetString(reader.GetOrdinal("id"));
        var ns = reader.GetString(reader.GetOrdinal("namespace"));

        object? content = null;
        var contentOrd = reader.GetOrdinal("content");
        if (!reader.IsDBNull(contentOrd))
        {
            var json = reader.GetString(contentOrd);
            json = EnsureTypeDiscriminatorFirst(json);
            content = JsonSerializer.Deserialize<object>(json, options);
        }

        return new MeshNode(id, string.IsNullOrEmpty(ns) ? null : ns)
        {
            Name = reader.IsDBNull(reader.GetOrdinal("name")) ? null : reader.GetString(reader.GetOrdinal("name")),
            Description = reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
            NodeType = reader.IsDBNull(reader.GetOrdinal("node_type")) ? null : reader.GetString(reader.GetOrdinal("node_type")),
            Category = reader.IsDBNull(reader.GetOrdinal("category")) ? null : reader.GetString(reader.GetOrdinal("category")),
            Icon = reader.IsDBNull(reader.GetOrdinal("icon")) ? null : reader.GetString(reader.GetOrdinal("icon")),
            Order = reader.IsDBNull(reader.GetOrdinal("display_order")) ? null : reader.GetInt32(reader.GetOrdinal("display_order")),
            LastModified = new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("last_modified")), TimeSpan.Zero),
            CreatedBy = PgMeshNodeReader.ReadNullableString(reader, "created_by"),
            LastModifiedBy = PgMeshNodeReader.ReadNullableString(reader, "last_modified_by"),
            CreatedDate = PgMeshNodeReader.ReadNullableTimestamp(reader, "created_date") ?? default,
            Version = reader.GetInt64(reader.GetOrdinal("version")),
            State = (MeshNodeState)reader.GetInt16(reader.GetOrdinal("state")),
            SyncBehavior = PgMeshNodeReader.ReadSyncBehavior(reader),
            ExcludeFromContext = PgMeshNodeReader.ReadStringArray(reader, "exclude_from_context"),
            Content = content,
            // Mirror the prerendered HTML onto the top-level field, like the FileSystem/Caching
            // adapters do (CachingStorageAdapter.MergeIndexMarkdownAsync). Consumers that render
            // straight from the node — e.g. the Space Overview's BuildBodyContent — read
            // MeshNode.PreRenderedHtml, not Content; without this the welcome page served from PG
            // is blank. It's a transient mirror of MarkdownContent.PrerenderedHtml, not a column.
            PreRenderedHtml = content is MarkdownContent { PrerenderedHtml: { Length: > 0 } html } ? html : null,
            DesiredId = reader.IsDBNull(reader.GetOrdinal("desired_id")) ? null : reader.GetString(reader.GetOrdinal("desired_id")),
            MainNode = reader.IsDBNull(reader.GetOrdinal("main_node"))
                ? (string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}")
                : reader.GetString(reader.GetOrdinal("main_node"))
        };
    }

    /// <summary>
    /// PostgreSQL jsonb reorders keys alphabetically at ALL nesting levels,
    /// which breaks System.Text.Json polymorphic deserialization (requires $type as the first property).
    /// This method recursively moves $type to the front in every object throughout the JSON tree.
    /// </summary>
    private static string EnsureTypeDiscriminatorFirst(string json)
    {
        if (!json.Contains("\"$type\"", StringComparison.Ordinal))
            return json; // No discriminator anywhere

        using var doc = JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            WriteElementWithTypeFirst(writer, doc.RootElement);
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Recursively writes a JsonElement, ensuring $type is the first property in every object.
    /// </summary>
    private static void WriteElementWithTypeFirst(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                // Write $type first if present
                if (element.TryGetProperty("$type", out var typeValue))
                {
                    writer.WritePropertyName("$type");
                    typeValue.WriteTo(writer);
                }
                // Write remaining properties (recursively)
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name == "$type")
                        continue;
                    writer.WritePropertyName(prop.Name);
                    WriteElementWithTypeFirst(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElementWithTypeFirst(writer, item);
                }
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string GetPartitionStorageKey(string nodePath, string? subPath)
    {
        var key = NormalizePath(nodePath);
        if (!string.IsNullOrEmpty(subPath))
            key = $"{key}/{NormalizePath(subPath)}";
        return key;
    }

    private static string GetObjectId(object obj)
    {
        var idProp = obj.GetType().GetProperty("Id") ?? obj.GetType().GetProperty("id");
        var id = idProp?.GetValue(obj)?.ToString();
        return id ?? Guid.NewGuid().ToString();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // DataSource is typically shared and disposed elsewhere
        return ValueTask.CompletedTask;
    }
}
