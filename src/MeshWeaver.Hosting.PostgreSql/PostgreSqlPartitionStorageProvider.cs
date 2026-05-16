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
    private readonly ConcurrentDictionary<string, PartitionDefinition> _partitions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _schemasInitialized =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<PostgreSqlPartitionStorageProvider>? _logger;
    private IDisposable? _partitionSubscription;

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

        // Satellite tables if any.
        if (def.TableMappings is { Count: > 0 })
        {
            await PostgreSqlSchemaInitializer.CreateSatelliteTablesAsync(
                ddlDs, schemaOptions, def.TableMappings.Values, ct);
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
        _partitions[def.Namespace] = def;
    }

    /// <summary>
    /// Removes a partition definition (e.g. organization deletion). Existing
    /// partition hubs will idle-expire from the router's 5-minute cache.
    /// </summary>
    public void RemovePartition(string @namespace)
    {
        if (string.IsNullOrEmpty(@namespace)) return;
        _partitions.TryRemove(@namespace, out _);
    }

    /// <inheritdoc/>
    public bool Matches(string fullPath)
    {
        var firstSegment = GetFirstSegment(fullPath);
        return firstSegment != null && _partitions.ContainsKey(firstSegment);
    }

    /// <inheritdoc/>
    public PartitionDefinition? ResolveDefinition(string fullPath)
    {
        var firstSegment = GetFirstSegment(fullPath);
        if (firstSegment == null) return null;
        _partitions.TryGetValue(firstSegment, out var def);
        return def;
    }

    /// <inheritdoc/>
    public IStorageAdapter CreateAdapterForTable(PartitionDefinition def, string table)
    {
        var schema = !string.IsNullOrEmpty(def.Schema) ? def.Schema : def.Namespace;

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
        return new PostgreSqlStorageAdapter(_baseDataSource, _embeddingProvider, def);
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
