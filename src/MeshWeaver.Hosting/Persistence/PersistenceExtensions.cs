using System.Text.Json;
using MeshWeaver.Hosting.Activity;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Extension methods for registering persistence services.
/// </summary>
public static class PersistenceExtensions
{
    /// <summary>
    /// Adds persistence configured from Graph:Storage section.
    /// Uses the Type field to select the appropriate storage adapter factory.
    /// </summary>
    /// <param name="builder">The mesh builder</param>
    /// <param name="configuration">Configuration containing Graph:Storage section</param>
    /// <returns>The mesh builder for chaining</returns>
    public static TBuilder AddPersistenceFromConfig<TBuilder>(this TBuilder builder, IConfiguration configuration)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services => services.AddPersistenceFromConfig(configuration));
        return builder;
    }

    /// <summary>
    /// Adds persistence configured from Graph:Storage section.
    /// Uses the Type field to select the appropriate storage adapter factory.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">Configuration containing Graph:Storage section</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPersistenceFromConfig(this IServiceCollection services, IConfiguration configuration)
    {
        var storageConfig = configuration.GetSection("Graph:Storage").Get<GraphStorageConfig>();
        if (storageConfig == null)
        {
            throw new InvalidOperationException(
                "Graph:Storage configuration section is required. " +
                "Configure it in appsettings.json with at least Type and BasePath.");
        }

        return services.AddPersistence(storageConfig);
    }

    /// <summary>
    /// Adds persistence using the specified storage configuration.
    /// Uses the Type field to select the appropriate storage adapter factory.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="config">Storage configuration</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPersistence(this IServiceCollection services, GraphStorageConfig config)
    {
        // Register FileSystem factory as default
        services.AddKeyedSingleton<IStorageAdapterFactory, FileSystemStorageAdapterFactory>(
            FileSystemStorageAdapterFactory.StorageType);

        // Register the storage adapter using the factory
        services.AddSingleton<IStorageAdapter>(sp =>
        {
            var factory = sp.GetKeyedService<IStorageAdapterFactory>(config.Type);
            if (factory == null)
            {
                throw new InvalidOperationException(
                    $"Unknown storage type: '{config.Type}'. " +
                    $"Ensure the appropriate storage factory is registered " +
                    $"(e.g., AddPostgreSqlStorageFactory, AddCosmosStorageFactory).");
            }

            return factory.Create(config, sp);
        });

        // Register common services and wrapper services
        return services.AddCoreAndWrapperServices<FileSystemPersistenceService>();
    }

    /// <summary>
    /// Adds file system persistence to the mesh builder.
    /// </summary>
    /// <param name="builder">The mesh builder</param>
    /// <param name="baseDirectory">The base directory for storing JSON files</param>
    /// <param name="writeOptionsModifier">Optional modifier for JsonSerializerOptions when writing (e.g., to enable WriteIndented)</param>
    /// <returns>The mesh builder for chaining</returns>
    public static TBuilder AddFileSystemPersistence<TBuilder>(
        this TBuilder builder,
        string baseDirectory,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services => services.AddFileSystemPersistence(baseDirectory, writeOptionsModifier));
        return builder;
    }

    /// <summary>
    /// Adds file system persistence to the mesh builder.
    /// Alias for AddFileSystemPersistence using the "With" naming convention.
    /// </summary>
    /// <param name="builder">The mesh builder</param>
    /// <param name="basePath">The base path for storing JSON files</param>
    /// <param name="writeOptionsModifier">Optional modifier for JsonSerializerOptions when writing (e.g., to enable WriteIndented)</param>
    /// <returns>The mesh builder for chaining</returns>
    public static TBuilder WithFileSystemPersistence<TBuilder>(
        this TBuilder builder,
        string basePath,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null)
        where TBuilder : MeshBuilder
        => builder.AddFileSystemPersistence(basePath, writeOptionsModifier);

    /// <summary>
    /// Adds in-memory persistence to the mesh builder (no file system backing).
    /// </summary>
    /// <param name="builder">The mesh builder</param>
    /// <returns>The mesh builder for chaining</returns>
    public static TBuilder AddInMemoryPersistence<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services => services.AddInMemoryPersistence());
        return builder;
    }

    /// <summary>
    /// Adds in-memory persistence to the mesh builder (no file system backing).
    /// Alias for AddInMemoryPersistence using the "With" naming convention.
    /// </summary>
    /// <param name="builder">The mesh builder</param>
    /// <returns>The mesh builder for chaining</returns>
    public static TBuilder WithInMemoryPersistence<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
        => builder.AddInMemoryPersistence();

    /// <summary>
    /// Adds an in-memory persistence service (no file system backing).
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddInMemoryPersistence(this IServiceCollection services)
    {
        // Register common services and wrapper services
        return services.AddCoreAndWrapperServices<InMemoryPersistenceService>();
    }

    /// <summary>
    /// Adds an existing in-memory persistence service instance.
    /// Useful for tests that need to seed data before the hub is initialized.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="instance">The pre-created persistence service instance</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddInMemoryPersistence(this IServiceCollection services, InMemoryPersistenceService instance)
    {
        return services.AddPersistence(instance);
    }

    /// <summary>
    /// Adds file system persistence that reads directly from disk.
    /// Uses the hub's JsonSerializerOptions for proper type polymorphism.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="baseDirectory">The base directory for storing JSON files</param>
    /// <param name="writeOptionsModifier">Optional modifier for JsonSerializerOptions when writing (e.g., to enable WriteIndented)</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddFileSystemPersistence(
        this IServiceCollection services,
        string baseDirectory,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null)
    {
        services.AddSingleton<IStorageAdapter>(new FileSystemStorageAdapter(baseDirectory, writeOptionsModifier));

        // Register common services and wrapper services
        return services.AddCoreAndWrapperServices<FileSystemPersistenceService>();
    }

    /// <summary>
    /// Adds cached file system persistence that pre-loads all files into memory at startup.
    /// All reads are served from the in-memory cache with zero disk I/O.
    /// Designed for test scenarios where repeated disk I/O is a bottleneck.
    /// </summary>
    public static TBuilder AddCachedFileSystemPersistence<TBuilder>(
        this TBuilder builder,
        string baseDirectory,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStorageAdapter>(new CachingStorageAdapter(baseDirectory, writeOptionsModifier));
            return services.AddCoreAndWrapperServices<FileSystemPersistenceService>();
        });
        return builder;
    }

    /// <summary>
    /// Adds cached partitioned file system persistence that pre-loads all files into memory.
    /// </summary>
    public static TBuilder AddCachedPartitionedFileSystemPersistence<TBuilder>(
        this TBuilder builder,
        string baseDirectory,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<IDataChangeNotifier, DataChangeNotifier>();
            services.TryAddSingleton<IStorageAdapter>(new CachingStorageAdapter(baseDirectory, writeOptionsModifier));

            services.AddSingleton<IPartitionedStoreFactory>(sp =>
            {
                var inclusions = sp.GetServices<PartitionInclusion>().ToList();
                var filter = inclusions.Count > 0
                    ? new PartitionFilter(inclusions.Select(i => i.Name))
                    : null;

                return new CachingPartitionedStoreFactory(
                    baseDirectory,
                    writeOptionsModifier,
                    sp.GetService<IDataChangeNotifier>(),
                    filter);
            });

            return services.AddPartitionedCoreAndWrapperServices();
        });
        return builder;
    }

    /// <summary>
    /// Adds a custom storage adapter with in-memory persistence service.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="storageAdapter">The custom storage adapter</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPersistence(this IServiceCollection services, IStorageAdapter storageAdapter)
    {
        services.AddSingleton(storageAdapter);

        // Register common services and wrapper services
        return services.AddCoreAndWrapperServices<InMemoryPersistenceService>();
    }

    /// <summary>
    /// Adds a custom persistence core service.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="persistenceServiceCore">The custom persistence core service</param>
    /// <returns>The service collection for chaining</returns>
    internal static IServiceCollection AddPersistence(this IServiceCollection services, IStorageService persistenceServiceCore)
    {
        // Register the data change notifier as singleton
        services.TryAddSingleton<IDataChangeNotifier, DataChangeNotifier>();
        services.TryAddSingleton<IActivityStore, InMemoryActivityStore>();

        // Core services remain singletons (for shared caches)
        services.AddSingleton(persistenceServiceCore);
        services.TryAddSingleton<IMeshQueryProvider, InMemoryMeshQuery>();

        // Always add static node provider (picks up IStaticNodeProvider registrations + MeshConfiguration.Nodes)
        services.AddSingleton<IMeshQueryProvider>(sp =>
            new StaticNodeQueryProvider(
                sp.GetServices<IStaticNodeProvider>(),
                sp.GetService<MeshConfiguration>()));

        // Wrapper services are scoped (per hub)
        services.AddScoped<IMeshStorage, PersistenceService>();
        services.AddScoped<IMeshService>(sp =>
            new MeshService(
                sp.GetServices<IMeshQueryProvider>(),
                sp.GetRequiredService<IMessageHub>(),
                sp.GetService<MeshCatalog>()));

        return services;
    }

    /// <summary>
    /// Adds partitioned file system persistence where each top-level path segment
    /// gets its own isolated partition with separate caching.
    /// Files stay in the same directory tree; isolation is logical via routing.
    /// </summary>
    /// <param name="builder">The mesh builder</param>
    /// <param name="baseDirectory">The base directory for storing JSON files</param>
    /// <param name="writeOptionsModifier">Optional modifier for JsonSerializerOptions when writing</param>
    /// <returns>The mesh builder for chaining</returns>
    public static TBuilder AddPartitionedFileSystemPersistence<TBuilder>(
        this TBuilder builder,
        string baseDirectory,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services => services.AddPartitionedFileSystemPersistence(baseDirectory, writeOptionsModifier));
        return builder;
    }

    /// <summary>
    /// Adds partitioned file system persistence where each top-level path segment
    /// gets its own isolated partition with separate caching.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="baseDirectory">The base directory for storing JSON files</param>
    /// <param name="writeOptionsModifier">Optional modifier for JsonSerializerOptions when writing</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPartitionedFileSystemPersistence(
        this IServiceCollection services,
        string baseDirectory,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null)
    {
        services.TryAddSingleton<IDataChangeNotifier, DataChangeNotifier>();

        // Register IStorageAdapter for cross-partition scanning (e.g. activity log feed)
        services.TryAddSingleton<IStorageAdapter>(new FileSystemStorageAdapter(baseDirectory, writeOptionsModifier));

        services.AddSingleton<IPartitionedStoreFactory>(sp =>
        {
            // Collect all partition inclusions registered via IncludePartition()
            var inclusions = sp.GetServices<PartitionInclusion>().ToList();
            var filter = inclusions.Count > 0
                ? new PartitionFilter(inclusions.Select(i => i.Name))
                : null;

            return new FileSystemPartitionedStoreFactory(
                baseDirectory,
                writeOptionsModifier,
                sp.GetService<IDataChangeNotifier>(),
                filter);
        });

        return services.AddPartitionedCoreAndWrapperServices();
    }

    /// <summary>
    /// Includes a specific partition by name in selective partitioned persistence.
    /// When at least one IncludePartition call is made, only explicitly included partitions are loaded.
    /// If no IncludePartition calls are made, all partitions are loaded (backward compatibility).
    /// </summary>
    public static TBuilder IncludePartition<TBuilder>(this TBuilder builder, string partitionName)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(new PartitionInclusion(partitionName));
            return services;
        });
        return builder;
    }

    /// <summary>
    /// Adds partitioned persistence using a custom IPartitionedStoreFactory.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPartitionedCoreAndWrapperServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IDataChangeNotifier, DataChangeNotifier>();
        services.TryAddSingleton<IActivityStore, InMemoryActivityStore>();

        // Register the routing persistence core
        services.AddSingleton<RoutingPersistenceServiceCore>(sp =>
            new RoutingPersistenceServiceCore(
                sp.GetRequiredService<IPartitionedStoreFactory>(),
                sp.GetService<IDataChangeNotifier>()));
        services.AddSingleton<IStorageService>(sp =>
            sp.GetRequiredService<RoutingPersistenceServiceCore>());

        // Register the routing query provider
        services.AddSingleton<IMeshQueryProvider>(sp =>
            new RoutingMeshQueryProvider(sp.GetRequiredService<RoutingPersistenceServiceCore>()));

        // Register the routing version query
        services.AddSingleton<IVersionQuery>(sp =>
        {
            var routingCore = sp.GetRequiredService<RoutingPersistenceServiceCore>();
            var routingVersionQuery = new RoutingVersionQuery();
            foreach (var (partition, vq) in routingCore.VersionQueries)
                routingVersionQuery.Register(partition, vq);
            return routingVersionQuery;
        });

        // Always add static node provider
        services.AddSingleton<IMeshQueryProvider>(sp =>
            new StaticNodeQueryProvider(
                sp.GetServices<IStaticNodeProvider>(),
                sp.GetService<MeshConfiguration>()));

        // Wrapper services are scoped (per hub)
        services.AddScoped<IMeshStorage, PersistenceService>();
        services.AddScoped<IMeshService>(sp =>
            new MeshService(
                sp.GetServices<IMeshQueryProvider>(),
                sp.GetRequiredService<IMessageHub>(),
                sp.GetService<MeshCatalog>()));

        return services;
    }

    /// <summary>
    /// Helper method to register common services and wrapper services.
    /// </summary>
    private static IServiceCollection AddCoreAndWrapperServices<TPersistenceCore>(this IServiceCollection services)
        where TPersistenceCore : class, IStorageService
    {
        // Register the data change notifier as singleton (use TryAdd to avoid duplicates)
        services.TryAddSingleton<IDataChangeNotifier, DataChangeNotifier>();

        // Register in-memory activity store as default (PostgreSQL overrides with its own)
        services.TryAddSingleton<IActivityStore, InMemoryActivityStore>();

        // Core services remain singletons (for shared caches)
        services.AddSingleton<IStorageService, TPersistenceCore>();
        services.TryAddSingleton<IMeshQueryProvider, InMemoryMeshQuery>();

        // Always add static node provider (picks up IStaticNodeProvider registrations + MeshConfiguration.Nodes)
        services.AddSingleton<IMeshQueryProvider>(sp =>
            new StaticNodeQueryProvider(
                sp.GetServices<IStaticNodeProvider>(),
                sp.GetService<MeshConfiguration>()));

        // Register IVersionQuery for non-partitioned mode (uses FileSystemVersionStore if available)
        services.TryAddSingleton<IVersionQuery>(sp =>
        {
            var adapter = sp.GetService<IStorageAdapter>();
            if (adapter is FileSystemStorageAdapter fsAdapter)
                return new FileSystemVersionStore(fsAdapter.BaseDirectory);
            return new NoOpVersionQuery();
        });

        // Wrapper services are scoped (per hub)
        services.AddScoped<IMeshStorage, PersistenceService>();
        services.AddScoped<IMeshService>(sp =>
            new MeshService(
                sp.GetServices<IMeshQueryProvider>(),
                sp.GetRequiredService<IMessageHub>(),
                sp.GetService<MeshCatalog>()));

        return services;
    }

    /// <summary>
    /// Registers the MeshCatalog and IPathResolver.
    /// </summary>
    public static IServiceCollection AddMeshCatalog(this IServiceCollection services)
    {
        services.TryAddSingleton<MeshCatalog>();
        services.TryAddSingleton<IMeshCatalog>(sp => sp.GetRequiredService<MeshCatalog>());
        services.TryAddSingleton<IPathResolver>(sp => sp.GetRequiredService<MeshCatalog>());
        return services;
    }
}
