using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Postgres-backed <see cref="IPartitionStorageProvider"/>. Owns a single
/// shared <see cref="NpgsqlDataSource"/>; the actual storage adapter
/// (<see cref="PostgreSqlPathRoutingAdapter"/>) routes per-path to a
/// per-schema <see cref="PostgreSqlStorageAdapter"/>.
///
/// <para><b>Partition discovery is lazy.</b> No upfront enumeration of
/// schemas, no <c>ObserveQuery</c> fan-in of <c>Admin/Partition/*</c>
/// rows. Each first-segment is cached in a per-namespace
/// <see cref="System.Reactive.Subjects.ReplaySubject{T}"/> (held by
/// <see cref="PgPartitionCache"/>) probed on first access; cross-silo
/// invalidation comes from the <c>partition_changes</c> pg_notify channel
/// via <see cref="PgPartitionNotifyListener"/>.</para>
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
    private readonly PgPartitionCache _cache;

    /// <summary>Per-silo memo of "CREATE SCHEMA already ran this session".</summary>
    private readonly ConcurrentDictionary<string, byte> _schemasInitialized =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _schemaInitTasks =
        new(StringComparer.OrdinalIgnoreCase);

    internal NpgsqlDataSource BaseDataSource => _baseDataSource;

    /// <summary>Cache shared with <see cref="PgPartitionNotifyListener"/>.</summary>
    internal PgPartitionCache PartitionCache => _cache;

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
        ILogger<PostgreSqlPartitionStorageProvider>? logger = null)
    {
        _baseDataSource = baseDataSource;
        _baseConnectionString = baseConnectionString;
        _options = options;
        _embeddingProvider = embeddingProvider;
        _configureDataSource = configureDataSource;
        _logger = logger;
        _cache = new PgPartitionCache(baseDataSource, logger);

        Contexts = contexts != null
            ? ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, contexts)
            : ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
                PartitionContexts.Search,
                PartitionContexts.Create,
                PartitionContexts.Autocomplete,
                PartitionContexts.Browse);

        _adapter = new PostgreSqlPathRoutingAdapter(this);

        // Boot-time seed: ANY pre-known partition definition (e.g. one passed
        // by the mesh-builder for system schemas) primes the cache positive
        // so first-touch reads don't pay the probe round-trip. No enumeration
        // — only what the caller hands us.
        if (partitions != null)
            foreach (var def in partitions)
                if (!string.IsNullOrEmpty(def.Namespace))
                    _cache.MarkExists(def.Namespace, def);
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
            await cmd.ExecuteNonQueryAsync(ct);
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
                ddlDs, schemaOptions, extraTables, ct);
        }

        _schemasInitialized.TryAdd(schema, 0);
        _cache.MarkExists(def.Namespace, def);
        _logger?.LogInformation(
            "PostgreSqlPartitionStorageProvider: schema {Schema} ready for partition {Namespace}",
            schema, def.Namespace);
        return def;
    }

    /// <summary>
    /// Public provisioning entry point used by eager-provisioning hooks (e.g. the
    /// Space top-level validator) to create a partition's schema + tables BEFORE the
    /// first read- or write-shaped touch — routed through
    /// <c>public.ensure_partition_schema</c> like every other provisioning path.
    /// Idempotent; the per-silo memo short-circuits repeat calls for the same schema.
    /// </summary>
    public Task EnsurePartitionProvisionedAsync(string @namespace, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(@namespace)) return Task.CompletedTask;
        var def = new PartitionDefinition
        {
            Namespace = @namespace,
            DataSource = Name,
            Schema = @namespace.ToLowerInvariant(),
            Table = "mesh_nodes",
            TableMappings = PartitionDefinition.StandardTableMappings,
            Versioned = true,
        };
        return EnsureSchemaAsync(def, ct);
    }

    /// <summary>
    /// Read-only existence probe (see <see cref="IPartitionStorageProvider.PartitionExistsAsync"/>).
    /// Answers from <see cref="PgPartitionCache"/> — never creates a schema. Maps the
    /// cached <see cref="PartitionState"/> to the tri-state contract:
    /// <list type="bullet">
    ///   <item><see cref="PartitionState.Exists"/> → <c>true</c> (schema present).</item>
    ///   <item><see cref="PartitionState.PendingCreate"/> → <c>false</c> (probe ran,
    ///     <c>information_schema.schemata</c> has no matching schema — the partition
    ///     genuinely does not exist; lazy-create would otherwise materialise it).</item>
    ///   <item><see cref="PartitionState.Absent"/> → <c>null</c> (the probe itself
    ///     failed; indeterminate, so the guard must NOT reject on it).</item>
    /// </list>
    /// The probe resolves the schema by <c>lower(namespace)</c>, which is exact for
    /// user/space partitions (schema == lower(id)); the namespace≠schema system
    /// partitions (the <c>_Access</c>/<c>_Activity</c>/… globals) are exempted by the
    /// guard before it ever calls this, and the named system schemas
    /// (admin/auth/portal/kernel/doc) resolve correctly by lower-case match.
    /// </summary>
    public IObservable<bool?> PartitionExists(string @namespace)
    {
        if (string.IsNullOrEmpty(@namespace)) return Observable.Return<bool?>(null);
        // GetOrProbe is already reactive: it emits the cached state or, on a miss, fires the
        // information_schema probe (Observable.FromAsync inside PgPartitionCache) and emits when
        // it lands. The async I/O therefore stays at the IO boundary; we just project the state.
        return _cache.GetOrProbe(@namespace)
            .Take(1)
            .Select(state => state switch
            {
                PartitionState.Exists => (bool?)true,
                PartitionState.PendingCreate => false,
                _ => (bool?)null,
            });
    }

    /// <summary>
    /// Tests / boot-time callers can pre-prime the cache with a known
    /// <see cref="PartitionDefinition"/>, skipping the
    /// <c>information_schema.schemata</c> probe. No-op when the namespace
    /// is empty.
    /// </summary>
    public void RegisterPartition(PartitionDefinition def)
    {
        if (string.IsNullOrEmpty(def.Namespace)) return;
        _cache.MarkExists(def.Namespace, def);
    }

    /// <summary>
    /// Drops the cache entry for <paramref name="namespace"/>. Used by tests
    /// that simulate partition deletion; production uses
    /// <see cref="PgPartitionNotifyListener"/> driven by the
    /// <c>partition_changes</c> pg_notify channel.
    /// </summary>
    public void RemovePartition(string @namespace)
    {
        if (string.IsNullOrEmpty(@namespace)) return;
        _cache.Invalidate(@namespace);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _cache.Dispose();
    }

    /// <inheritdoc/>
    public IStorageAdapter CreateAdapterForTable(PartitionDefinition def, string table)
    {
        var schema = !string.IsNullOrEmpty(def.Schema) ? def.Schema : def.Namespace;

        EnsureSchemaForPartitionSync(def);

        var connBuilder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            SearchPath = $"{schema},public",
            MaxPoolSize = 1
        };
        var dsBuilder = new NpgsqlDataSourceBuilder(connBuilder.ConnectionString);
        dsBuilder.UseVector();
        _configureDataSource?.Invoke(dsBuilder);
        var ds = dsBuilder.Build();

        var tableScopedDef = def with
        {
            Table = table,
            TableMappings = null
        };

        return new PostgreSqlStorageAdapter(ds, _embeddingProvider, tableScopedDef);
    }

    /// <inheritdoc/>
    public IStorageAdapter Adapter => _adapter;
    private readonly PostgreSqlPathRoutingAdapter _adapter;

    /// <summary>
    /// Synchronously ensures the schema for <paramref name="def"/> exists.
    /// Used by <see cref="PostgreSqlPathRoutingAdapter"/> on the lazy-create
    /// path. Concurrent first-touches share the same in-flight task via
    /// <see cref="_schemaInitTasks"/> so duplicate CREATE SCHEMAs don't race.
    /// </summary>
    internal void EnsureSchemaForPartitionSync(PartitionDefinition def)
    {
        var schema = !string.IsNullOrEmpty(def.Schema) ? def.Schema : def.Namespace;
        if (string.IsNullOrEmpty(schema)) return;
        if (_schemasInitialized.ContainsKey(schema)) return;

        var task = _schemaInitTasks.GetOrAdd(schema,
            _ => EnsureSchemaAsync(def, CancellationToken.None));
        task.GetAwaiter().GetResult();
    }

    internal static string? GetFirstSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Trim('/');
        if (normalized.Length == 0) return null;
        var slash = normalized.IndexOf('/');
        return slash < 0 ? normalized : normalized[..slash];
    }
}
