using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
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

    public IStorageAdapter Create(GraphStorageConfig config, IServiceProvider serviceProvider)
    {
        // Try to use an Aspire-injected or externally-registered NpgsqlDataSource first
        var dataSource = serviceProvider.GetService<NpgsqlDataSource>();

        if (dataSource == null)
        {
            var opts = options.Value;
            var connectionString = opts.ConnectionString
                ?? config.ConnectionString
                ?? throw new InvalidOperationException(
                    "PostgreSQL connection string not configured. " +
                    "Set PostgreSqlStorageOptions.ConnectionString or Graph:Storage:ConnectionString.");

            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            dataSourceBuilder.UseVector();
            dataSource = dataSourceBuilder.Build();
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

        // Register PostgreSqlMeshQuery so it takes priority over InMemoryMeshQuery
        services.AddSingleton<IMeshQueryCore>(sp =>
        {
            var adapter = sp.GetRequiredService<IStorageAdapter>() as PostgreSqlStorageAdapter
                ?? throw new InvalidOperationException(
                    "PostgreSqlMeshQuery requires PostgreSqlStorageAdapter.");
            var changeNotifier = sp.GetService<IDataChangeNotifier>();
            return new PostgreSqlMeshQuery(adapter, changeNotifier);
        });

        // Register PostgreSqlActivityStore for activity tracking
        services.TryAddSingleton<IActivityStore>(sp =>
        {
            var dataSource = sp.GetRequiredService<NpgsqlDataSource>();
            return new PostgreSqlActivityStore(dataSource);
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

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.UseVector();
        var dataSource = dataSourceBuilder.Build();

        var embeddingProvider = services.BuildServiceProvider().GetService<IEmbeddingProvider>();
        var storageAdapter = new PostgreSqlStorageAdapter(dataSource, embeddingProvider);

        // Register PostgreSqlMeshQuery BEFORE AddPersistence so TryAddSingleton picks it up
        services.AddSingleton<IMeshQueryCore>(sp =>
            new PostgreSqlMeshQuery(storageAdapter, sp.GetService<IDataChangeNotifier>()));

        services.AddPersistence(storageAdapter);

        // Register access control
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

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.UseVector();
        var dataSource = dataSourceBuilder.Build();

        var embeddingProvider = services.BuildServiceProvider().GetService<IEmbeddingProvider>();
        var storageAdapter = new PostgreSqlStorageAdapter(dataSource, embeddingProvider);

        // Register the data change notifier
        services.AddSingleton<IDataChangeNotifier, DataChangeNotifier>();

        // Register the storage adapter
        services.AddSingleton<IStorageAdapter>(storageAdapter);
        services.AddSingleton(storageAdapter);

        // Register persistence service
        services.AddSingleton<IPersistenceServiceCore>(sp =>
            new InMemoryPersistenceService(
                storageAdapter,
                sp.GetService<IDataChangeNotifier>()));

        // Register PostgreSqlMeshQuery with change notifier
        services.AddSingleton<IMeshQueryCore>(sp =>
            new PostgreSqlMeshQuery(
                storageAdapter,
                sp.GetService<IDataChangeNotifier>()));

        // Register the Change Listener
        services.AddSingleton(sp =>
        {
            var notifier = sp.GetRequiredService<IDataChangeNotifier>();
            var logger = sp.GetService<ILogger<PostgreSqlChangeListener>>();
            return new PostgreSqlChangeListener(dataSource, notifier, logger);
        });

        // Register access control
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
    }
}
