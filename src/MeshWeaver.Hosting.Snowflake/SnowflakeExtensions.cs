using MeshWeaver.Hosting.Embeddings;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// Factory for creating <see cref="SnowflakeStorageAdapter"/> instances from configuration,
/// mirroring <c>PostgreSqlStorageAdapterFactory</c>.
/// </summary>
public class SnowflakeStorageAdapterFactory(
    IOptions<SnowflakeStorageOptions> options) : IStorageAdapterFactory
{
    /// <summary>The storage-type key under which this factory is registered.</summary>
    public const string StorageType = "Snowflake";

    private SnowflakeConnectionSource? _cachedSource;

    /// <inheritdoc />
    public IStorageAdapter Create(GraphStorageConfig config, IServiceProvider serviceProvider)
    {
        var source = serviceProvider.GetService<SnowflakeConnectionSource>();
        if (source == null)
        {
            // Cache the source so multiple Create() calls share the driver's session pool.
            if (_cachedSource == null)
            {
                var opts = options.Value;
                var connectionString = opts.ConnectionString
                    ?? config.ConnectionString
                    ?? throw new InvalidOperationException(
                        "Snowflake connection string not configured. " +
                        "Set SnowflakeStorageOptions.ConnectionString or Graph:Storage:ConnectionString.");
                _cachedSource = new SnowflakeConnectionSource(connectionString);
            }
            source = _cachedSource;
        }

        var embeddingProvider = serviceProvider.GetService<IEmbeddingProvider>();
        return new SnowflakeStorageAdapter(
            source,
            embeddingProvider,
            capabilities: serviceProvider.GetService<SnowflakeCapabilityHolder>(),
            options: options.Value);
    }
}

/// <summary>
/// Extension methods for configuring Snowflake persistence — the Snowflake twin of
/// <c>PostgreSqlExtensions</c>. Registration shapes mirror the PG backend so a portal
/// can swap backends by swapping the one <c>AddPartitioned*Persistence</c> call.
/// </summary>
public static class SnowflakeExtensions
{
    /// <summary>
    /// Registers an embedding provider from an <see cref="EmbeddingOptions"/> instance and
    /// syncs <see cref="SnowflakeStorageOptions.VectorDimensions"/> — the Snowflake twin of
    /// the PG <c>AddEmbeddings</c>.
    /// </summary>
    public static IServiceCollection AddSnowflakeEmbeddings(
        this IServiceCollection services, EmbeddingOptions options)
    {
        if (services.TryAddEmbeddingProvider(options))
            services.Configure<SnowflakeStorageOptions>(o => o.VectorDimensions = options.Dimensions);
        return services;
    }

    /// <summary>
    /// Registers the Snowflake storage adapter factory for use with <c>AddPersistenceFromConfig</c>.
    /// Also registers <see cref="SnowflakeMeshQuery"/> for native SQL queries (same instance under
    /// <see cref="IVectorSearchProvider"/>), mirroring <c>AddPostgreSqlStorageFactory</c>.
    /// </summary>
    public static IServiceCollection AddSnowflakeStorageFactory(
        this IServiceCollection services, Action<SnowflakeStorageOptions>? configure = null)
    {
        if (configure != null)
            services.Configure(configure);
        services.TryAddSingleton<SnowflakeCapabilityHolder>();
        services.AddKeyedSingleton<IStorageAdapterFactory, SnowflakeStorageAdapterFactory>(
            SnowflakeStorageAdapterFactory.StorageType);

        services.AddSingleton<SnowflakeMeshQuery>(sp =>
        {
            var adapter = sp.GetRequiredService<IStorageAdapter>() as SnowflakeStorageAdapter
                ?? throw new InvalidOperationException(
                    "SnowflakeMeshQuery requires SnowflakeStorageAdapter.");
            return new SnowflakeMeshQuery(
                adapter,
                sp.GetService<AccessService>(),
                meshConfiguration: null,
                excludedNamespaces: null,
                embeddingProvider: sp.GetService<IEmbeddingProvider>(),
                ioPoolRegistry: sp.GetService<IoPoolRegistry>());
        });
        services.AddSingleton<IMeshQueryProvider>(sp => sp.GetRequiredService<SnowflakeMeshQuery>());
        services.AddSingleton<IVectorSearchProvider>(sp => sp.GetRequiredService<SnowflakeMeshQuery>());
        return services;
    }

