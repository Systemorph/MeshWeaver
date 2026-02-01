using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Cosmos;

/// <summary>
/// Factory for creating CosmosStorageAdapter instances from configuration.
/// </summary>
public class CosmosStorageAdapterFactory : IStorageAdapterFactory
{
    public const string StorageType = "Cosmos";

    public IStorageAdapter Create(GraphStorageConfig config, IServiceProvider serviceProvider)
    {
        var connectionString = config.ConnectionString
            ?? throw new InvalidOperationException(
                "Graph:Storage:ConnectionString is required for Cosmos storage. " +
                "Configure it in appsettings.json.");

        var databaseName = config.DatabaseName ?? "MeshWeaver";
        var nodesContainerName = config.Settings?.GetValueOrDefault("NodesContainer") ?? "nodes";
        var partitionsContainerName = config.Settings?.GetValueOrDefault("PartitionsContainer") ?? "partitions";

        var cosmosClient = new CosmosClient(connectionString);
        var database = cosmosClient.GetDatabase(databaseName);
        var nodesContainer = database.GetContainer(nodesContainerName);
        var partitionsContainer = database.GetContainer(partitionsContainerName);

        return new CosmosStorageAdapter(nodesContainer, partitionsContainer);
    }
}

/// <summary>
/// Extension methods for configuring Cosmos DB persistence.
/// </summary>
public static class PersistenceExtensions
{
    /// <summary>
    /// Registers the Cosmos storage adapter factory for use with AddPersistenceFromConfig.
    /// </summary>
    public static IServiceCollection AddCosmosStorageFactory(this IServiceCollection services)
    {
        services.AddKeyedSingleton<IStorageAdapterFactory, CosmosStorageAdapterFactory>(
            CosmosStorageAdapterFactory.StorageType);
        return services;
    }

