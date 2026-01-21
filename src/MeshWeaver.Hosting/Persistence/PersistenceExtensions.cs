using MeshWeaver.Domain;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

        // Register the data change notifier as singleton
        services.AddSingleton<IDataChangeNotifier, DataChangeNotifier>();

        // Register the storage adapter using the factory
        services.AddSingleton<IStorageAdapter>(sp =>
        {
            var factory = sp.GetKeyedService<IStorageAdapterFactory>(config.Type);
            if (factory == null)
            {
                var registeredTypes = string.Join(", ", new[] { "FileSystem", "AzureBlob", "Cosmos" });
                throw new InvalidOperationException(
                    $"Unknown storage type: '{config.Type}'. " +
                    $"Supported types: {registeredTypes}. " +
                    $"Ensure the appropriate package is referenced (e.g., MeshWeaver.Hosting.AzureStorage for AzureBlob).");
            }

            return factory.Create(config, sp);
        });

        services.AddSingleton<IPersistenceService>(sp =>
            new FileSystemPersistenceService(sp.GetRequiredService<IStorageAdapter>()));

        services.AddSingleton<IMeshQuery>(sp =>
            new InMemoryMeshQuery(
                sp.GetRequiredService<IPersistenceService>(),
                sp.GetService<INavigationService>(),
                sp.GetService<ISecurityService>(),
                sp.GetService<AccessService>(),
                sp.GetService<IDataChangeNotifier>()));

        return services;
    }

    /// <summary>
    /// Adds file system persistence to the mesh builder.
    /// </summary>
    /// <param name="builder">The mesh builder</param>
    /// <param name="baseDirectory">The base directory for storing JSON files</param>
    /// <returns>The mesh builder for chaining</returns>
    public static TBuilder AddFileSystemPersistence<TBuilder>(this TBuilder builder, string baseDirectory)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services => services.AddFileSystemPersistence(baseDirectory));
        return builder;
    }

    /// <summary>
    /// Adds file system persistence to the mesh builder.
    /// Alias for AddFileSystemPersistence using the "With" naming convention.
    /// </summary>
    /// <param name="builder">The mesh builder</param>
    /// <param name="basePath">The base path for storing JSON files</param>
    /// <returns>The mesh builder for chaining</returns>
    public static TBuilder WithFileSystemPersistence<TBuilder>(this TBuilder builder, string basePath)
        where TBuilder : MeshBuilder
        => builder.AddFileSystemPersistence(basePath);

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
        // Register the data change notifier as singleton
        services.AddSingleton<IDataChangeNotifier, DataChangeNotifier>();

        services.AddSingleton<IPersistenceService>(sp =>
            new InMemoryPersistenceService(
                storageAdapter: null,
                changeNotifier: sp.GetService<IDataChangeNotifier>()));

        services.AddSingleton<IMeshQuery>(sp =>
            new InMemoryMeshQuery(
                sp.GetRequiredService<IPersistenceService>(),
                sp.GetService<INavigationService>(),
                sp.GetService<ISecurityService>(),
                sp.GetService<AccessService>(),
                sp.GetService<IDataChangeNotifier>()));
        return services;
    }

    /// <summary>
    /// Adds file system persistence that reads directly from disk.
    /// Uses type registry for polymorphic JSON deserialization of Content and partition objects.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="baseDirectory">The base directory for storing JSON files</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddFileSystemPersistence(this IServiceCollection services, string baseDirectory)
    {
        // Register the data change notifier as singleton
        services.AddSingleton<IDataChangeNotifier, DataChangeNotifier>();

        // Use factory registration to get ITypeRegistry from the resolved service provider
        services.AddSingleton<IStorageAdapter>(sp =>
            new FileSystemStorageAdapter(
                baseDirectory,
                typeRegistryFactory: () => sp.GetService<ITypeRegistry>()));

        services.AddSingleton<IPersistenceService>(sp =>
            new FileSystemPersistenceService(sp.GetRequiredService<IStorageAdapter>()));

        services.AddSingleton<IMeshQuery>(sp =>
            new InMemoryMeshQuery(
                sp.GetRequiredService<IPersistenceService>(),
                sp.GetService<INavigationService>(),
                sp.GetService<ISecurityService>(),
                sp.GetService<AccessService>(),
                sp.GetService<IDataChangeNotifier>()));

        return services;
    }

    /// <summary>
    /// Adds a custom storage adapter with in-memory persistence service.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="storageAdapter">The custom storage adapter</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPersistence(this IServiceCollection services, IStorageAdapter storageAdapter)
    {
        // Register the data change notifier as singleton
        services.AddSingleton<IDataChangeNotifier, DataChangeNotifier>();

        services.AddSingleton(storageAdapter);
        services.AddSingleton<IPersistenceService>(sp =>
            new InMemoryPersistenceService(
                storageAdapter,
                sp.GetService<IDataChangeNotifier>()));

        services.AddSingleton<IMeshQuery>(sp =>
            new InMemoryMeshQuery(
                sp.GetRequiredService<IPersistenceService>(),
                sp.GetService<INavigationService>(),
                sp.GetService<ISecurityService>(),
                sp.GetService<AccessService>(),
                sp.GetService<IDataChangeNotifier>()));
        return services;
    }

    /// <summary>
    /// Adds a custom persistence service.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="persistenceService">The custom persistence service</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPersistence(this IServiceCollection services, IPersistenceService persistenceService)
    {
        // Register the data change notifier as singleton
        services.AddSingleton<IDataChangeNotifier, DataChangeNotifier>();

        services.AddSingleton(persistenceService);
        services.AddSingleton<IMeshQuery>(sp =>
            new InMemoryMeshQuery(
                sp.GetRequiredService<IPersistenceService>(),
                sp.GetService<INavigationService>(),
                sp.GetService<ISecurityService>(),
                sp.GetService<AccessService>(),
                sp.GetService<IDataChangeNotifier>()));
        return services;
    }
}
