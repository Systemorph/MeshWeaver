using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

#pragma warning disable CS8618 // Required properties may be uninitialized in partial classes

namespace MeshWeaver.Hosting.Cosmos;

/// <summary>
/// Factory for creating CosmosStorageAdapter instances from IOptions&lt;CosmosStorageOptions&gt;.
/// </summary>
public class CosmosStorageAdapterFactory(IOptions<CosmosStorageOptions> options) : IStorageAdapterFactory
{
    public const string StorageType = "Cosmos";

    public IStorageAdapter Create(GraphStorageConfig config, IServiceProvider serviceProvider)
    {
        var opts = options.Value;

        var connectionString = opts.ConnectionString
            ?? config.ConnectionString
            ?? throw new InvalidOperationException(
                "Cosmos DB connection string is not configured. " +
                "Set CosmosStorageOptions.ConnectionString or Graph:Storage:ConnectionString.");

        var cosmosClient = new CosmosClient(connectionString);
        var database = cosmosClient.GetDatabase(opts.DatabaseName);
        var nodesContainer = database.GetContainer(opts.NodesContainerName);
        var partitionsContainer = database.GetContainer(opts.PartitionsContainerName);

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
    public static IServiceCollection AddCosmosStorageFactory(
        this IServiceCollection services, Action<CosmosStorageOptions>? configure = null)
    {
        if (configure != null)
            services.Configure(configure);
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

    /// <summary>
    /// Creates container properties with vector index support for semantic search.
    /// </summary>
    /// <param name="containerName">The container name</param>
    /// <param name="partitionKeyPath">The partition key path</param>
    /// <param name="embeddingPath">Path to the embedding property (default: "/embedding")</param>
    /// <param name="dimensions">Vector dimensions (default: 1536 for OpenAI text-embedding-3-small)</param>
    /// <param name="dataType">Vector data type (default: Float32)</param>
    /// <param name="distanceFunction">Distance function for similarity (default: Cosine)</param>
    /// <param name="indexType">Vector index type (default: DiskANN)</param>
    /// <returns>Container properties configured for vector search</returns>
    public static ContainerProperties CreateVectorEnabledContainerProperties(
        string containerName,
        string partitionKeyPath,
        string embeddingPath = "/embedding",
        int dimensions = 1536,
        VectorDataType dataType = VectorDataType.Float32,
        DistanceFunction distanceFunction = DistanceFunction.Cosine,
        VectorIndexType indexType = VectorIndexType.DiskANN)
    {
        var containerProperties = new ContainerProperties(containerName, partitionKeyPath)
        {
            DefaultTimeToLive = -1,
            VectorEmbeddingPolicy = new VectorEmbeddingPolicy(
            [
                new Embedding
                {
                    Path = embeddingPath,
                    DataType = dataType,
                    DistanceFunction = distanceFunction,
                    Dimensions = dimensions
                }
            ]),
            IndexingPolicy = new IndexingPolicy
            {
                VectorIndexes =
                [
                    new VectorIndexPath
                    {
                        Path = embeddingPath,
                        Type = indexType
                    }
                ]
            }
        };

        return containerProperties;
    }

    /// <summary>
    /// Adds Cosmos DB persistence services with vector search support and automatic container creation.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="cosmosClient">The Cosmos DB client</param>
    /// <param name="databaseName">The database name</param>
    /// <param name="throughput">The throughput for containers</param>
    /// <param name="vectorDimensions">Vector dimensions for embeddings (default: 1536)</param>
    /// <returns>The service collection for chaining</returns>
    public static async Task<IServiceCollection> AddCosmosPersistenceWithVectorSearchAsync(
        this IServiceCollection services,
        CosmosClient cosmosClient,
        string databaseName,
        int throughput = 400,
        int vectorDimensions = 1536)
    {
        // Create database if not exists
        var databaseResponse = await cosmosClient.CreateDatabaseIfNotExistsAsync(
            databaseName,
            ThroughputProperties.CreateManualThroughput(throughput));
        var database = databaseResponse.Database;

        // Create nodes container with vector index support
        var nodesContainerProperties = CreateVectorEnabledContainerProperties(
            "nodes",
            "/key",
            "/embedding",
            vectorDimensions);

        await database.CreateContainerIfNotExistsAsync(nodesContainerProperties);

        // Create partitions container (standard, no vector support needed)
        await database.CreateContainerIfNotExistsAsync(
            new ContainerProperties("partitions", "/partitionKey")
            {
                DefaultTimeToLive = -1
            });

        return services.AddCosmosPersistence(cosmosClient, databaseName);
    }

    /// <summary>
    /// Registers the Cosmos DB data seeder hosted service.
    /// The seeder reads data from the file system and writes it to Cosmos DB at startup.
    /// </summary>
    public static IServiceCollection AddCosmosSeeding(
        this IServiceCollection services, Action<CosmosSeederOptions> configure)
    {
        services.Configure(configure);
        services.AddHostedService<CosmosDataSeeder>();
        return services;
    }
}
