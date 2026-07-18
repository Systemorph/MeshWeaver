using System.Reactive;
using MeshWeaver.Hosting.Embeddings;
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
    // The pg:{adapter} write I/O pool — every WRITE / provisioning DB round-trip runs inside it
    // (Invoke), never a bare Observable.FromAsync. Unbounded fallback when no registry is wired.
    private readonly IIoPool _ioPool;
    private readonly Subject<DataChangeNotification> _changes = new();

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
    public IObservable<DataChangeNotification> Changes => _changes.AsObservable();

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
        Microsoft.Extensions.Logging.ILogger<PostgreSqlStorageAdapter>? logger = null,
        IIoPool? readPool = null,
        IIoPool? ioPool = null)
    {
        _dataSource = dataSource;
        _embeddingProvider = embeddingProvider ?? NullEmbeddingProvider.Instance;
        _partitionDefinition = partitionDefinition;
        _schemaName = partitionDefinition?.Schema;
        _logger = logger;
        _readPool = readPool ?? IoPool.Unbounded;
        _ioPool = ioPool ?? IoPool.Unbounded;
    }

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

    /// <inheritdoc />
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => _ioPool.Invoke(ct => ReadAsyncCore(path, options, ct));

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
                $"last_modified, version, state, content, desired_id, main_node, {SyncBehaviorCol(table)} " +
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
        => _ioPool.InvokeStream(ct => ReadManyAsyncCore(paths, options, ct));

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
                $"last_modified, version, state, content, desired_id, main_node, {SyncBehaviorCol(table)} " +
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
            await WriteAsyncCore(node, options, ct).ConfigureAwait(false);
            // Fire the in-process Changes feed so same-process synced-query
            // subscribers re-emit without waiting for the PG NOTIFY round-trip.
            // PostgreSqlChangeListener still publishes for cross-process; the
            // listener's pg_notify dedup (PostgreSqlExtensions LISTEN/NOTIFY
            // dedup) makes the double-fire idempotent.
            try
            {
                _changes.OnNext(DataChangeNotification.Updated(
                    string.IsNullOrEmpty(node.Path) ? node.Id : node.Path, node));
            }
            catch { /* never throw — change feed is best-effort */ }
            return node;
        });

    private async Task WriteAsyncCore(MeshNode node, JsonSerializerOptions options, CancellationToken ct)
    {
        var ns = node.Namespace ?? "";

        // Generate embedding
        var embeddingText = string.Join(" ",
            new[] { node.Name, node.NodeType }
                .Where(s => !string.IsNullOrEmpty(s)));
        var embeddingVector = await _embeddingProvider.GenerateEmbeddingAsync(embeddingText).ConfigureAwait(false);

        var contentJson = node.Content != null
            ? JsonSerializer.Serialize(node.Content, node.Content.GetType(), options)
            : null;

        var table = ResolveTable(node.Path, node.NodeType);
        // sync_behavior lives only on mesh_nodes (the sole decouplable table); satellite
        // tables don't carry it, so write/update it only when targeting mesh_nodes.
        var writeSync = table.Contains("mesh_nodes", StringComparison.OrdinalIgnoreCase);
        var syncInsertCol = writeSync ? ", sync_behavior" : "";
        var syncInsertVal = writeSync ? ", $16" : "";
        var syncUpdate = writeSync ? ",\n                sync_behavior = EXCLUDED.sync_behavior" : "";
        await using var cmd = _dataSource.CreateCommand(
            $"""
            INSERT INTO {table} (namespace, id, name, description, node_type, category, icon, display_order,
                                    last_modified, version, state, content, desired_id, embedding, main_node{syncInsertCol})
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12::jsonb, $13, $14, $15{syncInsertVal})
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
                main_node = EXCLUDED.main_node{syncUpdate}
            """);

        cmd.Parameters.AddWithValue(ns);
        cmd.Parameters.AddWithValue(node.Id);
        cmd.Parameters.AddWithValue((object?)node.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)node.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)node.NodeType ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)node.Category ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)node.Icon ?? DBNull.Value);
        cmd.Parameters.AddWithValue(node.Order.HasValue ? node.Order.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(node.LastModified == default ? DateTimeOffset.UtcNow : node.LastModified);
        cmd.Parameters.AddWithValue(node.Version);
        cmd.Parameters.AddWithValue((short)node.State);
        cmd.Parameters.AddWithValue((object?)contentJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)node.DesiredId ?? DBNull.Value);

        if (embeddingVector != null)
            cmd.Parameters.AddWithValue(new Vector(embeddingVector));
        else
            cmd.Parameters.AddWithValue(DBNull.Value);

        cmd.Parameters.AddWithValue(node.MainNode);

        // $16 — only bound when the target is mesh_nodes (see writeSync above).
        if (writeSync)
            cmd.Parameters.AddWithValue((short)node.SyncBehavior);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IObservable<string> Delete(string path)
        => _ioPool.Invoke(async ct =>
        {
            await DeleteAsyncCore(path, ct).ConfigureAwait(false);
            try { _changes.OnNext(DataChangeNotification.Deleted(path)); }
            catch { /* never throw — change feed is best-effort */ }
            return path;
        });

    private async Task DeleteAsyncCore(string path, CancellationToken ct)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath))
            return;

        var (ns, id) = SplitPath(normalizedPath);

        var table = ResolveTable(normalizedPath);
        await using var cmd = _dataSource.CreateCommand(
            $"DELETE FROM {table} WHERE namespace = $1 AND id = $2");
        cmd.Parameters.AddWithValue(ns);
        cmd.Parameters.AddWithValue(id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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

    /// <inheritdoc />
    public IObservable<bool> Exists(string path)
        => _ioPool.Invoke(ct => ExistsAsyncCore(path, ct))
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
        => _ioPool.Invoke(ct => FindBestPrefixMatchAsyncCore(fullPath, options, ct))
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
            $"last_modified, version, state, content, desired_id, main_node, {SyncBehaviorCol(table)} " +
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
        => _ioPool.Invoke(ct => ResolvePathAsyncCore(fullPath, options, ct))
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
                $"last_modified, version, state, content, desired_id, main_node, {SyncBehaviorCol(qualified)} " +
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
        => _ioPool.InvokeStream(ct => GetPartitionObjectsAsyncCore(nodePath, subPath, options, ct))
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
        => _ioPool.Invoke(ct => GetPartitionMaxTimestampAsyncCore(nodePath, subPath, ct))
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
        => _ioPool.Invoke(ct => ListPartitionSubPathsAsyncCore(nodePath, ct))
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
        if (!string.IsNullOrEmpty(effectivePath) || (query.Paths is { Count: > 1 }))
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
            Version = reader.GetInt64(reader.GetOrdinal("version")),
            State = (MeshNodeState)reader.GetInt16(reader.GetOrdinal("state")),
            SyncBehavior = PgMeshNodeReader.ReadSyncBehavior(reader),
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