    /// <summary>
    /// Adds single-schema Snowflake persistence (the twin of <c>AddPostgreSqlPersistence</c>):
    /// one adapter over the configured schema, native query provider, access control.
    /// In-process change notifications are always published from Write/Delete; for
    /// cross-process propagation use <see cref="AddPartitionedSnowflakePersistence"/>.
    /// </summary>
    public static IServiceCollection AddSnowflakePersistence(
        this IServiceCollection services,
        string connectionString,
        Action<SnowflakeStorageOptions>? configure = null)
    {
        var opts = new SnowflakeStorageOptions { ConnectionString = connectionString };
        configure?.Invoke(opts);

        var source = new SnowflakeConnectionSource(connectionString);
        services.AddSingleton(source);
        var capabilities = new SnowflakeCapabilityHolder();
        services.AddSingleton(capabilities);

        // Mirrors the PG simple overload's eager-instance shape (incl. its acknowledged
        // BuildServiceProvider wart) — this overload serves tests/monolith bring-up where
        // the embedding provider is registered before persistence.
        var embeddingProvider = services.BuildServiceProvider().GetService<IEmbeddingProvider>();
        var storageAdapter = new SnowflakeStorageAdapter(
            source, embeddingProvider, capabilities: capabilities, options: opts);
        services.AddSingleton(storageAdapter);

        services.AddSingleton<SnowflakeMeshQuery>(sp =>
            new SnowflakeMeshQuery(
                storageAdapter,
                sp.GetService<AccessService>(),
                meshConfiguration: null,
                excludedNamespaces: null,
                embeddingProvider: embeddingProvider,
                ioPoolRegistry: sp.GetService<IoPoolRegistry>()));
        services.AddSingleton<IMeshQueryProvider>(sp => sp.GetRequiredService<SnowflakeMeshQuery>());
        services.AddSingleton<IVectorSearchProvider>(sp => sp.GetRequiredService<SnowflakeMeshQuery>());

        services.AddPersistence(storageAdapter);

        services.TryAddSingleton(new SnowflakeAccessControl(
            source, schemaName: opts.Schema, centralSchema: opts.Schema,
            capabilities: capabilities));

        return services;
    }

    /// <summary>
    /// Adds partitioned Snowflake persistence where each top-level path segment gets its own
    /// Snowflake schema with isolated tables — the twin of <c>AddPartitionedPostgreSqlPersistence</c>.
    /// Also installs the durable event log (events schema) and the cross-process change-feed
    /// poller (Snowflake's replacement for pg_notify).
    /// </summary>
    public static IServiceCollection AddPartitionedSnowflakePersistence(
        this IServiceCollection services,
        string connectionString,
        Action<SnowflakeStorageOptions>? configure = null)
    {
        var opts = new SnowflakeStorageOptions { ConnectionString = connectionString };
        configure?.Invoke(opts);
        services.AddSingleton(Options.Create(opts));

        // Factory registration → the container creates (and therefore disposes) the source,
        // closing the driver's session pool with the mesh — the PG leak-fix lesson.
        services.AddSingleton<SnowflakeConnectionSource>(_ =>
            new SnowflakeConnectionSource(connectionString));
        services.AddSingleton<SnowflakeOriginId>();
        services.AddSingleton<SnowflakeCapabilityHolder>();

        services.AddSingleton<SnowflakePartitionStorageProvider>(sp =>
            new SnowflakePartitionStorageProvider(
                sp.GetRequiredService<SnowflakeConnectionSource>(),
                opts,
                partitions: null,
                sp.GetService<IEmbeddingProvider>(),
                contexts: null,
                sp.GetService<ILogger<SnowflakePartitionStorageProvider>>(),
                sp.GetService<IoPoolRegistry>(),
                sp.GetRequiredService<SnowflakeCapabilityHolder>()));
        services.AddSingleton<IPartitionStorageProvider>(sp =>
            sp.GetRequiredService<SnowflakePartitionStorageProvider>());

        // Durable event-log store (events schema) — replaces the in-memory default so the
        // app-level outbox survives restarts AND feeds the cross-process change-feed poller.
        services.AddSingleton<SnowflakeEventLogStore>(sp =>
            new SnowflakeEventLogStore(
                sp.GetRequiredService<SnowflakeConnectionSource>(),
                sp.GetRequiredService<SnowflakeOriginId>(),
                opts,
                sp.GetService<IoPoolRegistry>()));
        services.Replace(ServiceDescriptor.Singleton<IEventLogStore>(sp =>
            sp.GetRequiredService<SnowflakeEventLogStore>()));

        // Cross-schema query provider — C#-generated UNION fan-out (no stored proc in Snowflake).
        services.AddSingleton<ICrossSchemaQueryProvider>(sp =>
            new SnowflakeCrossSchemaQueryProvider(
                sp.GetRequiredService<SnowflakeConnectionSource>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<SnowflakeCrossSchemaQueryProvider>(),
                opts));

        // Fan-out IMeshQueryProvider — unscoped + wildcard-namespace queries route through
        // the cross-schema UNION; scoped queries fall through per schema.
        services.AddSingleton<IMeshQueryProvider>(sp =>
            new SnowflakePartitionedMeshQuery(
                sp.GetRequiredService<ICrossSchemaQueryProvider>(),
                sp.GetService<AccessService>(),
                sp.GetService<ILogger<SnowflakePartitionedMeshQuery>>(),
                sp.GetRequiredService<SnowflakePartitionStorageProvider>(),
                sp.GetService<IoPoolRegistry>(),
                sp.GetService<MeshConfiguration>()));

        // top_level_index rebuild routine — invoked at init + partition provision/drop.
        services.AddSingleton<SnowflakeSearchInfrastructure>(sp =>
            new SnowflakeSearchInfrastructure(
                sp.GetRequiredService<SnowflakeConnectionSource>(),
                opts,
                sp.GetService<ILogger<SnowflakeSearchInfrastructure>>(),
                sp.GetService<IoPoolRegistry>()));

        // Cross-process change feed: Snowflake has no LISTEN/NOTIFY, so a poller tails the
        // event log and injects foreign silos' events into the routing adapter's merged
        // Changes feed. In-process changes publish synchronously from Write/Delete regardless.
        services.AddSingleton<SnowflakeChangeFeedPoller>(sp =>
        {
            var provider = sp.GetRequiredService<SnowflakePartitionStorageProvider>();
            var poller = new SnowflakeChangeFeedPoller(
                sp.GetRequiredService<SnowflakeEventLogStore>(),
                sp.GetRequiredService<SnowflakeOriginId>(),
                opts,
                sp.GetService<ILogger<SnowflakeChangeFeedPoller>>());
            if (provider.Adapter is SnowflakePathRoutingAdapter routing)
                poller.Attach(routing.ChangeObserver);
            return poller;
        });
        services.AddHostedService<SnowflakeChangeFeedPollerHostedService>();

        // Boot-time seed: CREATE SCHEMA + table init for every framework partition
        // advertised by a static node provider.
        services.AddHostedService<SnowflakePartitionSubscriptionHostedService>();

        // Same #20 rationale as PG: the fan-out provider serves unscoped + satellite queries
        // natively, so the pedestrian walk defers to it.
        services.AddSingleton(new StorageAdapterQueryProviderOptions
        {
            DeferToNativeProvider = true
        });
        services.AddPartitionedCoreAndWrapperServices();

        return services;
    }

