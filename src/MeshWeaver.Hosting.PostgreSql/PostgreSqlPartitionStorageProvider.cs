using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Postgres-backed <see cref="IPartitionStorageProvider"/>. Owns a single
/// shared <see cref="NpgsqlDataSource"/>; the actual storage adapter
/// (<see cref="PostgreSqlPathRoutingAdapter"/>) routes per-path to a
/// per-schema <see cref="PostgreSqlStorageAdapter"/>.
///
/// <para><b>No partition discovery, no existence probe, no lazy create.</b> The router maps
/// a path's first segment to a schema <i>synchronously</i> (<c>seg.ToLowerInvariant()</c>)
/// — no <c>information_schema</c> probe, no async cache. Schema creation is eager and gated to
/// partition-owning creates (<c>OwnsPartitionProvisioningValidator</c> →
/// <see cref="EnsurePartitionProvisioned"/>); the router itself NEVER creates a schema. A write
/// to an unprovisioned partition faults <c>42P01</c> ("no partition, no write"); reads tolerate
/// an absent schema (the per-schema adapter catches Postgres <c>42P01</c> → empty). The
/// <c>_</c>-prefix global-satellite namespaces (whose schema name differs from the
/// namespace) are resolved from the registered-partition map seeded at boot by the
/// static-partition providers.</para>
///
/// <para>See <c>Doc/Architecture/PartitionStorageHubs.md</c>.</para>
/// </summary>
public sealed class PostgreSqlPartitionStorageProvider : IPartitionStorageProvider, IDisposable
{
    private readonly NpgsqlDataSource _baseDataSource;
    private readonly string _baseConnectionString;
    private readonly PostgreSqlStorageOptions _options;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly Action<NpgsqlDataSourceBuilder>? _configureDataSource;
    private readonly ILogger<PostgreSqlPartitionStorageProvider>? _logger;
    // Registered partition definitions, keyed by namespace (first segment),
    // case-insensitive. Seeded by the boot-time static-partition provider, by
    // EnsureSchemaAsync, and by explicit RegisterPartition calls. The router
    // consults this ONLY to resolve `_`-prefix global-satellite namespaces
    // (whose schema name differs from the lowercased namespace) and to reuse a
    // richer PartitionDefinition when one was registered; ordinary partitions
    // resolve synchronously to `seg.ToLowerInvariant()` without it. Instance
    // field (never static) so its lifetime is the mesh's.
    private readonly ConcurrentDictionary<string, PartitionDefinition> _registeredPartitions =
        new(StringComparer.OrdinalIgnoreCase);
    // One read-concurrency gate shared by every adapter this provider creates —
    // they all share the single base connection pool, so the gate must be shared
    // too. Bounds read fan-out below the pool size (leaves write headroom).
    private readonly ReadConcurrencyGate _readGate;

    /// <summary>Per-silo memo of "CREATE SCHEMA already ran this session".</summary>
    private readonly ConcurrentDictionary<string, byte> _schemasInitialized =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Promise-cache of in-flight / completed partition provisioning, keyed by schema.
    /// The CREATE SCHEMA round-trip runs at most once per (silo, schema): <see cref="IoPoolExtensions.Run"/>
    /// is eager + <see cref="System.Reactive.Subjects.ReplaySubject{T}"/>-backed, so the first caller
    /// kicks the DDL off on the per-adapter pool and every later subscriber replays the cached
    /// completion. Instance field (never static) so its lifetime is the mesh's.
    /// </summary>
    private readonly ConcurrentDictionary<string, IObservable<Unit>> _provisioned =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-adapter I/O pool, capped at one in-flight op so the gate mirrors this adapter's
    /// single Npgsql connection — the gate IS the connection ("hook into the pg pool").
    /// The async DB edge is sealed inside <see cref="IIoPool"/>; there is NO
    /// <c>Observable.FromAsync</c> at any call site (forbidden — see ControlledIoPooling.md).
    /// </summary>
    private readonly IIoPool _ioPool;

    internal NpgsqlDataSource BaseDataSource => _baseDataSource;

    /// <summary>Shared per-adapter read-concurrency gate (see <see cref="ReadConcurrencyGate"/>).</summary>
    internal ReadConcurrencyGate ReadGate => _readGate;

    /// <summary>
    /// Synchronous lookup of a registered <see cref="PartitionDefinition"/> by
    /// namespace (first segment). Used by <see cref="PostgreSqlPathRoutingAdapter"/>
    /// to resolve <c>_</c>-prefix global-satellite namespaces to their real schema
    /// (e.g. <c>_Access</c> → <c>system_access</c>) and to reuse a registered def
    /// for an ordinary partition. No DB round-trip.
    /// </summary>
    internal bool TryGetRegisteredPartition(string @namespace, out PartitionDefinition def)
        => _registeredPartitions.TryGetValue(@namespace, out def!);

