using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// <see cref="IPartitionStorageProvider"/> implementation backing Postgres
/// partitions. Replaces the legacy <see cref="PostgreSqlPartitionedStoreFactory"/>
/// shared-pool model with per-(schema, table) adapters that own a tiny
/// <see cref="NpgsqlDataSource"/> with <c>MaxPoolSize=1</c>.
///
/// <para>The partition dictionary is seeded with known
/// <see cref="PartitionDefinition"/>s at construction (e.g. from the mesh
/// builder's <c>WithMeshNodes</c> static seed). A live <c>ObserveQuery</c>
/// subscription can be wired in later to react to <c>Admin/Partition/*</c>
/// node changes at runtime.</para>
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
    // Lazy per-partition cache. Each entry holds a single partition's
    // PartitionDefinition (or null = not a partition) with a TTL so a
    // partition registered / unregistered mid-flight becomes visible
    // without a process restart. NO upfront enumeration — entries are
    // populated on first <see cref="Matches"/> / <see cref="ResolveDefinition"/>
    // call against that partition. This is the "per-partition query, no
    // 'I know all the partitions' business" contract the user requested.
    private readonly ConcurrentDictionary<string, CachedPartitionEntry> _partitionCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan PartitionCacheTtl = TimeSpan.FromMinutes(5);

    private sealed record CachedPartitionEntry(DateTime ExpiresUtc, PartitionDefinition? Definition);

    private readonly ConcurrentDictionary<string, byte> _schemasInitialized =
        new(StringComparer.OrdinalIgnoreCase);
    // Per-schema init Task cache so concurrent first-touch racers join the
    // same in-flight CREATE SCHEMA / CREATE TABLE round instead of stampeding.
    private readonly ConcurrentDictionary<string, Task> _schemaInitTasks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<PostgreSqlPartitionStorageProvider>? _logger;
    private IDisposable? _partitionSubscription;

    /// <summary>
    /// Internal access to the shared base <see cref="NpgsqlDataSource"/> so
    /// the host service that drives schema discovery can issue an
    /// <c>information_schema.tables</c> query against the same Postgres
    /// instance without re-resolving DI (the connection-string overload of
    /// <c>AddPartitionedPostgreSqlPersistence</c> doesn't register the
    /// data source as a DI singleton — it builds one locally and closes
    /// over it).
    /// </summary>
    internal NpgsqlDataSource BaseDataSource => _baseDataSource;

    /// <inheritdoc/>
    public string Name => "Postgres";

    /// <inheritdoc/>
    public ImmutableHashSet<string> Contexts { get; }

    /// <summary>
    /// Constructs a provider over <paramref name="baseDataSource"/> /
    /// <paramref name="baseConnectionString"/>. <paramref name="partitions"/>
    /// seeds the per-namespace dictionary at boot. The standard mesh-builder
    /// extension also wires an <c>ObserveQuery</c> subscription so partitions
    /// added at runtime (e.g. organization creation) become routable without
    /// a restart.
    /// </summary>
    public PostgreSqlPartitionStorageProvider(
        NpgsqlDataSource baseDataSource,
        string baseConnectionString,
        PostgreSqlStorageOptions options,
        IEnumerable<PartitionDefinition>? partitions = null,
        IEmbeddingProvider? embeddingProvider = null,
        Action<NpgsqlDataSourceBuilder>? configureDataSource = null,
        IEnumerable<string>? contexts = null,
        ILogger<PostgreSqlPartitionStorageProvider>? logger = null)
    {
        _baseDataSource = baseDataSource;
        _baseConnectionString = baseConnectionString;
        _options = options;
        _embeddingProvider = embeddingProvider;
        _configureDataSource = configureDataSource;
        _logger = logger;

        Contexts = contexts != null
            ? ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, contexts)
            : ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
                PartitionContexts.Search,
                PartitionContexts.Create,
                PartitionContexts.Autocomplete,
                PartitionContexts.Browse);

        _adapter = new PostgreSqlPathRoutingAdapter(this);

        if (partitions != null)
            foreach (var def in partitions)
                RegisterPartition(def);
    }

    /// <summary>
    /// Subscribes to <c>Admin/Partition/*</c> nodes on the workspace and
    /// registers each emitted <see cref="PartitionDefinition"/>, ensuring the
    /// SQL schema + standard tables exist. Idempotent — repeats CREATE SCHEMA
    /// IF NOT EXISTS without side effects.
    /// <para>Call once after the mesh hub is up. Returns an <see cref="IDisposable"/>
    /// that ends the subscription; the provider also disposes it on
    /// <see cref="Dispose"/>.</para>
    /// </summary>
    public IDisposable SubscribeToWorkspace(IMessageHub mesh)
    {
        var meshService = mesh.ServiceProvider.GetRequiredService<IMeshService>();
        _partitionSubscription = meshService
            .ObserveQuery<MeshNode>(new MeshQueryRequest
            {
                Query = "namespace:Admin/Partition nodeType:Partition",
                Skip = 0,
                Limit = 1000
            })
            .Select(c => c.Items
                .Select(n => n.Content)
                .OfType<PartitionDefinition>()
                .Where(d => !string.IsNullOrEmpty(d.Namespace))
                .ToImmutableList())
            .SelectMany(defs => defs.ToObservable()
                .SelectMany(def => Observable.FromAsync(ct => EnsureSchemaAsync(def, ct))
                    .Catch<PartitionDefinition, Exception>(ex =>
                    {
                        _logger?.LogWarning(ex,
                            "PostgreSqlPartitionStorageProvider: failed to ensure schema for {Namespace}; "
                            + "writes to this partition will not route until the next emission.",
                            def.Namespace);
                        return Observable.Empty<PartitionDefinition>();
                    })))
            .Subscribe(
                def => RegisterPartition(def),
                ex => _logger?.LogError(ex,
                    "PostgreSqlPartitionStorageProvider: Admin/Partition stream failed; "
                    + "no further partition updates will be observed."));
        return _partitionSubscription;
    }

    /// <summary>
    /// Internal entrypoint for callers that pre-register a partition before
    /// the workspace stream emits it (e.g.
    /// <see cref="PostgreSqlPartitionSubscriptionHostedService"/>'s static-provider
    /// seeding pass). Idempotent.
    /// </summary>
    internal Task EnsureSchemaForPartitionAsync(PartitionDefinition def, CancellationToken ct)
        => EnsureSchemaAsync(def, ct);

    private async Task<PartitionDefinition> EnsureSchemaAsync(
        PartitionDefinition def, CancellationToken ct)
    {
        var schema = !string.IsNullOrEmpty(def.Schema) ? def.Schema : def.Namespace;
        if (string.IsNullOrEmpty(schema)) return def;

        // Fast path — already initialized this silo session.
        if (_schemasInitialized.ContainsKey(schema)) return def;

        // CREATE SCHEMA IF NOT EXISTS via the base data source (public search path)
        await using (var cmd = _baseDataSource.CreateCommand(
            $"CREATE SCHEMA IF NOT EXISTS \"{schema}\""))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Create the mesh-tables in this schema (idempotent — `CREATE TABLE IF NOT EXISTS`).
        // Use a tiny per-schema DDL data source so we don't tie up the base pool.
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
        await PostgreSqlSchemaInitializer.InitializeAsync(ddlDs, schemaOptions, ct);

        // Satellite tables: union of the partition's TableMappings (sub-namespace
        // satellites like {ns}/_Access → access) and the partition's own
        // primary table when it's NOT the default mesh_nodes (global-satellite
        // shape: _Access → system_access.access — the satellite IS the
        // partition, no further within-partition routing).
        var satelliteTables = new HashSet<string>(StringComparer.Ordinal);
        if (def.TableMappings is { Count: > 0 })
            foreach (var t in def.TableMappings.Values)
                if (!string.IsNullOrEmpty(t)) satelliteTables.Add(t);
        if (!string.IsNullOrEmpty(def.Table) &&
            !string.Equals(def.Table, "mesh_nodes", StringComparison.Ordinal))
            satelliteTables.Add(def.Table);
        if (satelliteTables.Count > 0)
        {
            await PostgreSqlSchemaInitializer.CreateSatelliteTablesAsync(
                ddlDs, schemaOptions, satelliteTables, ct);
        }

        _schemasInitialized.TryAdd(schema, 0);
        _logger?.LogInformation(
            "PostgreSqlPartitionStorageProvider: schema {Schema} ready for partition {Namespace}",
            schema, def.Namespace);
        return def;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _partitionSubscription?.Dispose();
        _partitionSubscription = null;
    }

    /// <summary>
    /// Adds or updates a partition definition. Idempotent — registering a
    /// namespace twice with different definitions replaces the cached
    /// entry. Called at boot by the mesh-builder seed and at runtime by the
    /// <c>Admin/Partition</c> live-query subscription.
    /// </summary>
    public void RegisterPartition(PartitionDefinition def)
    {
        if (string.IsNullOrEmpty(def.Namespace)) return;
        // Boot / in-code registrations get `DateTime.MaxValue` so the TTL
        // eviction doesn't drop them. (DB-discovered entries use the TTL —
        // those get re-queried on expiry, which lets a partition row deleted
        // out-of-band become unroutable within one TTL window.) The next
        // `RegisterPartition` call with the same key overrides; an explicit
        // `RemovePartition` evicts.
        _partitionCache[def.Namespace] = new CachedPartitionEntry(
            DateTime.MaxValue, def);
    }

    /// <summary>
    /// Removes a cached partition entry so the next lookup re-queries
    /// <c>admin.mesh_nodes</c>. The authoritative state is the DB row;
    /// callers that actually want to UNREGISTER a partition delete the
    /// <c>Admin/Partition/{namespace}</c> row.
    /// </summary>
    public void RemovePartition(string @namespace)
    {
        if (string.IsNullOrEmpty(@namespace)) return;
        _partitionCache.TryRemove(@namespace, out _);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Per-partition lazy lookup against <c>admin.mesh_nodes</c>. No global
    /// "I know all the partitions" cache; each first-segment is queried on
    /// demand, cached for <see cref="PartitionCacheTtl"/>, and re-checked
    /// after expiry. Subsequent partition registrations / removals become
    /// visible within one TTL window without a process restart.
    /// </remarks>
    public bool Matches(string fullPath)
    {
        var firstSegment = GetFirstSegment(fullPath);
        return firstSegment != null && LookupPartition(firstSegment) != null;
    }

    /// <inheritdoc/>
    public PartitionDefinition? ResolveDefinition(string fullPath)
    {
        var firstSegment = GetFirstSegment(fullPath);
        return firstSegment == null ? null : LookupPartition(firstSegment);
    }

    /// <summary>
    /// Per-partition cache lookup with TTL refresh. Cache misses (or expired
    /// entries) issue ONE round-trip to <c>admin.mesh_nodes</c> for the
    /// requested first-segment only — never an enumeration.
    /// </summary>
    private PartitionDefinition? LookupPartition(string firstSegment)
    {
        if (_partitionCache.TryGetValue(firstSegment, out var cached)
            && cached.ExpiresUtc > DateTime.UtcNow)
            return cached.Definition;

        var def = QueryPartitionFromAdmin(firstSegment);
        _partitionCache[firstSegment] = new CachedPartitionEntry(
            DateTime.UtcNow + PartitionCacheTtl,
            def);
        return def;
    }

    /// <summary>
    /// Synchronous single-row lookup against <c>information_schema.schemata</c>:
    /// does a Postgres schema exist for this first-segment? If yes, build a
    /// default <see cref="PartitionDefinition"/> with standard table mappings.
    ///
    /// <para>Schema existence IS the partition's existence — no separate
    /// <c>Admin/Partition</c> registry needed for routing. The Admin/Partition
    /// MeshNode (when it exists) is a UI/catalog concept, not a routing
    /// requirement. Hot path is the cache; this only runs on cold miss.</para>
    /// </summary>
    private PartitionDefinition? QueryPartitionFromAdmin(string firstSegment)
    {
        try
        {
            using var conn = _baseDataSource.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT schema_name FROM information_schema.schemata
                WHERE lower(schema_name) = lower($1)
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue(firstSegment);
            var scalar = cmd.ExecuteScalar();
            System.IO.File.AppendAllText(@"C:\tmp\claude\pg-partition.log",
                $"{DateTime.UtcNow:HH:mm:ss.fff} [QUERY] {firstSegment} → {(scalar?.ToString() ?? "null")}\n");
            if (scalar is not string actualSchema || string.IsNullOrEmpty(actualSchema))
                return null;

            return new PartitionDefinition
            {
                Namespace = firstSegment,
                DataSource = "default",
                Schema = actualSchema,
                Table = "mesh_nodes",
                TableMappings = PartitionDefinition.StandardTableMappings,
                Versioned = true,
            };
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex,
                "PostgreSqlPartitionStorageProvider: lookup for partition '{Partition}' failed " +
                "— treating as unknown (cached for {Ttl})", firstSegment, PartitionCacheTtl);
            return null;
        }
    }

    /// <inheritdoc/>
    public IStorageAdapter CreateAdapterForTable(PartitionDefinition def, string table)
    {
        var schema = !string.IsNullOrEmpty(def.Schema) ? def.Schema : def.Namespace;

        // First-touch schema init for lazy-created partitions (the
        // ResolveDefinition fallback path). Idempotent — already-initialized
        // schemas hit the _schemasInitialized fast path inside EnsureSchemaAsync.
        EnsureSchemaForPartitionSync(def);

        // Per-(schema, table) NpgsqlDataSource: SearchPath scopes to this
        // partition's schema, MaxPoolSize=1 because the hub's actor scheduler
        // serialises every query — one open connection is sufficient.
        var connBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            SearchPath = $"{schema},public",
            MaxPoolSize = 1
        };
        var dsBuilder = new NpgsqlDataSourceBuilder(connBuilder.ConnectionString);
        dsBuilder.UseVector();
        _configureDataSource?.Invoke(dsBuilder);
        var ds = dsBuilder.Build();

        // The inner PostgreSqlStorageAdapter is given a constrained
        // PartitionDefinition where every ResolveTable(...) call returns the
        // single table this hub owns. This collapses the adapter's internal
        // table-routing logic into a no-op while still reusing the SQL builders.
        var tableScopedDef = def with
        {
            Table = table,
            TableMappings = null
        };

        return new PostgreSqlStorageAdapter(ds, _embeddingProvider, tableScopedDef);
    }

    // Inherited default: PartitionDefinition? PartitionDefinition => null;
    // Wildcard provider — partition definitions come from the dict, not a
    // single static property. (Cosmos / SQL discovery uses the same shape.)

    /// <inheritdoc/>
    /// <remarks>
    /// Path-routing facade: every storage operation looks at the path's first
    /// segment, resolves the registered <see cref="PartitionDefinition"/>, and
    /// delegates to a per-schema <see cref="PostgreSqlStorageAdapter"/> bound
    /// to the shared <see cref="NpgsqlDataSource"/>. The per-schema adapters
    /// are cached on first use; <see cref="EnsureSchemaAsync"/> must have run
    /// (via <see cref="SubscribeToWorkspace"/> or a prior bootstrap) before
    /// the first read/write so the schema + tables exist.
    /// </remarks>
    public IStorageAdapter Adapter => _adapter;
    private readonly PostgreSqlPathRoutingAdapter _adapter;

    /// <summary>
    /// Per-schema adapter cache used by <see cref="Adapter"/>. Each entry
    /// reuses the shared base <see cref="NpgsqlDataSource"/> with a
    /// <see cref="PartitionDefinition"/> scoped to the schema; per-table
    /// routing happens inside <see cref="PostgreSqlStorageAdapter"/> via
    /// <see cref="PartitionDefinition.ResolveTable"/>.
    /// </summary>
    internal IStorageAdapter ResolveAdapterForSchema(string firstSegment)
    {
        var def = ResolveDefinition(firstSegment);
        if (def == null)
            throw new InvalidOperationException(
                $"PostgreSqlPartitionStorageProvider: no PartitionDefinition for segment '{firstSegment}'. "
                + "SubscribeToWorkspace's Admin/Partition stream must have registered it before first access.");
        EnsureSchemaForPartitionSync(def);
        return new PostgreSqlStorageAdapter(_baseDataSource, _embeddingProvider, def);
    }

    /// <summary>
    /// Synchronously waits for the schema's first-touch init to complete.
    /// Idempotent — already-initialized schemas return immediately via the
    /// <see cref="_schemasInitialized"/> fast path.
    /// </summary>
    private void EnsureSchemaForPartitionSync(PartitionDefinition def)
    {
        var schema = !string.IsNullOrEmpty(def.Schema) ? def.Schema : def.Namespace;
        if (string.IsNullOrEmpty(schema)) return;
        if (_schemasInitialized.ContainsKey(schema)) return;

        var task = _schemaInitTasks.GetOrAdd(schema,
            _ => EnsureSchemaAsync(def, CancellationToken.None));
        task.GetAwaiter().GetResult();
    }

    private static string? GetFirstSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Trim('/');
        if (normalized.Length == 0) return null;
        var slash = normalized.IndexOf('/');
        return slash < 0 ? normalized : normalized[..slash];
    }
}
