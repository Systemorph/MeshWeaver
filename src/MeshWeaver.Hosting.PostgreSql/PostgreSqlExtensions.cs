using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Factory for creating PostgreSqlStorageAdapter instances from configuration.
/// </summary>
public class PostgreSqlStorageAdapterFactory(
    IOptions<PostgreSqlStorageOptions> options) : IStorageAdapterFactory
{
    public const string StorageType = "PostgreSql";

    private NpgsqlDataSource? _cachedDataSource;

    public IStorageAdapter Create(GraphStorageConfig config, IServiceProvider serviceProvider)
    {
        // Try to use an Aspire-injected or externally-registered NpgsqlDataSource first
        var dataSource = serviceProvider.GetService<NpgsqlDataSource>();

        if (dataSource == null)
        {
            // Cache the data source so multiple Create() calls share a single connection pool
            if (_cachedDataSource == null)
            {
                var opts = options.Value;
                var connectionString = opts.ConnectionString
                    ?? config.ConnectionString
                    ?? throw new InvalidOperationException(
                        "PostgreSQL connection string not configured. " +
                        "Set PostgreSqlStorageOptions.ConnectionString or Graph:Storage:ConnectionString.");

                var csb = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    MaxPoolSize = 20
                };
                var dataSourceBuilder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
                dataSourceBuilder.UseVector();
                _cachedDataSource = dataSourceBuilder.Build();
            }
            dataSource = _cachedDataSource;
        }

        var embeddingProvider = serviceProvider.GetService<IEmbeddingProvider>();
        return new PostgreSqlStorageAdapter(dataSource, embeddingProvider);
    }
}

/// <summary>
/// Extension methods for configuring PostgreSQL persistence.
/// </summary>
public static class PostgreSqlExtensions
{
    /// <summary>
    /// Registers the Azure Foundry embedding provider from an <see cref="EmbeddingOptions"/> instance.
    /// </summary>
    public static IServiceCollection AddAzureFoundryEmbeddings(
        this IServiceCollection services, EmbeddingOptions options)
    {
        if (string.IsNullOrEmpty(options.Endpoint) || string.IsNullOrEmpty(options.ApiKey))
            return services;

        services.AddSingleton<IEmbeddingProvider>(
            new AzureFoundryEmbeddingProvider(options.Endpoint, options.ApiKey,
                options.Model, options.Dimensions));
        services.Configure<PostgreSqlStorageOptions>(o => o.VectorDimensions = options.Dimensions);
        return services;
    }

    /// <summary>
    /// Registers the PostgreSQL storage adapter factory for use with AddPersistenceFromConfig.
    /// Also registers PostgreSqlMeshQuery for native SQL queries.
    /// </summary>
    public static IServiceCollection AddPostgreSqlStorageFactory(
        this IServiceCollection services, Action<PostgreSqlStorageOptions>? configure = null)
    {
        if (configure != null)
            services.Configure(configure);
        services.AddKeyedSingleton<IStorageAdapterFactory, PostgreSqlStorageAdapterFactory>(
            PostgreSqlStorageAdapterFactory.StorageType);

        // Register PostgreSqlMeshQuery so it takes priority over StorageAdapterMeshQueryProvider.
        // The same instance is registered under IVectorSearchProvider so the search box /
        // MCP find / agent tools resolve vector-search via the contract.
        services.AddSingleton<PostgreSqlMeshQuery>(sp =>
        {
            var adapter = sp.GetRequiredService<IStorageAdapter>() as PostgreSqlStorageAdapter
                ?? throw new InvalidOperationException(
                    "PostgreSqlMeshQuery requires PostgreSqlStorageAdapter.");
            return new PostgreSqlMeshQuery(
                adapter,
                sp.GetService<AccessService>(),
                meshConfiguration: null,
                excludedNamespaces: null,
                embeddingProvider: sp.GetService<IEmbeddingProvider>());
        });
        services.AddSingleton<IMeshQueryProvider>(sp => sp.GetRequiredService<PostgreSqlMeshQuery>());
        services.AddSingleton<IVectorSearchProvider>(sp => sp.GetRequiredService<PostgreSqlMeshQuery>());

        return services;
    }

    /// <summary>
    /// Adds PostgreSQL persistence services with automatic schema creation.
    /// </summary>
    public static IServiceCollection AddPostgreSqlPersistence(
        this IServiceCollection services,
        string connectionString,
        Action<PostgreSqlStorageOptions>? configure = null)
    {
        var opts = new PostgreSqlStorageOptions { ConnectionString = connectionString };
        configure?.Invoke(opts);

        var csb = new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 3, ConnectionIdleLifetime = 30 };
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
        dataSourceBuilder.UseVector();
        var dataSource = dataSourceBuilder.Build();