    /// <inheritdoc/>
    public string Name => "Postgres";

    /// <inheritdoc/>
    public bool IsReadOnly => false;

    /// <inheritdoc/>
    public ImmutableHashSet<string> Contexts { get; }

    public PostgreSqlPartitionStorageProvider(
        NpgsqlDataSource baseDataSource,
        string baseConnectionString,
        PostgreSqlStorageOptions options,
        IEnumerable<PartitionDefinition>? partitions = null,
        IEmbeddingProvider? embeddingProvider = null,
        Action<NpgsqlDataSourceBuilder>? configureDataSource = null,
        IEnumerable<string>? contexts = null,
        ILogger<PostgreSqlPartitionStorageProvider>? logger = null,
        IoPoolRegistry? ioPoolRegistry = null)
    {
        _baseDataSource = baseDataSource;
        _baseConnectionString = baseConnectionString;
        _options = options;
        _embeddingProvider = embeddingProvider;
        _configureDataSource = configureDataSource;
        // Per-adapter pool (cap 1 — one connection). Falls back to the unbounded pool only
        // when constructed outside DI (tests) — still off the hub scheduler, never FromAsync.
        _ioPool = ioPoolRegistry?.Get($"{IoPoolNames.PostgresAdapterPrefix}{Name}") ?? IoPool.Unbounded;
        _logger = logger;
        _readGate = new ReadConcurrencyGate(options.MaxReadConcurrency);

        Contexts = contexts != null
            ? ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, contexts)
            : ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
                PartitionContexts.Search,
                PartitionContexts.Create,
                PartitionContexts.Autocomplete,
                PartitionContexts.Browse);

        _adapter = new PostgreSqlPathRoutingAdapter(this);

        // Boot-time seed: ANY pre-known partition definition (e.g. one passed
        // by the mesh-builder for system schemas) is registered so the router
        // can resolve its real schema (notably the `_`-prefix globals whose
        // schema ≠ lowercased namespace). No enumeration — only what the caller
        // hands us.
        if (partitions != null)
            foreach (var def in partitions)
                if (!string.IsNullOrEmpty(def.Namespace))
                    _registeredPartitions[def.Namespace] = def;
    }

    /// <summary>
    /// Idempotent CREATE SCHEMA + standard-tables init for one partition. Hot
    /// path: in-process memo (<see cref="_schemasInitialized"/>) returns
    /// immediately after the first call per (silo, schema).
    /// </summary>
    internal Task EnsureSchemaForPartitionAsync(PartitionDefinition def, CancellationToken ct)
        => EnsureSchemaAsync(def, ct);

    private async Task<PartitionDefinition> EnsureSchemaAsync(
        PartitionDefinition def, CancellationToken ct)
    {
        var schema = !string.IsNullOrEmpty(def.Schema) ? def.Schema : def.Namespace;
        if (string.IsNullOrEmpty(schema)) return def;

        if (_schemasInitialized.ContainsKey(schema)) return def;

        // Single source of truth for per-partition DDL: the public.ensure_partition_schema
        // stored proc (installed by PostgreSqlSchemaInitializer.InitializeAsync on the
        // public schema + the V29 migration). It idempotently creates the schema +
        // {schema}.mesh_nodes + every StandardTableMappings satellite + the permission
        // rebuild functions + notify/mirror/history triggers — byte-faithful to the C#
        // GetVersionedPartitionDdl/GetSatelliteTableScript bodies the proc embeds.
        // The base data source has UseVector() so the proc's vector(dim) columns resolve.
        await using (var cmd = _baseDataSource.CreateCommand("SELECT public.ensure_partition_schema(@partition)"))
        {
            cmd.Parameters.AddWithValue("partition", schema);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Non-standard satellite tables: the proc only provisions the standard set
        // (PartitionDefinition.StandardTableMappings). If this def carries a bespoke
        // Table or TableMappings entry outside that set, create it too so routing to it
        // doesn't hit 42P01. The common partition-root case (StandardTableMappings +
        // Table="mesh_nodes") finds nothing extra here and skips the per-schema datasource.
        var standard = new HashSet<string>(
            PartitionDefinition.StandardTableMappings.Values, StringComparer.Ordinal);
        var extraTables = new HashSet<string>(StringComparer.Ordinal);
        if (def.TableMappings is { Count: > 0 })
            foreach (var t in def.TableMappings.Values)
                if (!string.IsNullOrEmpty(t) && !standard.Contains(t)) extraTables.Add(t);
        if (!string.IsNullOrEmpty(def.Table) &&
            !string.Equals(def.Table, "mesh_nodes", StringComparison.Ordinal) &&
            !standard.Contains(def.Table))
            extraTables.Add(def.Table);
        if (extraTables.Count > 0)
        {
            var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
            {
                SearchPath = $"{schema},public",
                MaxPoolSize = 2,
                ConnectionIdleLifetime = 10
            };
            var dsBuilder = new NpgsqlDataSourceBuilder(builder.ConnectionString);
            dsBuilder.UseVector();
            _configureDataSource?.Invoke(dsBuilder);
            await using var ddlDs = dsBuilder.Build();
            var schemaOptions = new PostgreSqlStorageOptions
            {
                ConnectionString = builder.ConnectionString,
                VectorDimensions = _options.VectorDimensions,
                Schema = schema
            };
            await PostgreSqlSchemaInitializer.CreateSatelliteTablesAsync(
                ddlDs, schemaOptions, extraTables, ct).ConfigureAwait(false);
        }

        _schemasInitialized.TryAdd(schema, 0);
        if (!string.IsNullOrEmpty(def.Namespace))
            _registeredPartitions[def.Namespace] = def;
        _logger?.LogInformation(
            "PostgreSqlPartitionStorageProvider: schema {Schema} ready for partition {Namespace}",
            schema, def.Namespace);
        return def;
    }

    /// <summary>
    /// The reactive provisioning entry point — the ONE path that creates a partition's
    /// schema + tables, driven by <c>OwnsPartitionProvisioningValidator</c> when a
    /// top-level <c>User</c>/<c>Space</c> is created. Routed through
    /// <c>public.ensure_partition_schema</c>. Idempotent; the per-silo memo short-circuits
    /// repeat calls for the same schema. Emits once and completes.
    ///
    /// <para>The storage router no longer lazily creates schemas on arbitrary writes, so a
    /// write whose partition was never provisioned here fails loudly (42P01) instead of
    /// conjuring a ghost schema. See <c>Doc/Architecture/PartitionStorageRouting.md</c>.</para>
    /// </summary>
    public IObservable<Unit> EnsurePartitionProvisioned(string @namespace)
    {
        if (string.IsNullOrEmpty(@namespace))
            return Observable.Return(Unit.Default);
        var def = new PartitionDefinition
        {
            Namespace = @namespace,
            DataSource = Name,
            Schema = @namespace.ToLowerInvariant(),
            Table = "mesh_nodes",
            TableMappings = PartitionDefinition.StandardTableMappings,
            Versioned = true,
        };
        // Promise-cache: provision each schema at most once. IIoPool.Run pushes the
        // CREATE SCHEMA onto the per-adapter pool (cap 1 = one connection) and replays the
        // single completion to every subscriber. NO Observable.FromAsync here — the only
        // async edge lives sealed inside IIoPool (forbidden everywhere else; see
        // ControlledIoPooling.md).
        return _provisioned.GetOrAdd(def.Schema!, _ =>
            _ioPool.Run(ct => EnsureSchemaAsync(def, ct)).Select(_ => Unit.Default));
    }

    /// <summary>
    /// Read-only existence probe (see <see cref="IPartitionStorageProvider.PartitionExists"/>).
    /// NEVER creates a schema. Tri-state contract:
    /// <list type="bullet">
    ///   <item><c>true</c> — the schema exists (registered/initialised this session,
    ///     or found in <c>information_schema.schemata</c>).</item>
    ///   <item><c>false</c> — the probe ran and no matching schema exists; the
    ///     partition genuinely does not exist (this is what lets the
    ///     <c>PartitionWriteGuardValidator</c> reject implicit space creation).</item>
    ///   <item><c>null</c> — the probe itself failed; indeterminate, so the guard
    ///     must NOT reject on it.</item>
    /// </list>
    /// Fast positive: a namespace we've already registered or CREATE'd this session
    /// answers <c>true</c> with no round-trip. Otherwise a single read-only
    /// <c>information_schema.schemata</c> query at the reactive leaf
    /// (<see cref="Observable.FromAsync{TResult}(System.Func{System.Threading.CancellationToken, System.Threading.Tasks.Task{TResult}})"/>) —
    /// this is a genuine existence query, NOT partition routing, so the leaf async
    /// I/O is the sanctioned reactive boundary. The schema resolves by
    /// <c>lower(namespace)</c>, exact for user/space partitions (schema == lower(id));
    /// the namespace≠schema <c>_</c>-prefix globals are exempted by the guard before
    /// it ever calls this, and the named system schemas (admin/auth/portal/kernel/doc)
    /// resolve correctly by lower-case match.
    /// </summary>
    public IObservable<bool?> PartitionExists(string @namespace)
    {
        if (string.IsNullOrEmpty(@namespace)) return Observable.Return<bool?>(null);

        var schema = _registeredPartitions.TryGetValue(@namespace, out var def)
                     && !string.IsNullOrEmpty(def.Schema)
            ? def.Schema!
            : @namespace.ToLowerInvariant();

        if (_registeredPartitions.ContainsKey(@namespace) || _schemasInitialized.ContainsKey(schema))
            return Observable.Return<bool?>(true);

        return Observable.FromAsync<bool?>(async ct =>
        {
            await using var cmd = _baseDataSource.CreateCommand("""
                SELECT 1 FROM information_schema.schemata
                WHERE lower(schema_name) = lower($1)
                LIMIT 1
                """);
            cmd.Parameters.AddWithValue(schema);
            var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return scalar is not null;
        })
        .Catch<bool?, Exception>(ex =>
        {
            _logger?.LogDebug(ex,
                "PartitionExists: schema probe for '{Namespace}' (schema '{Schema}') failed; emitting null (indeterminate).",
                @namespace, schema);
            return Observable.Return<bool?>(null);
        });
    }

    /// <summary>
    /// Tests / boot-time callers can pre-register a known
    /// <see cref="PartitionDefinition"/> so the router resolves its real schema
    /// (notably the <c>_</c>-prefix globals whose schema ≠ lowercased namespace)
    /// and <see cref="PartitionExists"/> answers <c>true</c> without a probe.
    /// No-op when the namespace is empty.
    /// </summary>
    public void RegisterPartition(PartitionDefinition def)
    {
        if (string.IsNullOrEmpty(def.Namespace)) return;
        _registeredPartitions[def.Namespace] = def;
    }

    /// <summary>
    /// Drops the registered definition for <paramref name="namespace"/>. Used by
    /// tests that simulate partition deletion. Does NOT drop the
    /// <c>_schemasInitialized</c> memo or the underlying schema — it only forgets
    /// the registered def so the router falls back to the synchronous
    /// <c>seg.ToLowerInvariant()</c> mapping (and `_`-prefix globals become
    /// unroutable again).
    /// </summary>
    public void RemovePartition(string @namespace)
    {
        if (string.IsNullOrEmpty(@namespace)) return;
        _registeredPartitions.TryRemove(@namespace, out _);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _readGate.Dispose();
    }

    /// <inheritdoc/>
    public IStorageAdapter CreateAdapterForTable(PartitionDefinition def, string table)
    {
        // No lazy CREATE SCHEMA here: per-(schema,table) hubs only spin up for partitions
        // that were already provisioned (OwnsPartitionProvisioningValidator). An adapter for
        // an absent schema tolerates 42P01 on read (→ empty) and faults loudly on write —
        // it never conjures a ghost schema.
        //
        // Reuse the single shared base data source. PostgreSqlStorageAdapter
        // schema-qualifies every statement ("schema"."table") from the def
        // (see QualifyTable), so a per-(schema,table) connection pool /
        // search_path is unnecessary.
        //
        // 🚨 Leak fix: this used to `dsBuilder.Build()` a fresh NpgsqlDataSource
        // on every call — and the adapter's DisposeAsync is a deliberate no-op
        // ("DataSource is shared and disposed elsewhere"), so nobody ever
        // disposed these per-table pools. Each (schema, table) storage hub that
        // spun up leaked one pool holding a live server connection; the sprawl
        // exhausted the server's connection slots and starved the base pool
        // ("connection pool has been exhausted, currently 50" on onboarding).
        // One shared pool per silo instead.
        var tableScopedDef = def with
        {
            Table = table,
            TableMappings = null
        };

        return new PostgreSqlStorageAdapter(_baseDataSource, _embeddingProvider, tableScopedDef, readGate: _readGate);
    }

    /// <inheritdoc/>
    public IStorageAdapter Adapter => _adapter;
    private readonly PostgreSqlPathRoutingAdapter _adapter;

    internal static string? GetFirstSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Trim('/');
        if (normalized.Length == 0) return null;
        var slash = normalized.IndexOf('/');
        return slash < 0 ? normalized : normalized[..slash];
    }
}
