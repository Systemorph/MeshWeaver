using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Hosting.Embeddings;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Snowflake.Data.Client;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// Snowflake implementation of <see cref="IStorageAdapter"/> — the member-for-member port of
/// <c>PostgreSqlStorageAdapter</c>. Stores MeshNodes in <c>mesh_nodes</c> and partition objects
/// in <c>partition_objects</c>; when a <see cref="PartitionDefinition"/> with TableMappings is
/// provided, satellite nodes route to their dedicated tables by path/nodeType, exactly like PG.
///
/// <para><b>Dialect deltas vs PG</b> (everything else mirrors the PG adapter 1:1):
/// <list type="bullet">
///   <item>Identifiers are double-quoted lowercase via <see cref="SnowflakeIdentifiers"/>
///     (Snowflake uppercases unquoted names); parameters are <c>:name</c> via
///     <see cref="SnowflakeConnectionSource.AddParam"/>.</item>
///   <item><c>jsonb</c> → VARIANT written through <c>TRY_PARSE_JSON(:content)</c> (the ONE
///     JSON-write shape used everywhere in this adapter — NULL-safe, and the payload is
///     serializer-produced so it is always well-formed) and read back as JSON text through
///     <see cref="SnowflakeMeshNodeReader"/>.</item>
///   <item><c>ON CONFLICT</c> → <c>MERGE</c>, falling back to DELETE-by-key + INSERT run
///     sequentially on the same connection when the endpoint (LocalStack emulator) reports
///     <see cref="SnowflakeCapabilities.SupportsMerge"/> = false. Capabilities are read from
///     <see cref="SnowflakeCapabilityHolder.Current"/> lazily per operation, never cached at
///     construction time (the probe may run after this adapter is built).</item>
///   <item><c>path</c> is a REAL column (PG generates it): the write path maintains
///     <c>path = namespace == "" ? id : namespace + "/" + id</c> on every insert/update.</item>
///   <item>PG's 42P01 undefined-table read tolerance → <see cref="IsUndefinedObject"/>
///     (Snowflake error 2003 / "does not exist or not authorized").</item>
///   <item><b>Trigger replacement</b>: PG performs history copies, auth mirroring and the
///     permission projection in DB triggers. Snowflake has none, so the SAME behaviors run in
///     the C# write/delete leaves, post-commit, each individually guarded so a projection
///     failure never fails the node write (see <see cref="WriteAsyncCore"/>).</item>
/// </list></para>
/// </summary>
public class SnowflakeStorageAdapter : IScopedQueryStorageAdapter, IAsyncDisposable
{
    /// <summary>
    /// Node types mirrored into the central <c>"auth"."mesh_nodes"</c> lookup table — THE list
    /// from PG's <c>public.mirror_access_object_to_auth_schema()</c> trigger function
    /// (<c>'User','Group','Role','VUser','ApiToken','Space'</c>). PG's history shows what happens
    /// when the list drifts (the lost-<c>Space</c> production bug): keep this constant in lock
    /// step with the PG function when extending either backend.
    /// </summary>
    internal static readonly ImmutableHashSet<string> MirroredNodeTypes =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "User", "Group", "Role", "VUser", "ApiToken", "Space");

    /// <summary>
    /// Node types whose <c>mesh_nodes</c> writes/deletes re-run the permission projection.
    /// PG's <c>trg_access_changed</c> trigger fires only on the <c>access</c> satellite table;
    /// <c>GroupMembership</c>/<c>Role</c>/<c>PartitionAccessPolicy</c> rows are projection
    /// INPUTS read by <c>rebuild_user_effective_permissions()</c>, and on PG their edits are
    /// healed by the boot-time reconcile. Snowflake has no triggers and no boot-time plpgsql, so
    /// this adapter rebuilds on those writes too — a deliberate superset of the PG trigger
    /// condition that keeps the projection current without a watchdog.
    /// </summary>
    private static readonly ImmutableHashSet<string> ProjectionNodeTypes =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "GroupMembership", "Role", "PartitionAccessPolicy");

    /// <summary>The central auth-mirror schema name (mirror target, never a mirror source).</summary>
    private const string AuthSchemaName = "auth";

    private readonly SnowflakeConnectionSource _source;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly PartitionDefinition? _partitionDefinition;
    private readonly string? _schemaName;
    private readonly ILogger? _logger;
    // Per-adapter READ pool (the sf-read:{adapter} IIoPool). Bounds concurrent READS so a
    // synced-query read fan-out storm can't starve writes — the same split the PG adapter uses.
    // Unbounded fallback when no registry is wired (in-memory / tests).
    private readonly IIoPool _readPool;
    // The sf:{adapter} write I/O pool — every WRITE / provisioning DB round-trip runs inside it
    // (Invoke), never a bare Observable.FromAsync. Unbounded fallback when no registry is wired.
    private readonly IIoPool _ioPool;
    private readonly SnowflakeCapabilityHolder _capabilities;
    private readonly SnowflakeStorageOptions _options;
    private readonly Subject<DataChangeNotification> _changes = new();

    // Per-adapter cache of "does {schema}.content_chunks exist?" — drives whether the vector
    // search UNIONs the indexed-content branch. INSTANCE field (never static — the
    // no-static-state rule): its lifetime is this adapter's. Only TRUE is cached (permanently —
    // a content index is never dropped under us); FALSE/missing is NOT cached so a partition
    // that LATER gains content is picked up on the next search.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _contentChunksExists =
        new(StringComparer.Ordinal);

    /// <summary>The connection source this adapter reads and writes through.</summary>
    public SnowflakeConnectionSource Source => _source;

    /// <inheritdoc />
    /// <remarks>
    /// In-process change feed: <see cref="Write"/>/<see cref="Delete"/> publish here post-commit
    /// so same-process synced-query subscribers re-emit immediately. Cross-process changes arrive
    /// via the <see cref="SnowflakeChangeFeedPoller"/> (Snowflake has no LISTEN/NOTIFY), which is
    /// attached to <see cref="ChangeObserver"/> by the hosting wiring.
    /// </remarks>
    public IObservable<DataChangeNotification> Changes => _changes.AsObservable();

    /// <summary>
    /// Internal hook for the <see cref="SnowflakeChangeFeedPoller"/> to push foreign-silo
    /// change events into the adapter's <see cref="Changes"/> feed — the Snowflake twin of
    /// PG's <c>PostgreSqlChangeListener</c> hook.
    /// </summary>
    internal IObserver<DataChangeNotification> ChangeObserver => _changes;

    /// <summary>
    /// Initializes the storage adapter over a connection source, optionally scoped to a single
    /// partition schema. Signature is PINNED — the routing/provider layer constructs adapters
    /// with exactly this shape.
    /// </summary>
    /// <param name="source">The connection source used for all reads and writes.</param>
    /// <param name="embeddingProvider">Optional embedding provider used to populate the vector column on write; defaults to a no-op provider.</param>
    /// <param name="partitionDefinition">Optional partition definition; when set, table references are scoped to its schema.</param>
    /// <param name="logger">Optional logger for read/write diagnostics.</param>
    /// <param name="readPool">Optional per-adapter read pool bounding concurrent reads.</param>
    /// <param name="ioPool">Optional per-adapter write pool (capped at one connection) serializing writes.</param>
    /// <param name="capabilities">Probed endpoint capabilities; a fresh all-on holder when null.</param>
    /// <param name="options">Storage options (vector dimensions/enablement, central schema); defaults when null.</param>
    public SnowflakeStorageAdapter(
        SnowflakeConnectionSource source,
        IEmbeddingProvider? embeddingProvider = null,
        PartitionDefinition? partitionDefinition = null,
        ILogger<SnowflakeStorageAdapter>? logger = null,
        IIoPool? readPool = null,
        IIoPool? ioPool = null,
        SnowflakeCapabilityHolder? capabilities = null,
        SnowflakeStorageOptions? options = null)
    {
        _source = source;
        _embeddingProvider = embeddingProvider ?? NullEmbeddingProvider.Instance;
        _partitionDefinition = partitionDefinition;
        _schemaName = partitionDefinition?.Schema;
        _logger = logger;
        _readPool = readPool ?? IoPool.Unbounded;
        _ioPool = ioPool ?? IoPool.Unbounded;
        _capabilities = capabilities ?? new SnowflakeCapabilityHolder();
        _options = options ?? new SnowflakeStorageOptions();
    }

    /// <summary>
    /// Pumps a read <see cref="IAsyncEnumerable{T}"/> through the per-adapter READ pool,
    /// bounding concurrent reads so a fan-out storm can't starve writes. The pool's
    /// <see cref="IIoPool.InvokeStream{T}"/> holds ONE slot for the whole enumeration (acquired
    /// off the caller's scheduler, released when the enumeration completes / errors / is
    /// cancelled). The observable is bridged back to <see cref="IAsyncEnumerable{T}"/> via an
    /// unbounded channel so callers' existing <c>await foreach</c> shape is unchanged — the
    /// same relay the PG adapter uses.
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
    /// Returns a schema-qualified, double-quoted table reference for use in SQL.
    /// When a schema is set, returns <c>"schema"."table"</c>; otherwise just <c>"table"</c>.
    /// </summary>
    private string QualifyTable(string table)
        => string.IsNullOrEmpty(_schemaName)
            ? SnowflakeIdentifiers.Quote(table)
            : SnowflakeIdentifiers.Qualify(_schemaName, table);

    /// <summary>
    /// Resolves the RAW (unqualified) table name for a given path and optional nodeType —
    /// path-based satellite routing first, then nodeType-based routing, exactly like PG.
    /// The raw name also drives the trigger-replacement decisions (<c>mesh_nodes</c> vs the
    /// <c>access</c> satellite), which is why it is surfaced separately from the qualified form.
    /// </summary>
    private string ResolveRawTable(string path, string? nodeType = null)
    {
        if (_partitionDefinition == null)
            return "mesh_nodes";
        var table = _partitionDefinition.ResolveTable(path);
        if (table == "mesh_nodes" && !string.IsNullOrEmpty(nodeType))
            table = _partitionDefinition.ResolveTableByNodeType(nodeType);
        return table;
    }

    /// <summary>Qualified variant of <see cref="ResolveRawTable"/> — the PG <c>ResolveTable</c> twin.</summary>
    private string ResolveTable(string path, string? nodeType = null)
        => QualifyTable(ResolveRawTable(path, nodeType));

    /// <summary>
    /// Projection for the node-level sync claim: the real column when reading <c>mesh_nodes</c>
    /// (the only decouplable table), else the Include (0) default so single-table reads and
    /// UNION branches over satellite tables — which don't carry the column — keep a stable shape.
    /// </summary>
    private static string SyncBehaviorCol(string qualifiedTable) =>
        qualifiedTable.Contains("mesh_nodes", StringComparison.OrdinalIgnoreCase)
            ? "\"sync_behavior\""
            : "0 AS \"sync_behavior\"";

    /// <summary>The shared node projection column list (quoted lowercase), sans sync_behavior.</summary>
    private const string NodeColumns =
        "\"id\", \"namespace\", \"name\", \"description\", \"node_type\", \"category\", \"icon\", \"display_order\", " +
        "\"last_modified\", \"version\", \"state\", \"content\", \"desired_id\", \"main_node\"";

    /// <summary>Trims leading/trailing slashes; null → empty (the PG <c>NormalizePath</c>).</summary>
    private static string NormalizePath(string? path) =>
        path?.Trim('/') ?? "";

    /// <summary>Splits a normalized path into (namespace, id) on the last slash.</summary>
    private static (string Namespace, string Id) SplitPath(string normalizedPath)
    {
        var lastSlash = normalizedPath.LastIndexOf('/');
        var ns = lastSlash > 0 ? normalizedPath[..lastSlash] : "";
        var id = lastSlash > 0 ? normalizedPath[(lastSlash + 1)..] : normalizedPath;
        return (ns, id);
    }

    /// <summary>
    /// The generation semantics of the REAL <c>path</c> column (PG declares it
    /// <c>GENERATED ALWAYS AS (...) STORED</c>; Snowflake has no stored generated columns).
    /// </summary>
    private static string ComputePath(string ns, string id)
        => ns.Length == 0 ? id : ns + "/" + id;

    /// <summary>
    /// null Select → caller didn't project → fetch all columns (existing behavior).
    /// non-null Select → caller opted into projection → fetch column only if listed.
    /// </summary>
    private static bool SelectorAsksFor(IReadOnlyList<string>? select, string column)
        => select is null || select.Any(s => s.Equals(column, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the exception is Snowflake's "object does not exist" error — the twin of PG's
    /// <c>42P01</c> undefined_table. The partition router resolves a path's first segment to a
    /// schema <i>synchronously</i> (no existence probe), so a READ can legitimately target a
    /// schema that was never created — there's simply nothing to read; every read path swallows
    /// this and returns the empty result instead of faulting. Matching is defensive: the vendor
    /// error code 2003 ("SQL compilation error: Object ... does not exist or not authorized")
    /// when surfaced on the driver exception's <c>ErrorCode</c>, plus the message text — the
    /// LocalStack emulator does not always populate the code. Centralized here as the ONE
    /// helper used by every read path (and by <see cref="SnowflakeAccessProjection"/> /
    /// <see cref="SnowflakeAccessControl"/> in this assembly).
    /// </summary>
    internal static bool IsUndefinedObject(Exception ex)
        => ex is SnowflakeDbException sf
           && (sf.ErrorCode == 2003
               || sf.Message.Contains("does not exist or not authorized", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Serializes an embedding vector as an InvariantCulture JSON array string —
    /// the bind value for <c>PARSE_JSON(:embedding)::VECTOR(FLOAT, N)</c>.
    /// </summary>
    private static string ToJsonFloatArray(float[] vector)
        => "[" + string.Join(",", vector.Select(f => f.ToString(CultureInfo.InvariantCulture))) + "]";

    /// <summary>
    /// Binds one generator-produced parameter onto <paramref name="command"/>. The SQL generator
    /// returns a <c>name → value</c> map whose keys may carry a <c>@</c>/<c>:</c> sigil (the PG
    /// generator convention this one mirrors); the sigil is stripped and the <see cref="DbType"/>
    /// inferred from the CLR value. <see cref="DateTimeOffset"/> binds its
    /// <see cref="DateTimeOffset.UtcDateTime"/> (TIMESTAMP_NTZ stores UTC); <c>float[]</c> binds
    /// the InvariantCulture JSON array string (the generator emits the
    /// <c>PARSE_JSON(...)::VECTOR</c> cast around it).
    /// </summary>
    private static void BindGeneratedParameter(DbCommand command, string name, object? value)
    {
        var bare = name.TrimStart('@', ':');
        switch (value)
        {
            case null or DBNull:
                SnowflakeConnectionSource.AddParam(command, bare, DBNull.Value, DbType.String);
                break;
            case string s:
                SnowflakeConnectionSource.AddParam(command, bare, s, DbType.String);
                break;
            case bool b:
                SnowflakeConnectionSource.AddParam(command, bare, b, DbType.Boolean);
                break;
            case short i16:
                SnowflakeConnectionSource.AddParam(command, bare, i16, DbType.Int16);
                break;
            case int i32:
                SnowflakeConnectionSource.AddParam(command, bare, i32, DbType.Int32);
                break;
            case long i64:
                SnowflakeConnectionSource.AddParam(command, bare, i64, DbType.Int64);
                break;
            case float f:
                SnowflakeConnectionSource.AddParam(command, bare, (double)f, DbType.Double);
                break;
            case double d:
                SnowflakeConnectionSource.AddParam(command, bare, d, DbType.Double);
                break;
            case decimal m:
                SnowflakeConnectionSource.AddParam(command, bare, m, DbType.Decimal);
                break;
            case DateTimeOffset dto:
                SnowflakeConnectionSource.AddParam(command, bare, dto.UtcDateTime, DbType.DateTime);
                break;
            case DateTime dt:
                SnowflakeConnectionSource.AddParam(command, bare, dt, DbType.DateTime);
                break;
            case float[] vec:
                SnowflakeConnectionSource.AddParam(command, bare, ToJsonFloatArray(vec), DbType.String);
                break;
            case Guid g:
                SnowflakeConnectionSource.AddParam(command, bare, g.ToString(), DbType.String);
                break;
            default:
                SnowflakeConnectionSource.AddParam(
                    command, bare, Convert.ToString(value, CultureInfo.InvariantCulture), DbType.String);
                break;
        }
    }

    /// <inheritdoc />
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => _ioPool.Invoke(ct => ReadAsyncCore(path, options, ct));

    /// <summary>Single-node read leaf — mirrors PG's <c>ReadAsyncCore</c> including the undefined-table tolerance.</summary>
    private async Task<MeshNode?> ReadAsyncCore(string path, JsonSerializerOptions options, CancellationToken ct)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath))
            return null;

        var (ns, id) = SplitPath(normalizedPath);

        var table = ResolveTable(normalizedPath);
        try
        {
            await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"SELECT {NodeColumns}, {SyncBehaviorCol(table)} " +
                $"FROM {table} WHERE \"namespace\" = :ns AND \"id\" = :id";
            SnowflakeConnectionSource.AddParam(cmd, "ns", ns, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "id", id, DbType.String);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            return SnowflakeMeshNodeReader.ReadMeshNode(reader, options, _logger);
        }
        catch (Exception ex) when (IsUndefinedObject(ex))
        {
            // Half-provisioned partition: the schema exists (routed here) but its mesh_nodes /
            // satellite table was never created. There is no node to read → null, NOT an error —
            // the same guard that fixed PG's prod Systemorph-space bug (2026-06-02).
            _logger?.LogDebug(ex,
                "Read on {Table} for '{Path}' hit undefined-object; treating as no node " +
                "(bare/half-provisioned partition).",
                table, normalizedPath);
            return null;
        }
    }

    /// <summary>
    /// Batched override of <see cref="IStorageAdapter.ReadMany"/> — multi-path probes become ONE
    /// SQL query per (table, namespace) group instead of N, mirroring PG. Pump inside the IIoPool
    /// (InvokeStream) — never <c>Observable.Create(async ...)</c>, which starts the pump on the
    /// SUBSCRIBER's thread (the grain-wedge / dropped-initial-emission defect).
    /// </summary>
    public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
        => _ioPool.InvokeStream(ct => ReadManyAsyncCore(paths, options, ct));

    /// <summary>ReadMany leaf: one connection, one query per (table, namespace) group; absent tables skip the group.</summary>
    private async IAsyncEnumerable<MeshNode> ReadManyAsyncCore(
        IReadOnlyCollection<string> paths,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Normalize + drop empties up front. Group by (table, namespace) so each round-trip is
        // `WHERE "namespace" = :ns AND "id" IN (...)` — the cheapest shape for the (namespace, id) key.
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

        if (groups.Count == 0)
            yield break;

        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        foreach (var group in groups)
        {
            var table = group.Key.table;
            var ns = group.Key.ns;
            var ids = group.Select(t => t.id).Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length == 0)
                continue;

            var placeholders = string.Join(", ", Enumerable.Range(0, ids.Length).Select(i => $":id{i}"));
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"SELECT {NodeColumns}, {SyncBehaviorCol(table)} " +
                $"FROM {table} WHERE \"namespace\" = :ns AND \"id\" IN ({placeholders})";
            SnowflakeConnectionSource.AddParam(cmd, "ns", ns, DbType.String);
            for (var i = 0; i < ids.Length; i++)
                SnowflakeConnectionSource.AddParam(cmd, $"id{i}", ids[i], DbType.String);

            // Open the reader in its own try/catch: `yield return` can't live inside a
            // catch-bearing try, so the open is separated from the read loop. An absent table
            // (unprovisioned partition) simply contributes no rows.
            DbDataReader? reader;
            try
            {
                reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsUndefinedObject(ex))
            {
                _logger?.LogDebug(ex, "ReadMany on {Table} hit undefined-object; skipping group.", table);
                continue;
            }

            await using (reader)
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    yield return SnowflakeMeshNodeReader.ReadMeshNode(reader, options, _logger);
            }
        }
    }

    /// <inheritdoc />
    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => _ioPool.Invoke<MeshNode?>(async ct =>
        {
            await WriteAsyncCore(node, options, ct).ConfigureAwait(false);
            // Fire the in-process Changes feed so same-process synced-query subscribers re-emit
            // without waiting for the change-feed poller round-trip. The poller's origin-id
            // filter drops this silo's own event-log echoes, so there is no double-fire.
            try
            {
                _changes.OnNext(DataChangeNotification.Updated(
                    string.IsNullOrEmpty(node.Path) ? node.Id : node.Path, node));
            }
            catch { /* never throw — change feed is best-effort */ }
            return node;
        });

    /// <summary>
    /// Write leaf: embedding generation → MERGE upsert (or DELETE+INSERT fallback) → the three
    /// trigger-replacement steps (history copy, auth mirror, permission projection), all on ONE
    /// connection inside the cap-1 write-pool slot. Each post-write step is individually
    /// try/catch-guarded: a projection/mirror failure logs a warning but never fails the node
    /// write (mirroring PG's fail-safe trigger guards).
    /// </summary>
    private async Task WriteAsyncCore(MeshNode node, JsonSerializerOptions options, CancellationToken ct)
    {
        var ns = node.Namespace ?? "";

        // Generate embedding — the same text contract as PG (name + nodeType).
        var embeddingText = string.Join(" ",
            new[] { node.Name, node.NodeType }
                .Where(s => !string.IsNullOrEmpty(s)));
        var embeddingVector = await _embeddingProvider.GenerateEmbeddingAsync(embeddingText).ConfigureAwait(false);

        var contentJson = node.Content != null
            ? JsonSerializer.Serialize(node.Content, node.Content.GetType(), options)
            : null;

        var rawTable = ResolveRawTable(node.Path, node.NodeType);
        var table = QualifyTable(rawTable);
        // sync_behavior lives only on mesh_nodes (the sole decouplable table).
        var writeSync = rawTable == "mesh_nodes";
        // Vector fragments appear ONLY when the endpoint supports the type, the option doesn't
        // disable it AND an embedding was actually produced — recomputed per write (capabilities
        // are probed after construction; the provider may return null for empty text).
        var capabilities = _capabilities.Current;
        var writeEmbedding = capabilities.SupportsVector
            && _options.EnableVectorType != false
            && embeddingVector != null;

        var path = ComputePath(ns, node.Id);
        var lastModified = node.LastModified == default ? DateTimeOffset.UtcNow : node.LastModified;

        // (column, source-expression) pairs shared by the MERGE and the DELETE+INSERT fallback
        // so the two shapes can never drift. `path` is a REAL column here — computed in C# with
        // PG's generated-column semantics.
        var columns = new List<(string Column, string Expr)>
        {
            ("namespace", ":ns"),
            ("id", ":id"),
            ("path", ":path"),
            ("name", ":name"),
            ("description", ":description"),
            ("node_type", ":node_type"),
            ("category", ":category"),
            ("icon", ":icon"),
            ("display_order", ":display_order"),
            ("last_modified", ":last_modified"),
            ("version", ":version"),
            ("state", ":state"),
            ("content", "TRY_PARSE_JSON(:content)"),
            ("desired_id", ":desired_id"),
            ("main_node", ":main_node")
        };
        if (writeSync)
            columns.Add(("sync_behavior", ":sync_behavior"));
        if (writeEmbedding)
            columns.Add(("embedding", $"PARSE_JSON(:embedding)::VECTOR(FLOAT, {_options.VectorDimensions})"));

        void BindNodeParams(DbCommand cmd)
        {
            SnowflakeConnectionSource.AddParam(cmd, "ns", ns, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "id", node.Id, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "path", path, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "name", node.Name, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "description", node.Description, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "node_type", node.NodeType, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "category", node.Category, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "icon", node.Icon, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "display_order",
                (object?)node.Order, DbType.Int32);
            SnowflakeConnectionSource.AddParam(cmd, "last_modified", lastModified.UtcDateTime, DbType.DateTime);
            SnowflakeConnectionSource.AddParam(cmd, "version", node.Version, DbType.Int64);
            SnowflakeConnectionSource.AddParam(cmd, "state", (short)node.State, DbType.Int16);
            SnowflakeConnectionSource.AddParam(cmd, "content", contentJson, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "desired_id", node.DesiredId, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "main_node", node.MainNode, DbType.String);
            if (writeSync)
                SnowflakeConnectionSource.AddParam(cmd, "sync_behavior", (short)node.SyncBehavior, DbType.Int16);
            if (writeEmbedding)
                SnowflakeConnectionSource.AddParam(cmd, "embedding", ToJsonFloatArray(embeddingVector!), DbType.String);
        }

        void BindNodeKey(DbCommand cmd)
        {
            SnowflakeConnectionSource.AddParam(cmd, "ns", ns, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "id", node.Id, DbType.String);
        }

        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await UpsertAsync(connection, table, columns, BindNodeParams, BindNodeKey, capabilities, ct)
            .ConfigureAwait(false);

        // ── Trigger replacement ────────────────────────────────────────────────────────────
        // PG performs the next three steps in DB triggers on mesh_nodes / access. Snowflake has
        // no triggers, so they run here, on the SAME connection (same pool slot), AFTER the
        // upsert, each guarded so a failure never fails the node write.
        if (writeSync)
        {
            // 1. History copy (PG: trg_mesh_node_to_history, AFTER INSERT OR UPDATE on
            //    mesh_nodes, versioned partitions only).
            if (_partitionDefinition?.Versioned == true)
            {
                try
                {
                    await CopyToHistoryAsync(connection, ns, node, path, contentJson, lastModified, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (IsUndefinedObject(ex))
                {
                    _logger?.LogDebug(ex, "History copy skipped for {Path}: history table not provisioned.", path);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "History copy failed for {Path}.", path);
                }
            }

            // 2. Auth mirror (PG: mirror_access_object_to_auth_schema, on mesh_nodes only,
            //    never on the auth schema itself).
            if (node.NodeType is { } nodeType
                && MirroredNodeTypes.Contains(nodeType)
                && !string.IsNullOrEmpty(_schemaName)
                && !string.Equals(_schemaName, AuthSchemaName, StringComparison.Ordinal))
            {
                try
                {
                    await MirrorToAuthAsync(connection, ns, node, path, contentJson, lastModified, capabilities, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (IsUndefinedObject(ex))
                {
                    // Fail-safe mirror of PG's to_regclass guard: an unprovisioned auth schema
                    // must NEVER fail the originating write.
                    _logger?.LogDebug(ex, "Auth mirror skipped for {Path}: auth schema not provisioned.", path);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Auth mirror failed for {Path}.", path);
                }
            }
        }

        // 3. Permission projection (PG: trg_access_changed on the access satellite; extended
        //    here to the projection-input node types — see ProjectionNodeTypes).
        if (rawTable == "access"
            || (writeSync && node.NodeType is { } nt && ProjectionNodeTypes.Contains(nt)))
        {
            await RebuildProjectionGuardedAsync(connection, path, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one upsert keyed on <c>(namespace, id)</c>: a <c>MERGE ... USING (SELECT :params)</c>
    /// when the endpoint supports MERGE, else DELETE-by-key + INSERT executed sequentially on the
    /// same connection (the emulator fallback; both statements run inside the same cap-1
    /// write-pool slot, so within this silo the pair is effectively atomic).
    /// </summary>
    /// <param name="connection">The open connection (same pool slot).</param>
    /// <param name="table">Qualified target table.</param>
    /// <param name="columns">(column, source-expression) pairs; the first two must be the <c>namespace</c>/<c>id</c> key.</param>
    /// <param name="bind">Binds every parameter the full column list references onto a command.</param>
    /// <param name="bindKey">Binds ONLY the <c>:ns</c>/<c>:id</c> key parameters — the fallback DELETE references nothing else, and unused binds must not be sent.</param>
    /// <param name="capabilities">The capabilities snapshot for this operation.</param>
    /// <param name="ct">Cancellation token.</param>
    private static async Task UpsertAsync(
        DbConnection connection,
        string table,
        IReadOnlyList<(string Column, string Expr)> columns,
        Action<DbCommand> bind,
        Action<DbCommand> bindKey,
        SnowflakeCapabilities capabilities,
        CancellationToken ct)
    {
        if (capabilities.SupportsMerge)
        {
            var sourceSelect = string.Join(", ",
                columns.Select(c => $"{c.Expr} AS {SnowflakeIdentifiers.Quote(c.Column)}"));
            var updateSet = string.Join(", ",
                columns.Where(c => c.Column is not ("namespace" or "id"))
                    .Select(c => $"{SnowflakeIdentifiers.Quote(c.Column)} = s.{SnowflakeIdentifiers.Quote(c.Column)}"));
            var insertColumns = string.Join(", ", columns.Select(c => SnowflakeIdentifiers.Quote(c.Column)));
            var insertValues = string.Join(", ", columns.Select(c => $"s.{SnowflakeIdentifiers.Quote(c.Column)}"));

            await using var merge = connection.CreateCommand();
            merge.CommandText = $"""
                MERGE INTO {table} AS t
                USING (SELECT {sourceSelect}) AS s
                ON t."namespace" = s."namespace" AND t."id" = s."id"
                WHEN MATCHED THEN UPDATE SET {updateSet}
                WHEN NOT MATCHED THEN INSERT ({insertColumns}) VALUES ({insertValues})
                """;
            bind(merge);
            await merge.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return;
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = $"DELETE FROM {table} WHERE \"namespace\" = :ns AND \"id\" = :id";
            bindKey(delete);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText =
            $"INSERT INTO {table} ({string.Join(", ", columns.Select(c => SnowflakeIdentifiers.Quote(c.Column)))}) " +
            $"SELECT {string.Join(", ", columns.Select(c => c.Expr))}";
        bind(insert);
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends the just-written node to <c>mesh_node_history</c> when no row with the same
    /// <c>(namespace, id, version)</c> exists — the C# transcription of PG's
    /// <c>trg_mesh_node_to_history</c> (its <c>ON CONFLICT DO NOTHING</c> becomes the
    /// <c>WHERE NOT EXISTS</c> guard). The PG trigger copies NEW.* without stamping
    /// <c>changed_by</c> (no session variable is read), so the column is left NULL here too.
    /// Like PG's history row, neither <c>sync_behavior</c> nor <c>embedding</c> is carried.
    /// </summary>
    private async Task CopyToHistoryAsync(
        DbConnection connection,
        string ns,
        MeshNode node,
        string path,
        string? contentJson,
        DateTimeOffset lastModified,
        CancellationToken ct)
    {
        var history = QualifyTable("mesh_node_history");
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {history}
                ("namespace", "id", "path", "name", "node_type", "description", "category", "icon",
                 "display_order", "last_modified", "version", "state", "content", "desired_id", "main_node")
            SELECT s.* FROM (SELECT
                :ns AS "namespace", :id AS "id", :path AS "path", :name AS "name",
                :node_type AS "node_type", :description AS "description", :category AS "category",
                :icon AS "icon", :display_order AS "display_order", :last_modified AS "last_modified",
                :version AS "version", :state AS "state", TRY_PARSE_JSON(:content) AS "content",
                :desired_id AS "desired_id", :main_node AS "main_node") s
            WHERE NOT EXISTS (
                SELECT 1 FROM {history} h
                WHERE h."namespace" = :ns AND h."id" = :id AND h."version" = :version)
            """;
        SnowflakeConnectionSource.AddParam(cmd, "ns", ns, DbType.String);
        SnowflakeConnectionSource.AddParam(cmd, "id", node.Id, DbType.String);
        SnowflakeConnectionSource.AddParam(cmd, "path", path, DbType.String);
        SnowflakeConnectionSource.AddParam(cmd, "name", node.Name, DbType.String);
        SnowflakeConnectionSource.AddParam(cmd, "node_type", node.NodeType, DbType.String);
        SnowflakeConnectionSource.AddParam(cmd, "description", node.Description, DbType.String);
        SnowflakeConnectionSource.AddParam(cmd, "category", node.Category, DbType.String);
        SnowflakeConnectionSource.AddParam(cmd, "icon", node.Icon, DbType.String);
        SnowflakeConnectionSource.AddParam(cmd, "display_order",
            (object?)node.Order, DbType.Int32);
        SnowflakeConnectionSource.AddParam(cmd, "last_modified", lastModified.UtcDateTime, DbType.DateTime);
        SnowflakeConnectionSource.AddParam(cmd, "version", node.Version, DbType.Int64);
        SnowflakeConnectionSource.AddParam(cmd, "state", (short)node.State, DbType.Int16);
        SnowflakeConnectionSource.AddParam(cmd, "content", contentJson, DbType.String);
        SnowflakeConnectionSource.AddParam(cmd, "desired_id", node.DesiredId, DbType.String);
        SnowflakeConnectionSource.AddParam(cmd, "main_node", node.MainNode, DbType.String);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Upserts an access-object node into the central <c>"auth"."mesh_nodes"</c> mirror — the C#
    /// transcription of PG's <c>mirror_access_object_to_auth_schema()</c> INSERT branch. The PG
    /// trigger mirrors exactly (namespace, id, name, node_type, category, icon, display_order,
    /// last_modified, version, state, content, desired_id, main_node) — notably NOT
    /// <c>description</c>; that asymmetry is preserved. <c>path</c> is additionally written
    /// because on Snowflake it is a REAL NOT NULL column (PG generates it).
    /// </summary>
    private async Task MirrorToAuthAsync(
        DbConnection connection,
        string ns,
        MeshNode node,
        string path,
        string? contentJson,
        DateTimeOffset lastModified,
        SnowflakeCapabilities capabilities,
        CancellationToken ct)
    {
        var authTable = SnowflakeIdentifiers.Qualify(AuthSchemaName, "mesh_nodes");
        var columns = new List<(string Column, string Expr)>
        {
            ("namespace", ":ns"),
            ("id", ":id"),
            ("path", ":path"),
            ("name", ":name"),
            ("node_type", ":node_type"),
            ("category", ":category"),
            ("icon", ":icon"),
            ("display_order", ":display_order"),
            ("last_modified", ":last_modified"),
            ("version", ":version"),
            ("state", ":state"),
            ("content", "TRY_PARSE_JSON(:content)"),
            ("desired_id", ":desired_id"),
            ("main_node", ":main_node")
        };

        void Bind(DbCommand cmd)
        {
            SnowflakeConnectionSource.AddParam(cmd, "ns", ns, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "id", node.Id, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "path", path, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "name", node.Name, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "node_type", node.NodeType, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "category", node.Category, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "icon", node.Icon, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "display_order",
                (object?)node.Order, DbType.Int32);
            SnowflakeConnectionSource.AddParam(cmd, "last_modified", lastModified.UtcDateTime, DbType.DateTime);
            SnowflakeConnectionSource.AddParam(cmd, "version", node.Version, DbType.Int64);
            SnowflakeConnectionSource.AddParam(cmd, "state", (short)node.State, DbType.Int16);
            SnowflakeConnectionSource.AddParam(cmd, "content", contentJson, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "desired_id", node.DesiredId, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "main_node", node.MainNode, DbType.String);
        }

        void BindKey(DbCommand cmd)
        {
            SnowflakeConnectionSource.AddParam(cmd, "ns", ns, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "id", node.Id, DbType.String);
        }

        await UpsertAsync(connection, authTable, columns, Bind, BindKey, capabilities, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-runs the permission projection for this partition on the given (already open)
    /// connection — guarded so a projection failure logs a warning but never fails the
    /// triggering node write/delete. No-ops for schemaless adapters (nothing to project).
    /// </summary>
    private async Task RebuildProjectionGuardedAsync(DbConnection connection, string path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_schemaName))
            return;
        try
        {
            await SnowflakeAccessProjection.RebuildOnConnectionAsync(
                connection, _schemaName, _options.Schema, _logger, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUndefinedObject(ex))
        {
            _logger?.LogDebug(ex,
                "Permission projection skipped for {Path}: projection tables not provisioned.", path);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Permission projection rebuild failed after write to {Path}.", path);
        }
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

    /// <summary>
    /// Delete leaf: reads the row's <c>node_type</c> first (PG's triggers see OLD.*; without
    /// triggers the type must be known BEFORE the row is gone to decide the auth-mirror delete
    /// and the projection rebuild), then deletes, then runs the trigger-replacement steps on the
    /// same connection.
    /// </summary>
    private async Task DeleteAsyncCore(string path, CancellationToken ct)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath))
            return;

        var (ns, id) = SplitPath(normalizedPath);

        var rawTable = ResolveRawTable(normalizedPath);
        var table = QualifyTable(rawTable);
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);

        // OLD.node_type — needed only for mesh_nodes rows (mirror + projection decisions).
        string? nodeType = null;
        if (rawTable == "mesh_nodes")
        {
            try
            {
                await using var select = connection.CreateCommand();
                select.CommandText =
                    $"SELECT \"node_type\" FROM {table} WHERE \"namespace\" = :ns AND \"id\" = :id";
                SnowflakeConnectionSource.AddParam(select, "ns", ns, DbType.String);
                SnowflakeConnectionSource.AddParam(select, "id", id, DbType.String);
                var value = await select.ExecuteScalarAsync(ct).ConfigureAwait(false);
                nodeType = value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (IsUndefinedObject(ex))
            {
                // Table absent → the DELETE below will fault the same way PG's does; the
                // pre-read itself must not.
                _logger?.LogDebug(ex, "Pre-delete node_type read hit undefined-object for {Path}.", normalizedPath);
            }
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"DELETE FROM {table} WHERE \"namespace\" = :ns AND \"id\" = :id";
            SnowflakeConnectionSource.AddParam(cmd, "ns", ns, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "id", id, DbType.String);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // ── Trigger replacement (delete side) ─────────────────────────────────────────────
        // Auth mirror delete: PG's trigger DELETE branch (guarded by OLD.node_type IN (...)).
        if (rawTable == "mesh_nodes"
            && nodeType is not null
            && MirroredNodeTypes.Contains(nodeType)
            && !string.IsNullOrEmpty(_schemaName)
            && !string.Equals(_schemaName, AuthSchemaName, StringComparison.Ordinal))
        {
            try
            {
                await using var mirror = connection.CreateCommand();
                mirror.CommandText =
                    $"DELETE FROM {SnowflakeIdentifiers.Qualify(AuthSchemaName, "mesh_nodes")} " +
                    "WHERE \"namespace\" = :ns AND \"id\" = :id";
                SnowflakeConnectionSource.AddParam(mirror, "ns", ns, DbType.String);
                SnowflakeConnectionSource.AddParam(mirror, "id", id, DbType.String);
                await mirror.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsUndefinedObject(ex))
            {
                _logger?.LogDebug(ex, "Auth mirror delete skipped for {Path}: auth schema not provisioned.", normalizedPath);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Auth mirror delete failed for {Path}.", normalizedPath);
            }
        }

        // Permission projection: access-satellite deletes (PG's trg_access_changed DELETE
        // branch) and projection-input node types (see ProjectionNodeTypes).
        if (rawTable == "access"
            || (rawTable == "mesh_nodes" && nodeType is not null && ProjectionNodeTypes.Contains(nodeType)))
        {
            await RebuildProjectionGuardedAsync(connection, normalizedPath, ct).ConfigureAwait(false);
        }
    }

    // Child-listing is a READ → runs in the read pool, bounded, NOT the cap-1 write pool
    // (which would serialise it behind writes).
    /// <inheritdoc />
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
        => _readPool.Invoke(ct => ListChildPathsAsyncCore(parentPath, ct))
            .Catch<(IEnumerable<string>, IEnumerable<string>), Exception>(ex => IsUndefinedObject(ex)
                ? Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], []))
                : Observable.Throw<(IEnumerable<string>, IEnumerable<string>)>(ex));

    /// <summary>Child-listing leaf — nodes whose namespace equals the parent path, mirroring PG.</summary>
    private async Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsyncCore(
        string? parentPath,
        CancellationToken ct)
    {
        var normalizedParent = NormalizePath(parentPath);

        var table = ResolveTable(normalizedParent);
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT \"id\", \"namespace\" FROM {table} WHERE \"namespace\" = :ns";
        SnowflakeConnectionSource.AddParam(cmd, "ns", normalizedParent, DbType.String);

        var paths = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var ns = reader.GetString(1);
            paths.Add(ComputePath(ns, id));
        }

        return (paths, Enumerable.Empty<string>());
    }

    /// <inheritdoc />
    public IObservable<bool> Exists(string path)
        => _ioPool.Invoke(ct => ExistsAsyncCore(path, ct))
            .Catch<bool, Exception>(ex => IsUndefinedObject(ex)
                ? Observable.Return(false)
                : Observable.Throw<bool>(ex));

    /// <summary>Existence-check leaf — a keyed <c>SELECT 1 ... LIMIT 1</c>, mirroring PG.</summary>
    private async Task<bool> ExistsAsyncCore(string path, CancellationToken ct)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath))
            return false;

        var (ns, id) = SplitPath(normalizedPath);

        var table = ResolveTable(normalizedPath);
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM {table} WHERE \"namespace\" = :ns AND \"id\" = :id LIMIT 1";
        SnowflakeConnectionSource.AddParam(cmd, "ns", ns, DbType.String);
        SnowflakeConnectionSource.AddParam(cmd, "id", id, DbType.String);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => _ioPool.Invoke(ct => FindBestPrefixMatchAsyncCore(fullPath, options, ct))
            .Catch<(MeshNode?, int), Exception>(ex => IsUndefinedObject(ex)
                ? Observable.Return<(MeshNode?, int)>((null, 0))
                : Observable.Throw<(MeshNode?, int)>(ex));

    /// <summary>
    /// Longest-prefix single-table lookup leaf. The <c>:p LIKE "path" || '/%'</c> predicate is
    /// deliberately bug-compatible with PG (no LIKE-escape of pattern characters inside stored
    /// paths — PG doesn't escape either).
    /// </summary>
    private async Task<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatchAsyncCore(
        string fullPath, JsonSerializerOptions options, CancellationToken ct)
    {
        var normalizedPath = NormalizePath(fullPath);
        if (string.IsNullOrEmpty(normalizedPath))
            return (null, 0);

        // Single SQL query: the node whose path is the longest prefix of the input — exact
        // match or any ancestor — deepest (most specific) first.
        var table = ResolveTable(normalizedPath);
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"SELECT {NodeColumns}, {SyncBehaviorCol(table)} " +
            $"FROM {table} WHERE :p = \"path\" OR :p LIKE \"path\" || '/%' " +
            "ORDER BY LENGTH(\"path\") DESC LIMIT 1";
        SnowflakeConnectionSource.AddParam(cmd, "p", normalizedPath, DbType.String);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return (null, 0);

        var node = SnowflakeMeshNodeReader.ReadMeshNode(reader, options, _logger);
        var matchedSegments = node.Path.Split('/').Length;
        return (node, matchedSegments);
    }

    /// <summary>
    /// Resolves the closest-matching MeshNode for <paramref name="fullPath"/> across the
    /// partition's primary <c>mesh_nodes</c> table AND every satellite table named in
    /// <see cref="PartitionDefinition.TableMappings"/> in a SINGLE round-trip — a CTE of
    /// UNION-ALL branches whose outer ORDER BY picks the deepest path-prefix match, exactly
    /// like PG's <c>ResolvePath</c>. Contract: <c>PathResolutionTests</c>.
    /// </summary>
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => _ioPool.Invoke(ct => ResolvePathAsyncCore(fullPath, options, ct))
            .Catch<(MeshNode?, int), Exception>(ex => IsUndefinedObject(ex)
                ? Observable.Return<(MeshNode?, int)>((null, 0))
                : Observable.Throw<(MeshNode?, int)>(ex));

    /// <summary>Multi-table longest-prefix resolution leaf — see <see cref="ResolvePath"/>.</summary>
    private async Task<(MeshNode? Node, int MatchedSegments)> ResolvePathAsyncCore(
        string fullPath, JsonSerializerOptions options, CancellationToken ct)
    {
        var normalizedPath = NormalizePath(fullPath);
        if (string.IsNullOrEmpty(normalizedPath))
            return (null, 0);

        // Primary + every distinct satellite table (case-insensitive dedup — multiple suffixes
        // can map to the same table, e.g. _Comment / _Approval / _Tracking all → annotations).
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mesh_nodes" };
        if (_partitionDefinition?.TableMappings is { } mappings)
            foreach (var t in mappings.Values)
                if (!string.IsNullOrEmpty(t))
                    tables.Add(t);

        var unionBranches = new List<string>(tables.Count);
        foreach (var t in tables)
        {
            var qualified = QualifyTable(t);
            unionBranches.Add(
                $"SELECT {NodeColumns}, {SyncBehaviorCol(qualified)} " +
                $"FROM {qualified} " +
                "WHERE :p = \"path\" OR :p LIKE \"path\" || '/%'");
        }
        var sql =
            "WITH candidates AS (\n" +
            string.Join("\n UNION ALL\n", unionBranches) +
            "\n) " +
            "SELECT * FROM candidates " +
            "ORDER BY LENGTH(CASE WHEN \"namespace\" = '' THEN \"id\" ELSE \"namespace\" || '/' || \"id\" END) DESC " +
            "LIMIT 1";

        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        SnowflakeConnectionSource.AddParam(cmd, "p", normalizedPath, DbType.String);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return (null, 0);

        var node = SnowflakeMeshNodeReader.ReadMeshNode(reader, options, _logger);
        var matchedSegments = node.Path.Split('/').Length;
        return (node, matchedSegments);
    }

    #region Partition Storage

    // Pump inside the IIoPool (InvokeStream) — never Observable.Create(async ...), which starts
    // the pump on the subscriber's scheduler (the grain-wedge edge; see
    // PartitionObjectsSubscriberIndependenceTest for the repro shape).
    /// <inheritdoc />
    public IObservable<object> GetPartitionObjects(
        string nodePath, string? subPath, JsonSerializerOptions options)
        => _ioPool.InvokeStream(ct => GetPartitionObjectsAsyncCore(nodePath, subPath, options, ct))
            .Catch<object, Exception>(ex => IsUndefinedObject(ex)
                // Absent schema (router resolved synchronously, schema never created) →
                // nothing to read. Complete empty, don't fault.
                ? Observable.Empty<object>()
                : Observable.Throw<object>(ex));

    /// <summary>Partition-object enumeration leaf — VARIANT JSON deserialized via type_name when resolvable.</summary>
    private async IAsyncEnumerable<object> GetPartitionObjectsAsyncCore(
        string nodePath, string? subPath, JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var partitionKey = GetPartitionStorageKey(nodePath, subPath);

        var poTable = QualifyTable("partition_objects");
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT \"data\", \"type_name\" FROM {poTable} WHERE \"partition_key\" = :pk";
        SnowflakeConnectionSource.AddParam(cmd, "pk", partitionKey, DbType.String);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // VARIANT alphabetizes keys exactly like PG jsonb → re-front the discriminator.
            var json = SnowflakeMeshNodeReader.EnsureTypeDiscriminatorFirst(reader.GetString(0));
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
        => _ioPool.Invoke(async ct =>
        {
            await SavePartitionObjectsAsyncCore(nodePath, subPath, objects, options, ct).ConfigureAwait(false);
            return Unit.Default;
        });

    /// <summary>
    /// Save leaf: delete-then-insert on ONE connection. Where the PG code loops one INSERT
    /// round-trip per object, Snowflake round-trips are expensive, so objects are written in
    /// chunked multi-row <c>INSERT ... SELECT ... UNION ALL SELECT ...</c> statements (the
    /// SELECT form because Snowflake's plain <c>VALUES</c> clause rejects expressions like
    /// <c>TRY_PARSE_JSON</c>). Objects are de-duplicated by id first (last one wins), matching
    /// the effective semantics of PG's per-row upsert after the delete.
    /// </summary>
    private async Task SavePartitionObjectsAsyncCore(
        string nodePath,
        string? subPath,
        IReadOnlyCollection<object> objects,
        JsonSerializerOptions options,
        CancellationToken ct = default)
    {
        var partitionKey = GetPartitionStorageKey(nodePath, subPath);

        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await DeletePartitionObjectsOnConnectionAsync(connection, partitionKey, ct).ConfigureAwait(false);

        // Dedup by id, last wins — PG's DELETE + per-row ON CONFLICT upsert net effect.
        var rows = new Dictionary<string, (string Json, string? TypeName)>(StringComparer.Ordinal);
        foreach (var obj in objects)
        {
            var id = GetObjectId(obj);
            rows[id] = (JsonSerializer.Serialize(obj, obj.GetType(), options), obj.GetType().AssemblyQualifiedName);
        }
        if (rows.Count == 0)
            return;

        var poTable = QualifyTable("partition_objects");
        var now = DateTimeOffset.UtcNow;
        // 5 parameters per row; chunk to keep statements well under driver/emulator limits.
        const int chunkSize = 100;
        foreach (var chunk in rows.Chunk(chunkSize))
        {
            await using var cmd = connection.CreateCommand();
            var selects = new List<string>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                selects.Add($"SELECT :id{i}, :pk, :tn{i}, TRY_PARSE_JSON(:data{i}), :lm{i}");
                SnowflakeConnectionSource.AddParam(cmd, $"id{i}", chunk[i].Key, DbType.String);
                SnowflakeConnectionSource.AddParam(cmd, $"tn{i}", chunk[i].Value.TypeName, DbType.String);
                SnowflakeConnectionSource.AddParam(cmd, $"data{i}", chunk[i].Value.Json, DbType.String);
                SnowflakeConnectionSource.AddParam(cmd, $"lm{i}", now.UtcDateTime, DbType.DateTime);
            }
            SnowflakeConnectionSource.AddParam(cmd, "pk", partitionKey, DbType.String);
            cmd.CommandText =
                $"INSERT INTO {poTable} (\"id\", \"partition_key\", \"type_name\", \"data\", \"last_modified\")\n" +
                string.Join("\nUNION ALL\n", selects);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => _ioPool.Invoke(async ct =>
        {
            await DeletePartitionObjectsAsyncCore(nodePath, subPath, ct).ConfigureAwait(false);
            return Unit.Default;
        })
            .Catch<Unit, Exception>(ex => IsUndefinedObject(ex)
                ? Observable.Return(Unit.Default)
                : Observable.Throw<Unit>(ex));

    /// <summary>Delete-partition-objects leaf (opens its own connection).</summary>
    private async Task DeletePartitionObjectsAsyncCore(
        string nodePath,
        string? subPath = null,
        CancellationToken ct = default)
    {
        var partitionKey = GetPartitionStorageKey(nodePath, subPath);
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await DeletePartitionObjectsOnConnectionAsync(connection, partitionKey, ct).ConfigureAwait(false);
    }

    /// <summary>Shared delete-by-partition-key statement (Save reuses the caller's connection).</summary>
    private async Task DeletePartitionObjectsOnConnectionAsync(
        DbConnection connection, string partitionKey, CancellationToken ct)
    {
        var poTable = QualifyTable("partition_objects");
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM {poTable} WHERE \"partition_key\" = :pk";
        SnowflakeConnectionSource.AddParam(cmd, "pk", partitionKey, DbType.String);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => _ioPool.Invoke(ct => GetPartitionMaxTimestampAsyncCore(nodePath, subPath, ct))
            .Catch<DateTimeOffset?, Exception>(ex => IsUndefinedObject(ex)
                ? Observable.Return<DateTimeOffset?>(null)
                : Observable.Throw<DateTimeOffset?>(ex));

    /// <summary>Max-timestamp leaf — TIMESTAMP_NTZ stores UTC, so a bare DateTime is re-stamped UTC.</summary>
    private async Task<DateTimeOffset?> GetPartitionMaxTimestampAsyncCore(
        string nodePath,
        string? subPath = null,
        CancellationToken ct = default)
    {
        var partitionKey = GetPartitionStorageKey(nodePath, subPath);

        var poTable = QualifyTable("partition_objects");
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT MAX(\"last_modified\") FROM {poTable} WHERE \"partition_key\" = :pk";
        SnowflakeConnectionSource.AddParam(cmd, "pk", partitionKey, DbType.String);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is DateTimeOffset dto)
            return dto.ToUniversalTime();
        if (result is DateTime dt)
            return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero);
        return null;
    }

    /// <inheritdoc />
    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => _ioPool.Invoke(ct => ListPartitionSubPathsAsyncCore(nodePath, ct))
            .Catch<IEnumerable<string>, Exception>(ex => IsUndefinedObject(ex)
                ? Observable.Return(Enumerable.Empty<string>())
                : Observable.Throw<IEnumerable<string>>(ex));

    /// <summary>
    /// Sub-path listing leaf — PG's <c>position</c>/<c>substring FROM..FOR</c> forms translated
    /// to Snowflake's <c>CHARINDEX</c>/comma-form <c>SUBSTRING</c>.
    /// </summary>
    private async Task<IEnumerable<string>> ListPartitionSubPathsAsyncCore(string nodePath, CancellationToken ct)
    {
        var prefix = NormalizePath(nodePath) + "/";

        var poTable = QualifyTable("partition_objects");
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT
                CASE WHEN CHARINDEX('/', SUBSTRING("partition_key", LENGTH(:prefix) + 1)) > 0
                     THEN SUBSTRING("partition_key", LENGTH(:prefix) + 1,
                          CHARINDEX('/', SUBSTRING("partition_key", LENGTH(:prefix) + 1)) - 1)
                     ELSE SUBSTRING("partition_key", LENGTH(:prefix) + 1)
                END AS "sub_path"
            FROM {poTable}
            WHERE "partition_key" LIKE :pattern
            """;
        SnowflakeConnectionSource.AddParam(cmd, "prefix", prefix, DbType.String);
        SnowflakeConnectionSource.AddParam(cmd, "pattern", prefix + "%", DbType.String);

        var subPaths = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var sub = reader.IsDBNull(0) ? "" : reader.GetString(0);
            if (!string.IsNullOrEmpty(sub))
                subPaths.Add(sub);
        }

        return subPaths;
    }

    #endregion

    #region Query Support

    /// <summary>
    /// Queries nodes using a parsed query, translated to Snowflake SQL by
    /// <see cref="SnowflakeSqlGenerator"/>. The reader pump runs in the per-adapter READ pool
    /// via <see cref="ReadPooled{T}"/> — one pooled slot for the whole enumeration, bounding
    /// read fan-out. Mirrors PG's <c>QueryNodesAsync</c> including the scope-clause injection.
    /// </summary>
    /// <param name="query">The parsed query.</param>
    /// <param name="options">Serializer options for content deserialization.</param>
    /// <param name="userId">Optional user id for access filtering.</param>
    /// <param name="basePath">Optional base path when the query itself carries none.</param>
    /// <param name="activityUserId">Optional user id for <c>source:accessed</c> queries.</param>
    /// <param name="excludedNodeTypes">Node types to exclude from results.</param>
    /// <param name="ct">Cancellation token.</param>
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

    /// <summary>Single-query leaf — table resolution + generator + scope-clause injection, mirroring PG.</summary>
    private async IAsyncEnumerable<MeshNode> QueryNodesInnerAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        string? userId,
        string? basePath,
        string? activityUserId,
        IReadOnlyCollection<string>? excludedNodeTypes,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var (sql, parameters) = BuildSingleQuerySql(
            query, options, userId, basePath, activityUserId, excludedNodeTypes,
            includeContent: SelectorAsksFor(query.Select, "content"));

        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            _logger.LogDebug("SQL: {Sql}", sql);
            foreach (var (name, value) in parameters)
                _logger.LogDebug("  Param {Name} = {Value}", name, value);
        }

        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            BindGeneratedParameter(cmd, name, value);

        // Open the reader in its own try/catch: an absent schema (the router resolves the schema
        // synchronously) faults at ExecuteReaderAsync — treat as "no rows". `yield return` can't
        // live inside a catch-bearing try, so the open is separated from the read loop.
        DbDataReader? reader;
        try
        {
            reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUndefinedObject(ex))
        {
            yield break;
        }

        await using (reader)
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                yield return SnowflakeMeshNodeReader.ReadMeshNode(reader, options, _logger);
        }
    }

    /// <summary>
    /// Multi-query UNION variant — one SELECT per parsed query with disjoint per-query parameter
    /// names, joined with <c>UNION ALL</c> and deduplicated by node identity
    /// <c>(namespace, id)</c> with newest <c>last_modified</c> winning. PG's
    /// <c>DISTINCT ON</c> has no Snowflake twin, so dedup uses
    /// <c>QUALIFY ROW_NUMBER() OVER (PARTITION BY ...) = 1</c> when the endpoint supports
    /// QUALIFY, else the same ROW_NUMBER in a derived table (the extra <c>rn</c> column is
    /// ignored by the name-based reader). Single round-trip, server-side dedup.
    /// </summary>
    /// <param name="queries">The parsed queries to union.</param>
    /// <param name="options">Serializer options for content deserialization.</param>
    /// <param name="userId">Optional user id for access filtering.</param>
    /// <param name="basePath">Optional base path when a query itself carries none.</param>
    /// <param name="activityUserId">Optional user id for <c>source:accessed</c> queries.</param>
    /// <param name="excludedNodeTypes">Node types to exclude from results.</param>
    /// <param name="ct">Cancellation token.</param>
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
        // must NOT wrap this in our own ReadPooled too: that would hold a read slot while the
        // delegate acquires a SECOND, the one same-pool nesting that can deadlock the gate.
        if (queries.Count == 1)
            return QueryNodesAsync(queries[0], options, userId, basePath, activityUserId, excludedNodeTypes, ct);
        // Multi-query UNION: ONE pooled slot for the whole reader enumeration.
        return ReadPooled(
            c => QueryNodesUnionInnerAsync(queries, options, userId, basePath, activityUserId, excludedNodeTypes, c),
            ct);
    }

    /// <summary>Multi-query UNION leaf — per-query param disambiguation + identity dedup wrapper.</summary>
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

        // UNION ALL requires column shape to match across all branches, so the content-skip
        // optimization is all-or-nothing: every query's Select must exclude "content" before
        // the content column can be elided for the whole union.
        var includeContent = queries.Any(q => SelectorAsksFor(q.Select, "content"));

        for (var qi = 0; qi < queries.Count; qi++)
        {
            var (perSql, perParams) = BuildSingleQuerySql(
                queries[qi], options, userId, basePath, activityUserId, excludedNodeTypes, includeContent);
            // Disambiguate param names across the union: rename every sigil-prefixed token
            // referenced in this per-query SQL to {sigil}qI_{name}. A single regex pass keyed on
            // the param-name word boundary (a sequence of string.Replace calls is
            // order-dependent and mangles overlapping names). The ContainsKey gate skips
            // sigil-lookalikes inside literals and the second colon of `::VECTOR` casts. Both
            // '@' and ':' sigils are accepted so the rename is agnostic to the generator's
            // internal key convention.
            var prefix = $"q{qi}_";
            var renamedSql = System.Text.RegularExpressions.Regex.Replace(
                perSql,
                @"[@:]([A-Za-z_]\w*)",
                m => HasGeneratedParam(perParams, m.Groups[1].Value)
                    ? m.Value[0] + prefix + m.Groups[1].Value
                    : m.Value);
            foreach (var (k, v) in perParams)
                unionedParams[prefix + k.TrimStart('@', ':')] = v;
            unionedSelects.Add($"({renamedSql})");
        }

        // Dedup by node identity (namespace, id), newest last_modified winning the tie-break —
        // the QUALIFY / derived-table twin of PG's `SELECT DISTINCT ON (namespace, id) ...
        // ORDER BY namespace, id, last_modified DESC`.
        var unionAllInner = string.Join(" UNION ALL ", unionedSelects);
        const string dedupWindow =
            "ROW_NUMBER() OVER (PARTITION BY \"namespace\", \"id\" ORDER BY \"last_modified\" DESC)";
        var sql = _capabilities.Current.SupportsQualify
            ? $"SELECT * FROM ({unionAllInner}) AS unioned QUALIFY {dedupWindow} = 1"
            : $"SELECT * FROM (SELECT u.*, {dedupWindow} AS \"rn\" FROM ({unionAllInner}) AS u) WHERE \"rn\" = 1";

        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            _logger.LogDebug("UNION SQL ({Count} queries): {Sql}", queries.Count, sql);
            foreach (var (name, value) in unionedParams)
                _logger.LogDebug("  Param {Name} = {Value}", name, value);
        }

        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in unionedParams)
            BindGeneratedParameter(cmd, name, value);

        // Absent schema → undefined-object at open → no rows (see single-query overload).
        DbDataReader? reader;
        try
        {
            reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUndefinedObject(ex))
        {
            yield break;
        }

        await using (reader)
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                yield return SnowflakeMeshNodeReader.ReadMeshNode(reader, options, _logger);
        }
    }

    /// <summary>True when the generator's parameter map carries <paramref name="bareName"/> under any sigil convention.</summary>
    private static bool HasGeneratedParam(Dictionary<string, object> parameters, string bareName)
        => parameters.ContainsKey(bareName)
           || parameters.ContainsKey("@" + bareName)
           || parameters.ContainsKey(":" + bareName);

    /// <summary>
    /// Builds the same SELECT + scope-clause SQL the single-query path executes, returning the
    /// (sql, parameters) pair instead — shared by the multi-query UNION path so per-query SQL
    /// stays bug-compatible with the single-query path. Mirrors PG's <c>BuildSingleQuerySql</c>
    /// including the satellite-redirect and the scope-clause WHERE splice (PG lines 1069-1087).
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
        // Resolve the target table from the query path or nodeType and partition definition.
        // For satellite paths like "User/alice/_Thread", this routes to the "threads" table;
        // nodeType-only queries resolve via the nodeType mapping. When the path resolves to
        // mesh_nodes but nodeType maps to a satellite, the satellite wins (source of truth).
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
        // Satellite table names for source:activity and source:accessed JOINs. Non-partitioned
        // setups store everything in mesh_nodes.
        var activityTable = QualifyTable(_partitionDefinition?.ResolveTableByNodeType("Activity") ?? "mesh_nodes");
        var userActivityTable = QualifyTable(_partitionDefinition?.ResolveTableByNodeType("UserActivity") ?? "mesh_nodes");

        // Fresh generator per call — it has mutable state (param counters) and is NOT
        // thread-safe; capabilities are read lazily per operation.
        var generator = new SnowflakeSqlGenerator(_capabilities.Current) { SchemaName = _schemaName };
        var (sql, parameters) = generator.GenerateSelectQuery(query, userId, activityUserId, tableName,
            activityTable, userActivityTable, excludedNodeTypes, includeContent);
        // An EMPTY-but-set path (`namespace:` → Path == "" + Children) is the "root-level rows
        // only" query — it must still push down `n."namespace" = ''` (mirrors PG).
        if (!string.IsNullOrEmpty(effectivePath)
            || (effectivePath is not null && query.Scope == QueryScope.Children)
            || (query.Paths is { Count: > 1 }))
        {
            // Multi-value `path:a|b|c` push-down → `n.path IN (...)`; single-path queries use
            // the scope-clause generator unchanged.
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
    /// Performs vector similarity search — the PG twin. Reader pump runs in the per-adapter READ
    /// pool via <see cref="ReadPooled{T}"/>; the ILIKE fallback when the endpoint lacks the
    /// VECTOR type comes from the generator.
    /// </summary>
    /// <param name="queryVector">The query embedding.</param>
    /// <param name="options">Serializer options for content deserialization.</param>
    /// <param name="filter">Optional structured filter applied alongside similarity.</param>
    /// <param name="userId">Optional user id for access filtering.</param>
    /// <param name="namespacePath">Optional namespace prefix restriction.</param>
    /// <param name="topK">Maximum result count.</param>
    /// <param name="lexicalTerm">Optional lexical term for hybrid recall.</param>
    /// <param name="ct">Cancellation token.</param>
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

    /// <summary>Vector-search leaf — content-chunk probe + generator + reader loop, mirroring PG.</summary>
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
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);

        // Does this schema carry a content index? If so, the vector search UNIONs each file's
        // best chunk in as a synthetic Document row. Probe + cache (instance, TRUE-only) so a
        // partition that later gains content is picked up; the probe runs in THIS pooled leaf.
        var includeContentChunks = await ContentChunksExistAsync(connection, ct).ConfigureAwait(false);

        var generator = new SnowflakeSqlGenerator(_capabilities.Current) { SchemaName = _schemaName };
        var (sql, parameters) = generator.GenerateVectorSearchQuery(
            filter, queryVector, userId, topK, lexicalTerm,
            namespacePath: string.IsNullOrEmpty(namespacePath) ? null : NormalizePath(namespacePath),
            includeContentChunks: includeContentChunks);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            BindGeneratedParameter(cmd, name, value);

        // Absent schema → undefined-object at open → no rows (see QueryNodesAsync).
        DbDataReader? reader;
        try
        {
            reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUndefinedObject(ex))
        {
            yield break;
        }

        await using (reader)
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                yield return SnowflakeMeshNodeReader.ReadMeshNode(reader, options, _logger);
        }
    }

    /// <summary>
    /// Whether <c>{schema}.content_chunks</c> exists, so the vector search can UNION the
    /// indexed-content branch in. PG probes via <c>to_regclass()</c>; Snowflake has no such
    /// catalog function, so the probe is a zero-row <c>SELECT ... WHERE 1 = 0</c> against the
    /// table, with <see cref="IsUndefinedObject"/> meaning "absent". Cached in the instance
    /// <see cref="_contentChunksExists"/> map: TRUE permanently (a content index is not dropped
    /// under us); FALSE/absent NOT cached so a partition that later gains content is picked up.
    /// A schemaless adapter has no per-schema content table — returns false without a probe.
    /// </summary>
    private async Task<bool> ContentChunksExistAsync(DbConnection connection, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_schemaName))
            return false;
        if (_contentChunksExists.TryGetValue(_schemaName, out var cached))
            return cached;

        bool exists;
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"SELECT 1 FROM {SnowflakeIdentifiers.Qualify(_schemaName, "content_chunks")} WHERE 1 = 0";
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            exists = true;
        }
        catch (Exception ex) when (IsUndefinedObject(ex))
        {
            exists = false;
        }

        // Only cache the positive — leave a negative uncached so a later content gain is seen.
        if (exists)
            _contentChunksExists[_schemaName] = true;
        return exists;
    }

    /// <summary>
    /// Queries nodes across multiple schemas using a single UNION ALL query — one connection,
    /// one round-trip, mirroring PG's cross-schema overload. Reader pump runs in the per-adapter
    /// READ pool via <see cref="ReadPooled{T}"/>.
    /// </summary>
    /// <param name="query">The parsed query.</param>
    /// <param name="options">Serializer options for content deserialization.</param>
    /// <param name="schemas">The partition schemas to union across.</param>
    /// <param name="userId">Optional user id for access filtering.</param>
    /// <param name="ct">Cancellation token.</param>
    public IAsyncEnumerable<MeshNode> QueryNodesAcrossSchemasAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        IReadOnlyList<string> schemas,
        string? userId = null,
        CancellationToken ct = default)
        => schemas.Count == 0
            ? EmptyAsync<MeshNode>()
            : ReadPooled(c => QueryNodesAcrossSchemasInnerAsync(query, options, schemas, userId, c), ct);

    /// <summary>Cross-schema query leaf — generator-produced UNION over the given schemas.</summary>
    private async IAsyncEnumerable<MeshNode> QueryNodesAcrossSchemasInnerAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        IReadOnlyList<string> schemas,
        string? userId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var generator = new SnowflakeSqlGenerator(_capabilities.Current);
        // aclSchemas = all schemas: the adapter has no information_schema probe, so it
        // conservatively enforces the access filter on every branch (over-filtering is safe;
        // SnowflakeCrossSchemaQueryProvider does the real ACL-table probe for public schemas).
        var (sql, parameters) = generator.GenerateCrossSchemaSelectQuery(query, schemas, schemas, userId);

        if (_logger?.IsEnabled(LogLevel.Debug) == true)
            _logger.LogDebug("Cross-schema SQL ({SchemaCount} schemas): {Sql}", schemas.Count, sql);

        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            BindGeneratedParameter(cmd, name, value);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            yield return SnowflakeMeshNodeReader.ReadMeshNode(reader, options, _logger);
    }

    #endregion

    /// <summary>Partition-object key: node path plus optional sub-path.</summary>
    private static string GetPartitionStorageKey(string nodePath, string? subPath)
    {
        var key = NormalizePath(nodePath);
        if (!string.IsNullOrEmpty(subPath))
            key = $"{key}/{NormalizePath(subPath)}";
        return key;
    }

    /// <summary>Reads the object's <c>Id</c>/<c>id</c> property; falls back to a fresh GUID (PG-identical).</summary>
    private static string GetObjectId(object obj)
    {
        var idProp = obj.GetType().GetProperty("Id") ?? obj.GetType().GetProperty("id");
        var id = idProp?.GetValue(obj)?.ToString();
        return id ?? Guid.NewGuid().ToString();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // The connection source is shared and disposed by DI, like PG's data source.
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
