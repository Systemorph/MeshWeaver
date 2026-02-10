using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Cosmos;

/// <summary>
/// Ensures the Cosmos DB database and containers exist.
/// </summary>
public static class CosmosContainerInitializer
{
    public static async Task<Database> EnsureDatabaseAndContainersAsync(
        CosmosClient client,
        CosmosStorageOptions options,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        logger?.LogInformation("Ensuring Cosmos DB database '{Database}' and containers exist", options.DatabaseName);

        var databaseResponse = await client.CreateDatabaseIfNotExistsAsync(options.DatabaseName, cancellationToken: ct);
        var database = databaseResponse.Database;

        await database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(options.NodesContainerName, "/namespace")
            {
                DefaultTimeToLive = -1
            }, cancellationToken: ct);

        await database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(options.PartitionsContainerName, "/partitionKey")
            {
                DefaultTimeToLive = -1
            }, cancellationToken: ct);

        logger?.LogInformation("Cosmos DB containers '{Nodes}' and '{Partitions}' are ready",
            options.NodesContainerName, options.PartitionsContainerName);

        return database;
    }
}
