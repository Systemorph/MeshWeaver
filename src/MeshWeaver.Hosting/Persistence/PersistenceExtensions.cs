using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Extension methods for registering persistence services.
/// </summary>
public static class PersistenceExtensions
{
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
        services.AddSingleton<IPersistenceService>(new InMemoryPersistenceService());
        return services;
    }

    /// <summary>
    /// Adds an in-memory persistence service backed by file system storage.
    /// Initializes the service by loading existing data from the file system.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="baseDirectory">The base directory for storing JSON files</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddFileSystemPersistence(this IServiceCollection services, string baseDirectory)
    {
        var storageAdapter = new FileSystemStorageAdapter(baseDirectory);
        var persistenceService = new InMemoryPersistenceService(storageAdapter);

        // Initialize synchronously to load existing data from file system
        persistenceService.InitializeAsync().GetAwaiter().GetResult();

        services.AddSingleton<IStorageAdapter>(storageAdapter);
        services.AddSingleton<IPersistenceService>(persistenceService);
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
        var persistenceService = new InMemoryPersistenceService(storageAdapter);
        services.AddSingleton(storageAdapter);
        services.AddSingleton<IPersistenceService>(persistenceService);
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
        services.AddSingleton(persistenceService);
        return services;
    }
}