    /// <summary>
    /// Initializes the Snowflake schema (central tables, events schema, capability probe,
    /// node-type permissions, top-level index) — the twin of <c>InitializePostgreSqlSchemaAsync</c>.
    /// Call during application startup, before the mesh serves traffic.
    /// </summary>
    public static async Task InitializeSnowflakeSchemaAsync(
        this IServiceProvider serviceProvider,
        CancellationToken ct = default)
    {
        var source = serviceProvider.GetService<SnowflakeConnectionSource>()
            ?? (serviceProvider.GetService<IStorageAdapter>() as SnowflakeStorageAdapter)?.Source
            ?? throw new InvalidOperationException(
                "No SnowflakeConnectionSource found. Register via AddSnowflakePersistence or AddPartitionedSnowflakePersistence.");

        var options = serviceProvider.GetService<IOptions<SnowflakeStorageOptions>>()?.Value
            ?? new SnowflakeStorageOptions();
        var logger = serviceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(SnowflakeExtensions));

        // Probe what the endpoint actually supports (the LocalStack emulator lacks features
        // real Snowflake has); EnableVectorType, when set, overrides the vector probe.
        var probed = await SnowflakeCapabilityProbe.ProbeAsync(source, logger, ct).ConfigureAwait(false);
        if (options.EnableVectorType is { } enforced)
            probed = probed with { SupportsVector = enforced };
        var holder = serviceProvider.GetService<SnowflakeCapabilityHolder>();
        if (holder != null)
            holder.Current = probed;

        await SnowflakeSchemaInitializer.InitializeAsync(source, options, logger, ct).ConfigureAwait(false);

        // Sync node type permissions from MeshConfiguration to the database.
        var meshConfig = serviceProvider.GetService<MeshConfiguration>();
        if (meshConfig?.NodeTypePermissions is { Count: > 0 } permissions)
        {
            var ac = serviceProvider.GetService<SnowflakeAccessControl>()
                ?? new SnowflakeAccessControl(
                    source, schemaName: options.Schema, centralSchema: options.Schema,
                    capabilities: holder);
            await ac.SyncNodeTypePermissionsAsync(permissions, ct).ConfigureAwait(false);
        }

        // Build (or refresh) the top-level autocomplete index so first queries don't race it.
        var searchInfra = serviceProvider.GetService<SnowflakeSearchInfrastructure>();
        if (searchInfra != null)
            await searchInfra.RebuildTopLevelIndexAsync(ct).ConfigureAwait(false);
    }
}
