using System.Collections.Concurrent;
using System.Collections.Immutable;
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

        await using (var cmd = _baseDataSource.CreateCommand(
            $"CREATE SCHEMA IF NOT EXISTS \"{schema}\""))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

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
        _cache.MarkExists(def.Namespace, def);
        _logger?.LogInformation(
            "PostgreSqlPartitionStorageProvider: schema {Schema} ready for partition {Namespace}",
            schema, def.Namespace);
        return def;
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
