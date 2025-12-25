using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Cosmos;

/// <summary>
/// Extension methods for configuring Cosmos DB persistence.
/// </summary>
public static class PersistenceExtensions
{
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
        services.AddSingleton<IPersistenceService>(persistenceService);

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
}