        var embeddingProvider = services.BuildServiceProvider().GetService<IEmbeddingProvider>();
        var storageAdapter = new PostgreSqlStorageAdapter(dataSource, embeddingProvider);

        // Register PostgreSqlMeshQuery BEFORE AddPersistence so TryAddSingleton picks it up.
        // Same instance under IVectorSearchProvider so the search box / MCP find / agent
        // tools route through HNSW cosine similarity when bare-text tokens are present.
        services.AddSingleton<PostgreSqlMeshQuery>(sp =>
            new PostgreSqlMeshQuery(
                storageAdapter,
                sp.GetService<AccessService>(),
                meshConfiguration: null,
                excludedNamespaces: null,
                embeddingProvider: embeddingProvider));
        services.AddSingleton<IMeshQueryProvider>(sp => sp.GetRequiredService<PostgreSqlMeshQuery>());
        services.AddSingleton<IVectorSearchProvider>(sp => sp.GetRequiredService<PostgreSqlMeshQuery>());

        services.AddPersistence(storageAdapter);

        // Register access control and activity store
        services.TryAddSingleton(new PostgreSqlAccessControl(dataSource));

        return services;
    }

    /// <summary>
    /// Adds PostgreSQL persistence with LISTEN/NOTIFY change notification support.
    /// </summary>
    public static IServiceCollection AddPostgreSqlPersistenceWithChangeNotifications(
        this IServiceCollection services,
        string connectionString,
        Action<PostgreSqlStorageOptions>? configure = null)
    {
        var opts = new PostgreSqlStorageOptions { ConnectionString = connectionString };
        configure?.Invoke(opts);

        var csb = new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 3, ConnectionIdleLifetime = 30 };
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
        dataSourceBuilder.UseVector();
        var dataSource = dataSourceBuilder.Build();

        var embeddingProvider = services.BuildServiceProvider().GetService<IEmbeddingProvider>();
        var storageAdapter = new PostgreSqlStorageAdapter(dataSource, embeddingProvider);

        // Register concrete adapter type for change listener
        services.AddSingleton(storageAdapter);

        // PostgreSqlMeshQuery + IVectorSearchProvider — same instance.
        services.AddSingleton<PostgreSqlMeshQuery>(sp =>
            new PostgreSqlMeshQuery(
                storageAdapter,
                sp.GetService<AccessService>(),
                meshConfiguration: null,
                excludedNamespaces: null,
                embeddingProvider: embeddingProvider));
        services.AddSingleton<IMeshQueryProvider>(sp => sp.GetRequiredService<PostgreSqlMeshQuery>());
        services.AddSingleton<IVectorSearchProvider>(sp => sp.GetRequiredService<PostgreSqlMeshQuery>());

        // Register core persistence services (IStorageAdapter, IStorageService, etc.)
        services.AddPersistence(storageAdapter);

        // Register the Change Listener — feeds the adapter's Changes feed.
        services.AddSingleton(sp =>
        {
            var logger = sp.GetService<ILogger<PostgreSqlChangeListener>>();
            return new PostgreSqlChangeListener(dataSource, storageAdapter.ChangeObserver, logger);
        });

        // Register access control and activity store
        services.TryAddSingleton(new PostgreSqlAccessControl(dataSource));

        return services;
    }

    /// <summary>
    /// Initializes the PostgreSQL schema (tables, indexes, triggers).
    /// Call this during application startup.
    /// </summary>
    public static async Task InitializePostgreSqlSchemaAsync(
        this IServiceProvider serviceProvider,
        CancellationToken ct = default)
    {
        // Try to get data source from a registered PostgreSqlStorageAdapter, or from DI directly
        var dataSource = (serviceProvider.GetService<IStorageAdapter>() as PostgreSqlStorageAdapter)?.DataSource
            ?? serviceProvider.GetService<NpgsqlDataSource>()
            ?? throw new InvalidOperationException(
                "No NpgsqlDataSource found. Register via AddPostgreSqlPersistence or Aspire AddNpgsqlDataSource.");

        var options = serviceProvider.GetService<IOptions<PostgreSqlStorageOptions>>()?.Value
            ?? new PostgreSqlStorageOptions();

        await PostgreSqlSchemaInitializer.InitializeAsync(dataSource, options, ct).ConfigureAwait(false);

        // Sync node type permissions from MeshConfiguration to the database
        var meshConfig = serviceProvider.GetService<MeshConfiguration>();
        if (meshConfig?.NodeTypePermissions is { Count: > 0 } permissions)
        {
            var ac = serviceProvider.GetService<PostgreSqlAccessControl>()
                ?? new PostgreSqlAccessControl(dataSource);
            await ac.SyncNodeTypePermissionsAsync(permissions, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Adds partitioned PostgreSQL persistence where each top-level path segment
    /// gets its own PostgreSQL schema with isolated tables.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="connectionString">PostgreSQL connection string</param>
    /// <param name="configure">Optional configuration for PostgreSqlStorageOptions</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPartitionedPostgreSqlPersistence(
        this IServiceCollection services,
        string connectionString,
        Action<PostgreSqlStorageOptions>? configure = null,
        Action<NpgsqlDataSourceBuilder>? configureDataSource = null)
    {
        var opts = new PostgreSqlStorageOptions { ConnectionString = connectionString };
        configure?.Invoke(opts);

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.UseVector();
        configureDataSource?.Invoke(dataSourceBuilder);
        var baseDataSource = dataSourceBuilder.Build();

        // No need to remove a pre-registered InMemory wildcard: PersistenceService
        // orders wildcards by IPartitionStorageProvider.Priority desc, and
        // PostgreSqlPartitionStorageProvider returns 100 (schema-aware) vs.
        // InMemory's default 0 (catch-all). Postgres claims rbuergi (schema
        // exists) before InMemory is asked; for paths Postgres doesn't own
        // (Matches emits false), InMemory's catch-all wins.

        services.AddSingleton<PostgreSqlPartitionStorageProvider>(sp =>
            new PostgreSqlPartitionStorageProvider(
                baseDataSource,
                connectionString,
                opts,
                partitions: null,
                sp.GetService<IEmbeddingProvider>(),
                configureDataSource,
                contexts: null,
                sp.GetService<ILogger<PostgreSqlPartitionStorageProvider>>(),
                sp.GetService<MeshWeaver.Mesh.Threading.IoPoolRegistry>()));
        services.AddSingleton<IPartitionStorageProvider>(sp =>
            sp.GetRequiredService<PostgreSqlPartitionStorageProvider>());

        // Cross-schema query provider — UNION fan-out over searchable partitions.
        services.AddSingleton<ICrossSchemaQueryProvider>(sp =>
            new PostgreSqlCrossSchemaQueryProvider(
                baseDataSource,
                sp.GetService<ILoggerFactory>()?.CreateLogger<PostgreSqlCrossSchemaQueryProvider>()));

        // Fan-out IMeshQueryProvider — picks up unscoped + wildcard-namespace
        // queries (Activity Feed, Latest Threads, Recently Viewed) and routes
        // them through the cross-schema UNION. Scoped queries fall through to
        // the per-schema StorageAdapterMeshQueryProvider unchanged.
        services.AddSingleton<IMeshQueryProvider>(sp =>
            new PostgreSqlPartitionedMeshQuery(
                sp.GetRequiredService<ICrossSchemaQueryProvider>(),
                sp.GetService<AccessService>(),
                sp.GetService<ILogger<PostgreSqlPartitionedMeshQuery>>(),
                sp.GetRequiredService<PostgreSqlPartitionStorageProvider>()));

        // pg_notify listener: register both the singleton and an IHostedService
        // wrapper so the LISTEN session opens at host startup. Without the
        // hosted-service wrapper the listener never starts and every synced
        // query's Replay(1) cache freezes at its Initial value (writes propagate
        // to the table but the cached observable never re-emits).
        // TODO partitioned-pg change feed: in the partitioned setup the
        // listener pump-notifications across many per-partition adapters.
        // For now this PG listener wiring is disabled — each
        // PostgreSqlStorageAdapter publishes from its own Write/Delete (no
        // cross-process LISTEN) which is enough for the in-process test
        // scenarios. A follow-up will route LISTEN events to the right
        // partition adapter's ChangeObserver via the partition registry.
        // services.AddSingleton(sp => new PostgreSqlChangeListener(baseDataSource, ..., ...));
        // services.AddHostedService<PostgreSqlChangeListenerHostedService>();
        // Boot-time seed: CREATE SCHEMA + table init for every framework
        // partition advertised by a static node provider. No enumeration —
        // only what's explicitly registered.
        services.AddHostedService<PostgreSqlPartitionSubscriptionHostedService>();
        // #15: the cross-silo partition-state invalidation listener
        // (PgPartitionNotifyListener / LISTEN partition_changes) is gone. The
        // router no longer caches/probes schema existence — it maps the first
        // path segment to a schema synchronously and reads tolerate an absent
        // schema (42P01 → empty). A partition created on another silo therefore
        // becomes routable immediately, with no invalidation round-trip.

        // #20: PostgreSqlPartitionedMeshQuery serves unscoped + satellite queries via
        // fast SQL fan-out, so tell the pedestrian StorageAdapterMeshQueryProvider to
        // DEFER those (walk only scoped mesh_nodes). This removes the pedestrian's
        // redundant ListChildPaths walk from those merges — the walk that gated the
        // 60-70s cross-schema ResolvePath/onboarding stall — without dropping rows
        // (the pedestrian never visited satellite tables anyway).
        services.AddSingleton(new StorageAdapterQueryProviderOptions
        {
            DeferToNativeProvider = true
        });
        services.AddPartitionedCoreAndWrapperServices();

        return services;
    }

    /// <summary>
    /// Adds partitioned PostgreSQL persistence using an Aspire-injected NpgsqlDataSource from DI.
    /// Each top-level path segment gets its own PostgreSQL schema with isolated tables.
    /// Resolves the connection string from IConfiguration (Aspire convention) because
    /// NpgsqlDataSource.ConnectionString strips the password by default.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional configuration for PostgreSqlStorageOptions</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPartitionedPostgreSqlPersistence(
        this IServiceCollection services,
        Action<PostgreSqlStorageOptions>? configure = null,
        Action<NpgsqlDataSourceBuilder>? configureDataSource = null)
    {
        services.AddSingleton<PostgreSqlPartitionStorageProvider>(sp =>
        {
            var baseDataSource = sp.GetRequiredService<NpgsqlDataSource>();
            var config = sp.GetService<IConfiguration>();
            var connectionString = config?.GetConnectionString("memex")
                                   ?? baseDataSource.ConnectionString;

            var baseCsb = new NpgsqlConnectionStringBuilder(baseDataSource.ConnectionString);
            if (!string.IsNullOrEmpty(baseCsb.Username))
            {
                var csb = new NpgsqlConnectionStringBuilder(connectionString);
                if (string.IsNullOrEmpty(csb.Username))
                {
                    csb.Username = baseCsb.Username;
                    connectionString = csb.ConnectionString;
                }
            }
            var opts = new PostgreSqlStorageOptions { ConnectionString = connectionString };
            configure?.Invoke(opts);

            return new PostgreSqlPartitionStorageProvider(
                baseDataSource,
                connectionString,
                opts,
                partitions: null,
                sp.GetService<IEmbeddingProvider>(),
                configureDataSource,
                contexts: null,
                sp.GetService<ILogger<PostgreSqlPartitionStorageProvider>>(),
                sp.GetService<MeshWeaver.Mesh.Threading.IoPoolRegistry>());
        });
        services.AddSingleton<IPartitionStorageProvider>(sp =>
            sp.GetRequiredService<PostgreSqlPartitionStorageProvider>());

        // Cross-schema query provider — uses stored procedure for single-query fan-out.
        // Self-contained discovery via information_schema; no provider/factory dependency.
        services.AddSingleton<ICrossSchemaQueryProvider>(sp =>
            new PostgreSqlCrossSchemaQueryProvider(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<PostgreSqlCrossSchemaQueryProvider>()));

        // Fan-out IMeshQueryProvider — picks up unscoped + wildcard-namespace
        // queries (Activity Feed, Latest Threads, Recently Viewed) and routes
        // them through the cross-schema UNION. Scoped queries fall through to
        // the per-schema StorageAdapterMeshQueryProvider unchanged.
        services.AddSingleton<IMeshQueryProvider>(sp =>
            new PostgreSqlPartitionedMeshQuery(
                sp.GetRequiredService<ICrossSchemaQueryProvider>(),
                sp.GetService<AccessService>(),
                sp.GetService<ILogger<PostgreSqlPartitionedMeshQuery>>(),
                sp.GetRequiredService<PostgreSqlPartitionStorageProvider>()));

        // Start the Admin/Partition/* subscription so writes can route — see
        // the longer comment on the same registration in the connection-string
        // overload above.
        services.AddHostedService<PostgreSqlPartitionSubscriptionHostedService>();

        // #20: PostgreSqlPartitionedMeshQuery serves unscoped + satellite queries via
        // fast SQL fan-out, so tell the pedestrian StorageAdapterMeshQueryProvider to
        // DEFER those (walk only scoped mesh_nodes). This removes the pedestrian's
        // redundant ListChildPaths walk from those merges — the walk that gated the
        // 60-70s cross-schema ResolvePath/onboarding stall — without dropping rows
        // (the pedestrian never visited satellite tables anyway).
        services.AddSingleton(new StorageAdapterQueryProviderOptions
        {
            DeferToNativeProvider = true
        });
        services.AddPartitionedCoreAndWrapperServices();

        return services;
    }
}
