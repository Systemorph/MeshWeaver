using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
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
    public static IServiceCollection AddPersistence(this IServiceCollection services, IPersistenceServiceCore persistenceServiceCore)
    {
        // Register the data change notifier as singleton
        services.TryAddSingleton<IDataChangeNotifier, DataChangeNotifier>();

        // Core services remain singletons (for shared caches)
        services.AddSingleton(persistenceServiceCore);
        services.TryAddSingleton<IMeshQueryProvider, InMemoryMeshQuery>();

        // Always add static node provider (picks up IStaticNodeProvider registrations + MeshConfiguration.Nodes)
        services.AddSingleton<IMeshQueryProvider>(sp =>
            new StaticNodeQueryProvider(
                sp.GetServices<IStaticNodeProvider>(),
                sp.GetService<MeshConfiguration>()));

        // Wrapper services are scoped (per hub)
        services.AddScoped<IPersistenceService, PersistenceService>();
        services.AddScoped<IMeshQuery, MeshQuery>();

        return services;
    }

    /// <summary>
    /// Helper method to register common services and wrapper services.
    /// </summary>
    private static IServiceCollection AddCoreAndWrapperServices<TPersistenceCore>(this IServiceCollection services)
        where TPersistenceCore : class, IPersistenceServiceCore
    {
        // Register the data change notifier as singleton (use TryAdd to avoid duplicates)
        services.TryAddSingleton<IDataChangeNotifier, DataChangeNotifier>();

        // Core services remain singletons (for shared caches)
        services.AddSingleton<IPersistenceServiceCore, TPersistenceCore>();
        services.TryAddSingleton<IMeshQueryProvider, InMemoryMeshQuery>();

        // Always add static node provider (picks up IStaticNodeProvider registrations + MeshConfiguration.Nodes)
        services.AddSingleton<IMeshQueryProvider>(sp =>
            new StaticNodeQueryProvider(
                sp.GetServices<IStaticNodeProvider>(),
                sp.GetService<MeshConfiguration>()));

        // Wrapper services are scoped (per hub)
        services.AddScoped<IPersistenceService, PersistenceService>();
        services.AddScoped<IMeshQuery, MeshQuery>();

        return services;
    }
}
