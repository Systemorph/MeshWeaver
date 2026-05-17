using MeshWeaver.Hosting.Persistence;
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

        // Register PostgreSqlMeshQuery so it takes priority over StorageAdapterMeshQueryProvider
        services.AddSingleton<IMeshQueryProvider>(sp =>
        {
            var adapter = sp.GetRequiredService<IStorageAdapter>() as PostgreSqlStorageAdapter
                ?? throw new InvalidOperationException(
                    "PostgreSqlMeshQuery requires PostgreSqlStorageAdapter.");
            var changeNotifier = sp.GetService<IDataChangeNotifier>();
            var accessService = sp.GetService<AccessService>();
            return new PostgreSqlMeshQuery(adapter, changeNotifier, accessService);
        });

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

        // Register PostgreSqlMeshQuery BEFORE AddPersistence so TryAddSingleton picks it up
        services.AddSingleton<IMeshQueryProvider>(sp =>
            new PostgreSqlMeshQuery(storageAdapter, sp.GetService<IDataChangeNotifier>(), sp.GetService<AccessService>()));

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

        // Register PostgreSqlMeshQuery BEFORE AddPersistence so TryAddSingleton doesn't override it
        services.AddSingleton<IMeshQueryProvider>(sp =>
            new PostgreSqlMeshQuery(
                storageAdapter,
                sp.GetService<IDataChangeNotifier>(),
                sp.GetService<AccessService>()));

        // Register core persistence services (IStorageAdapter, IStorageService, etc.)
        services.AddPersistence(storageAdapter);

        // Register the Change Listener
        services.AddSingleton(sp =>
        {
            var notifier = sp.GetRequiredService<IDataChangeNotifier>();
            var logger = sp.GetService<ILogger<PostgreSqlChangeListener>>();
            return new PostgreSqlChangeListener(dataSource, notifier, logger);
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

        await PostgreSqlSchemaInitializer.InitializeAsync(dataSource, options, ct);

        // Sync node type permissions from MeshConfiguration to the database
        var meshConfig = serviceProvider.GetService<MeshConfiguration>();
        if (meshConfig?.NodeTypePermissions is { Count: > 0 } permissions)
        {
            var ac = serviceProvider.GetService<PostgreSqlAccessControl>()
                ?? new PostgreSqlAccessControl(dataSource);
            await ac.SyncNodeTypePermissionsAsync(permissions, ct);
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

        services.TryAddSingleton<IDataChangeNotifier, DataChangeNotifier>();

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
                sp.GetService<ILogger<PostgreSqlPartitionStorageProvider>>()));
        services.AddSingleton<IPartitionStorageProvider>(sp =>
            sp.GetRequiredService<PostgreSqlPartitionStorageProvider>());

        services.AddSingleton<IPartitionAccessProvider>(
            new PostgreSqlPartitionAccessProvider(baseDataSource));

        // pg_notify listener: register both the singleton and an IHostedService
        // wrapper so the LISTEN session opens at host startup. Without the
        // hosted-service wrapper the listener never starts and every synced
        // query's Replay(1) cache freezes at its Initial value (writes propagate
        // to the table but the cached observable never re-emits).
        services.AddSingleton(sp =>
        {
            var notifier = sp.GetRequiredService<IDataChangeNotifier>();
            var listenerLogger = sp.GetService<ILogger<PostgreSqlChangeListener>>();
            return new PostgreSqlChangeListener(baseDataSource, notifier, listenerLogger);
        });
        services.AddHostedService<PostgreSqlChangeListenerHostedService>();
        // Boot-time seed: CREATE SCHEMA + table init for every framework
        // partition advertised by a static node provider. No enumeration —
        // only what's explicitly registered.
        services.AddHostedService<PostgreSqlPartitionSubscriptionHostedService>();
        // Cross-silo partition-state invalidation: LISTEN partition_changes
        // and drop the PgPartitionCache entry for any namespace whose
        // Admin/Partition row was added/updated/deleted on any silo.
        // Migration V23 installs the trigger.
        services.AddSingleton(sp =>
        {
            var provider = sp.GetRequiredService<PostgreSqlPartitionStorageProvider>();
            var notifyLogger = sp.GetService<ILogger<PgPartitionNotifyListener>>();
            return new PgPartitionNotifyListener(baseDataSource, provider, notifyLogger);
        });
        services.AddHostedService(sp => sp.GetRequiredService<PgPartitionNotifyListener>());

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
        services.TryAddSingleton<IDataChangeNotifier, DataChangeNotifier>();

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
                sp.GetService<ILogger<PostgreSqlPartitionStorageProvider>>());
        });
        services.AddSingleton<IPartitionStorageProvider>(sp =>
            sp.GetRequiredService<PostgreSqlPartitionStorageProvider>());

        services.AddSingleton<IPartitionAccessProvider>(sp =>
            new PostgreSqlPartitionAccessProvider(sp.GetRequiredService<NpgsqlDataSource>()));

        // Cross-schema query provider — uses stored procedure for single-query fan-out.
        // Self-contained discovery via information_schema; no provider/factory dependency.
        services.AddSingleton<ICrossSchemaQueryProvider>(sp =>
            new PostgreSqlCrossSchemaQueryProvider(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<PostgreSqlCrossSchemaQueryProvider>()));

        // Start the Admin/Partition/* subscription so writes can route — see
        // the longer comment on the same registration in the connection-string
        // overload above.
        services.AddHostedService<PostgreSqlPartitionSubscriptionHostedService>();

        services.AddPartitionedCoreAndWrapperServices();

        return services;
    }
}
