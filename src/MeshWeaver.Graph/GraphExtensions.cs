using MeshWeaver.Graph.Persistence;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Extension methods for configuring graph persistence via dependency injection.
/// </summary>
public static class GraphExtensions
{
    /// <summary>
    /// Adds in-memory graph persistence without file system backing.
    /// </summary>
    public static IServiceCollection AddGraphPersistence(this IServiceCollection services)
        => services.AddSingleton<IGraphPersistenceService, InMemoryGraphPersistenceService>();

    /// <summary>
    /// Adds in-memory graph persistence with file system initialization and persistence.
    /// </summary>
    public static IServiceCollection AddGraphPersistence(
        this IServiceCollection services,
        string dataDirectory)
        => services
            .AddSingleton<IGraphStorageProvider>(new FileSystemGraphStorageProvider(dataDirectory))
            .AddSingleton<IGraphPersistenceService, InMemoryGraphPersistenceService>();

    /// <summary>
    /// Adds in-memory graph persistence with a custom storage provider.
    /// </summary>
    public static IServiceCollection AddGraphPersistence<TStorageProvider>(
        this IServiceCollection services)
        where TStorageProvider : class, IGraphStorageProvider
        => services
            .AddSingleton<IGraphStorageProvider, TStorageProvider>()
            .AddSingleton<IGraphPersistenceService, InMemoryGraphPersistenceService>();

    /// <summary>
    /// Adds a custom graph persistence service.
    /// </summary>
    public static IServiceCollection AddGraphPersistence<TPersistenceService>(
        this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TPersistenceService : class, IGraphPersistenceService
    {
        services.Add(new ServiceDescriptor(
            typeof(IGraphPersistenceService),
            typeof(TPersistenceService),
            lifetime));
        return services;
    }

    /// <summary>
    /// Adds graph support to a message hub configuration.
    /// </summary>
    public static MessageHubConfiguration AddGraph(this MessageHubConfiguration config)
        => config.WithServices(AddGraphPersistence);

    /// <summary>
    /// Adds graph support with file system persistence to a message hub configuration.
    /// </summary>
    public static MessageHubConfiguration AddGraph(
        this MessageHubConfiguration config,
        string dataDirectory)
        => config.WithServices(sc => sc.AddGraphPersistence(dataDirectory));

    /// <summary>
    /// Adds graph support with a custom storage provider to a message hub configuration.
    /// </summary>
    public static MessageHubConfiguration AddGraph<TStorageProvider>(
        this MessageHubConfiguration config)
        where TStorageProvider : class, IGraphStorageProvider
        => config.WithServices(sc => sc.AddGraphPersistence<TStorageProvider>());
}
