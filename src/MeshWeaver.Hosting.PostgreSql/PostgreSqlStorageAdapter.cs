using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
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
    // Optional per-adapter READ concurrency gate (Postgres only; null = ungated,
    // e.g. in-memory/tests). Reads acquire a slot; writes stay ungated so they
    // always have pool headroom. See ReadConcurrencyGate.
    private readonly ReadConcurrencyGate? _readGate;
    private readonly Subject<DataChangeNotification> _changes = new();

    public NpgsqlDataSource DataSource => _dataSource;

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

    public PostgreSqlStorageAdapter(
        NpgsqlDataSource dataSource,
        IEmbeddingProvider? embeddingProvider = null,
        PartitionDefinition? partitionDefinition = null,
        Microsoft.Extensions.Logging.ILogger<PostgreSqlStorageAdapter>? logger = null,
        ReadConcurrencyGate? readGate = null)
    {
        _dataSource = dataSource;
        _embeddingProvider = embeddingProvider ?? NullEmbeddingProvider.Instance;
        _partitionDefinition = partitionDefinition;
        _schemaName = partitionDefinition?.Schema;
        _logger = logger;
        _readGate = readGate;
    }

    /// <summary>
    /// Acquire a read-concurrency slot for the duration of a read:
    /// <c>using var _ = await AcquireReadSlotAsync(ct);</c> (safe inside an
    /// <c>async IAsyncEnumerable</c> — the slot releases when the enumerator is
    /// disposed). No-op (immediate empty disposable) when the adapter is ungated
    /// (in-memory / tests). Gated reads cannot drain the connection pool past
    /// <see cref="ReadConcurrencyGate.MaxConcurrency"/>, leaving headroom for writes.
    /// </summary>
    private async Task<IDisposable> AcquireReadSlotAsync(CancellationToken ct)
        => _readGate is null
            ? System.Reactive.Disposables.Disposable.Empty
            : await _readGate.AcquireAsync(ct).ConfigureAwait(false);

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

    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => Observable.FromAsync(ct => ReadAsyncCore(path, options, ct));

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
                $"last_modified, version, state, content, desired_id, main_node " +
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
    public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
        => Observable.Create<MeshNode>(async (observer, ct) =>
        {
            try
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
                        $"last_modified, version, state, content, desired_id, main_node " +
                        $"FROM {table} WHERE namespace = $1 AND id IN ({placeholders})");
                    cmd.Parameters.AddWithValue(ns);
                    foreach (var id in ids)
                        cmd.Parameters.AddWithValue(id);

                    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        observer.OnNext(ReadMeshNode(reader, options));
                    }
                }

                observer.OnCompleted();
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
            }
        });

    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => Observable.FromAsync<MeshNode?>(async ct =>
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
        await using var cmd = _dataSource.CreateCommand(
            $"""
            INSERT INTO {table} (namespace, id, name, description, node_type, category, icon, display_order,
                                    last_modified, version, state, content, desired_id, embedding, main_node)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12::jsonb, $13, $14, $15)
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
                main_node = EXCLUDED.main_node
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

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public IObservable<string> Delete(string path)
        => Observable.FromAsync(async ct =>
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

    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
        => Observable.FromAsync(ct => ListChildPathsAsyncCore(parentPath, ct))
            .Catch<(IEnumerable<string>, IEnumerable<string>), Exception>(ex => IsUndefinedTable(ex)
                ? Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], []))
                : Observable.Throw<(IEnumerable<string>, IEnumerable<string>)>(ex));

    private async Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsyncCore(
        string? parentPath,
        CancellationToken ct)
    {
        var normalizedParent = NormalizePath(parentPath);

        var table = ResolveTable(normalizedParent);
        using var readSlot = await AcquireReadSlotAsync(ct).ConfigureAwait(false);
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

    public IObservable<bool> Exists(string path)
        => Observable.FromAsync(ct => ExistsAsyncCore(path, ct))
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

    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => Observable.FromAsync(ct => FindBestPrefixMatchAsyncCore(fullPath, options, ct))
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
            $"last_modified, version, state, content, desired_id, main_node " +
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
        => Observable.FromAsync(ct => ResolvePathAsyncCore(fullPath, options, ct))
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
                $"last_modified, version, state, content, desired_id, main_node " +
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

    public IObservable<object> GetPartitionObjects(
        string nodePath, string? subPath, JsonSerializerOptions options)
        => Observable.Create<object>(async (observer, ct) =>
        {
            try
            {
                await foreach (var obj in GetPartitionObjectsAsyncCore(nodePath, subPath, options, ct).ConfigureAwait(false))
                    observer.OnNext(obj);
                observer.OnCompleted();
            }
            catch (Exception ex) when (IsUndefinedTable(ex))
            {
                // Absent schema (router resolved synchronously, schema never
                // created) → nothing to read. Complete empty, don't fault.
                observer.OnCompleted();
            }
        });

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

    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => Observable.FromAsync(async ct => { await SavePartitionObjectsAsyncCore(nodePath, subPath, objects, options, ct).ConfigureAwait(false); return Unit.Default; });

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

    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => Observable.FromAsync(async ct => { await DeletePartitionObjectsAsyncCore(nodePath, subPath, ct).ConfigureAwait(false); return Unit.Default; })
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

    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => Observable.FromAsync(ct => GetPartitionMaxTimestampAsyncCore(nodePath, subPath, ct))
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

    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => Observable.FromAsync(ct => ListPartitionSubPathsAsyncCore(nodePath, ct))
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
    /// Queries nodes using parsed query, translated to PostgreSQL SQL.
    /// </summary>
    public async IAsyncEnumerable<MeshNode> QueryNodesAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        string? userId = null,
        string? basePath = null,
        string? activityUserId = null,
        IReadOnlyCollection<string>? excludedNodeTypes = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Resolve the target table based on the query path or nodeType and partition definition.
        // For satellite paths like "User/alice/_Thread", this routes to the "threads" table.
        // For nodeType-only queries like "nodeType:Thread", resolves via nodeType-to-suffix mapping.
        // When the path doesn't contain a satellite suffix (e.g., routing fan-out with DefaultPath="User")
        // but the query has a nodeType filter for a satellite type, prefer the nodeType-based resolution.
        // NOTE: We query BOTH the satellite table and mesh_nodes (via UNION ALL) because existing data
        // may be in mesh_nodes from before satellite table routing was enabled on the write path.
        var effectivePath = query.Path ?? basePath;
        string rawTable;
        if (!string.IsNullOrEmpty(effectivePath))
        {
            rawTable = _partitionDefinition?.ResolveTable(effectivePath) ?? "mesh_nodes";
        }
        else
        {
            rawTable = _partitionDefinition?.ResolveTableByNodeType(query.ExtractNodeType()) ?? "mesh_nodes";
        }

        // When the path resolves to mesh_nodes but nodeType maps to a satellite table,
        // use the satellite table instead. Satellite tables are the source of truth.
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
        var tableName = QualifyTable(rawTable);
        // Resolve satellite table names for source:activity and source:accessed JOINs.
        // Non-partitioned setups store everything in mesh_nodes (no satellite tables).
        var activityTable = QualifyTable(_partitionDefinition?.ResolveTableByNodeType("Activity") ?? "mesh_nodes");
        var userActivityTable = QualifyTable(_partitionDefinition?.ResolveTableByNodeType("UserActivity") ?? "mesh_nodes");

        // Create a fresh generator per call — the generator has mutable state (_paramIndex, _parameters)
        // and is NOT thread-safe. Concurrent fan-out queries share the same adapter.
        var generator = new PostgreSqlSqlGenerator { SchemaName = _schemaName };
        var includeContent = SelectorAsksFor(query.Select, "content");
        var (sql, parameters) = generator.GenerateSelectQuery(query, userId, activityUserId, tableName,
            activityTable, userActivityTable, excludedNodeTypes, includeContent);
        if (!string.IsNullOrEmpty(effectivePath) || (query.Paths is { Count: > 1 }))
        {
            // Multi-value `path:a|b|c` push-down → `n.path IN (...)`. Routing-layer
            // "longest-matching-prefix" lookups go through this path. Single-path
            // queries use the existing scope-clause generator unchanged.
            var (scopeClause, scopeParams) = query.Paths is { Count: > 1 }
                ? generator.GenerateScopeClause(query.Paths, query.Scope, useMainNode: satelliteRedirect)
                : generator.GenerateScopeClause(effectivePath, query.Scope, useMainNode: satelliteRedirect);

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

        if (_logger?.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug) == true)
        {
            _logger.LogDebug("SQL: {Sql}", sql);
            foreach (var (name, value) in parameters)
                _logger.LogDebug("  Param {Name} = {Value}", name, value);
        }

        using var readSlot = await AcquireReadSlotAsync(ct).ConfigureAwait(false);
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
    public async IAsyncEnumerable<MeshNode> QueryNodesAsync(
        IReadOnlyList<ParsedQuery> queries,
        JsonSerializerOptions options,
        string? userId = null,
        string? basePath = null,
        string? activityUserId = null,
        IReadOnlyCollection<string>? excludedNodeTypes = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (queries == null || queries.Count == 0) yield break;
        if (queries.Count == 1)
        {
            await foreach (var node in QueryNodesAsync(
                queries[0], options, userId, basePath, activityUserId, excludedNodeTypes, ct).ConfigureAwait(false))
                yield return node;
            yield break;
        }

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
            var (perSql, perParams) = BuildSingleQuerySql(
                queries[qi], options, userId, basePath, activityUserId, excludedNodeTypes, includeContent);
            // Disambiguate param names across the union: rename every @<name> token
            // referenced in this per-query SQL to @qI_<name>. We use a single regex
            // pass keyed on the param-name word boundary so we don't mangle adjacent
            // tokens. A naive sequence of `string.Replace` calls is order-dependent:
            // with params @p and @p1, replacing @p first inside an already-rewritten
            // @q0_p1 would mangle it into @q0_q0_p1. Regex.Replace also gates on
            // `perParams.ContainsKey` so we don't accidentally rewrite @-sigils that
            // appear inside string literals or JSONB path expressions.
            var prefix = $"q{qi}_";
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

        using var readSlot = await AcquireReadSlotAsync(ct).ConfigureAwait(false);
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
    /// Builds the same SELECT + scope-clause SQL that the single-query
    /// <see cref="QueryNodesAsync(ParsedQuery, JsonSerializerOptions, string?, string?, string?, IReadOnlyCollection{string}?, CancellationToken)"/>
    /// path emits, but returns the (sql, parameters) pair instead of executing.
    /// Shared by the multi-query UNION path so per-query SQL stays
    /// bug-compatible with the single-query path.
    /// </summary>
    private (string Sql, Dictionary<string, object> Parameters) BuildSingleQuerySql(
        ParsedQuery query,
        JsonSerializerOptions options,
        string? userId,
        string? basePath,
        string? activityUserId,
        IReadOnlyCollection<string>? excludedNodeTypes,
        bool includeContent = true)
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
        var tableName = QualifyTable(rawTable);
        var activityTable = QualifyTable(_partitionDefinition?.ResolveTableByNodeType("Activity") ?? "mesh_nodes");
        var userActivityTable = QualifyTable(_partitionDefinition?.ResolveTableByNodeType("UserActivity") ?? "mesh_nodes");

        var generator = new PostgreSqlSqlGenerator { SchemaName = _schemaName };
        var (sql, parameters) = generator.GenerateSelectQuery(query, userId, activityUserId, tableName,
            activityTable, userActivityTable, excludedNodeTypes, includeContent);
        if (!string.IsNullOrEmpty(effectivePath) || (query.Paths is { Count: > 1 }))
        {
            var (scopeClause, scopeParams) = query.Paths is { Count: > 1 }
                ? generator.GenerateScopeClause(query.Paths, query.Scope, useMainNode: satelliteRedirect)
                : generator.GenerateScopeClause(effectivePath, query.Scope, useMainNode: satelliteRedirect);

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
    /// Performs vector similarity search.
    /// </summary>
    public async IAsyncEnumerable<MeshNode> VectorSearchAsync(
        float[] queryVector,
        JsonSerializerOptions options,
        ParsedQuery? filter = null,
        string? userId = null,
        string? namespacePath = null,
        int topK = 10,
        string? lexicalTerm = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var generator = new PostgreSqlSqlGenerator { SchemaName = _schemaName };
        var (sql, parameters) = generator.GenerateVectorSearchQuery(
            filter, queryVector, userId, topK, lexicalTerm);

        if (!string.IsNullOrEmpty(namespacePath))
        {
            var normalizedPath = NormalizePath(namespacePath);
            parameters["@nsPrefix"] = $"{normalizedPath}/";

            if (sql.Contains("WHERE"))
                sql = sql.Replace("WHERE", "WHERE n.path LIKE @nsPrefix || '%' AND");
            else
                sql = sql.Replace("ORDER BY", "WHERE n.path LIKE @nsPrefix || '%' ORDER BY");
        }

        using var readSlot = await AcquireReadSlotAsync(ct).ConfigureAwait(false);
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
    /// Queries nodes across multiple schemas using a single UNION ALL query.
    /// Much more efficient than per-schema fan-out: one connection, one round-trip.
    /// </summary>
    public async IAsyncEnumerable<MeshNode> QueryNodesAcrossSchemasAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        IReadOnlyList<string> schemas,
        string? userId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (schemas.Count == 0) yield break;

        var generator = new PostgreSqlSqlGenerator();
        var (sql, parameters) = generator.GenerateCrossSchemaSelectQuery(query, schemas, userId);

        if (_logger?.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug) == true)
            _logger.LogDebug("Cross-schema SQL ({SchemaCount} schemas): {Sql}", schemas.Count, sql);

        using var readSlot = await AcquireReadSlotAsync(ct).ConfigureAwait(false);
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
            Content = content,
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

    public ValueTask DisposeAsync()
    {
        // DataSource is typically shared and disposed elsewhere
        return ValueTask.CompletedTask;
    }
}