    /// <summary>
    /// Adds Cosmos DB persistence services.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="cosmosClient">The Cosmos DB client</param>
    /// <param name="databaseName">The database name</param>
    /// <param name="nodesContainerName">Container name for MeshNodes (default: "nodes")</param>
    /// <param name="partitionsContainerName">Container name for partition objects (default: "partitions")</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddCosmosPersistence(
        this IServiceCollection services,
        CosmosClient cosmosClient,
        string databaseName,
        string nodesContainerName = "nodes",
        string partitionsContainerName = "partitions")
    {
        var database = cosmosClient.GetDatabase(databaseName);
        var nodesContainer = database.GetContainer(nodesContainerName);
        var partitionsContainer = database.GetContainer(partitionsContainerName);

        var storageAdapter = new CosmosStorageAdapter(nodesContainer, partitionsContainer);
        var persistenceService = new InMemoryPersistenceService(storageAdapter);

        services.AddSingleton<IStorageAdapter>(storageAdapter);
        services.AddSingleton<IPersistenceServiceCore>(persistenceService);

        return services;
    }

    /// <summary>
    /// Adds Cosmos DB persistence services with automatic container creation.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="cosmosClient">The Cosmos DB client</param>
    /// <param name="databaseName">The database name</param>
    /// <param name="throughput">The throughput for containers</param>
    /// <returns>The service collection for chaining</returns>
    public static async Task<IServiceCollection> AddCosmosPersistenceAsync(
        this IServiceCollection services,
        CosmosClient cosmosClient,
        string databaseName,
        int throughput = 400)
    {
        // Create database if not exists
        var databaseResponse = await cosmosClient.CreateDatabaseIfNotExistsAsync(
            databaseName,
            ThroughputProperties.CreateManualThroughput(throughput));
        var database = databaseResponse.Database;

        // Create containers if not exists
        await database.CreateContainerIfNotExistsAsync(
            new ContainerProperties("nodes", "/key")
            {
                DefaultTimeToLive = -1 // No expiration
            });

        await database.CreateContainerIfNotExistsAsync(
            new ContainerProperties("partitions", "/partitionKey")
            {
                DefaultTimeToLive = -1
            });

        return services.AddCosmosPersistence(cosmosClient, databaseName);
    }

    /// <summary>
    /// Adds Cosmos DB persistence services with Change Feed support for observable queries.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="cosmosClient">The Cosmos DB client</param>
    /// <param name="databaseName">The database name</param>
    /// <param name="nodesContainerName">Container name for MeshNodes (default: "nodes")</param>
    /// <param name="partitionsContainerName">Container name for partition objects (default: "partitions")</param>
    /// <param name="leaseContainerName">Container name for Change Feed leases (default: "{databaseName}-leases")</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddCosmosPersistenceWithChangeFeed(
        this IServiceCollection services,
        CosmosClient cosmosClient,
        string databaseName,
        string nodesContainerName = "nodes",
        string partitionsContainerName = "partitions",
        string? leaseContainerName = null)
    {
        var database = cosmosClient.GetDatabase(databaseName);
        var nodesContainer = database.GetContainer(nodesContainerName);
        var partitionsContainer = database.GetContainer(partitionsContainerName);

        // Register the data change notifier as singleton
        services.AddSingleton<IDataChangeNotifier, DataChangeNotifier>();

        // Register the storage adapter
        var storageAdapter = new CosmosStorageAdapter(nodesContainer, partitionsContainer);
        services.AddSingleton<IStorageAdapter>(storageAdapter);
        services.AddSingleton(storageAdapter); // Also register as concrete type for change feed setup

        // Register persistence service with change notifier
        services.AddSingleton<IPersistenceServiceCore>(sp =>
            new InMemoryPersistenceService(
                storageAdapter,
                sp.GetService<IDataChangeNotifier>()));

        // Register IMeshQueryCore with change notifier
        services.AddSingleton<IMeshQueryCore>(sp =>
            new InMemoryMeshQuery(
                sp.GetRequiredService<IPersistenceServiceCore>(),
                sp.GetService<ISecurityService>(),
                sp.GetService<AccessService>(),
                sp.GetService<IDataChangeNotifier>()));

        // Register the Change Feed Processor
        services.AddSingleton(sp =>
        {
            var effectiveLeaseContainerName = leaseContainerName ?? $"{databaseName}-leases";
            var leaseContainer = database.GetContainer(effectiveLeaseContainerName);
            var notifier = sp.GetRequiredService<IDataChangeNotifier>();
            var logger = sp.GetService<ILogger<CosmosChangeFeedProcessor>>();

            return new CosmosChangeFeedProcessor(
                nodesContainer,
                leaseContainer,
                notifier,
                "MeshWeaverChangeFeedProcessor",
                logger);
        });

        return services;
    }

    /// <summary>
    /// Adds Cosmos DB persistence services with Change Feed support and automatic container creation.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="cosmosClient">The Cosmos DB client</param>
    /// <param name="databaseName">The database name</param>
    /// <param name="throughput">The throughput for containers</param>
    /// <returns>The service collection for chaining</returns>
    public static async Task<IServiceCollection> AddCosmosPersistenceWithChangeFeedAsync(
        this IServiceCollection services,
        CosmosClient cosmosClient,
        string databaseName,
        int throughput = 400)
    {
        // Create database if not exists
        var databaseResponse = await cosmosClient.CreateDatabaseIfNotExistsAsync(
            databaseName,
            ThroughputProperties.CreateManualThroughput(throughput));
        var database = databaseResponse.Database;

        // Create containers if not exists
        await database.CreateContainerIfNotExistsAsync(
            new ContainerProperties("nodes", "/key")
            {
                DefaultTimeToLive = -1 // No expiration
            });

        await database.CreateContainerIfNotExistsAsync(
            new ContainerProperties("partitions", "/partitionKey")
            {
                DefaultTimeToLive = -1
            });

        // Create lease container for Change Feed
        await database.CreateContainerIfNotExistsAsync(
            new ContainerProperties($"{databaseName}-leases", "/id")
            {
                DefaultTimeToLive = -1
            });

        return services.AddCosmosPersistenceWithChangeFeed(cosmosClient, databaseName);
    }
}
