using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeshWeaver.Domain;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

#pragma warning disable CS8618 // Required properties may be uninitialized in partial classes

namespace MeshWeaver.Hosting.Cosmos;

/// <summary>
/// Factory for creating CosmosStorageAdapter instances from IOptions&lt;CosmosStorageOptions&gt;.
/// </summary>
public class CosmosStorageAdapterFactory(
    IOptions<CosmosStorageOptions> options) : IStorageAdapterFactory
{
    public const string StorageType = "Cosmos";

    public IStorageAdapter Create(GraphStorageConfig config, IServiceProvider serviceProvider)
    {
        var opts = options.Value;
        var logger = serviceProvider.GetService<ILogger<CosmosStorageAdapterFactory>>();

        // Prefer Aspire-injected keyed containers (registered via AddAzureCosmosDatabase + AddKeyedContainer)
        logger?.LogDebug("Resolving keyed Container '{Name}'", opts.NodesContainerName);
        var nodesContainer = serviceProvider.GetKeyedService<Container>(opts.NodesContainerName);
        logger?.LogDebug("nodes={Found}, resolving '{Name}'", nodesContainer != null, opts.PartitionsContainerName);
        var partitionsContainer = serviceProvider.GetKeyedService<Container>(opts.PartitionsContainerName);
        logger?.LogDebug("partitions={Found}", partitionsContainer != null);

        if (nodesContainer != null && partitionsContainer != null)
            return new CosmosStorageAdapter(nodesContainer, partitionsContainer);

        // Fallback: create CosmosClient manually from connection string (non-Aspire scenarios)
        var connectionString = opts.ConnectionString
            ?? config.ConnectionString
            ?? throw new InvalidOperationException(
                "Cosmos DB containers not found in DI. " +
                "Register via Aspire (AddAzureCosmosDatabase + AddKeyedContainer), " +
                "or set CosmosStorageOptions.ConnectionString.");

        var typeRegistry = serviceProvider.GetService<ITypeRegistry>();
        var jsonOptions = StorageImporter.CreateFullImportOptions(typeRegistry);
        var cosmosClient = new CosmosClient(connectionString, new CosmosClientOptions
        {
            UseSystemTextJsonSerializerWithOptions = jsonOptions
        });

        var database = cosmosClient.GetDatabase(opts.DatabaseName);
        return new CosmosStorageAdapter(
            database.GetContainer(opts.NodesContainerName),
            database.GetContainer(opts.PartitionsContainerName));
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

        // Register CosmosMeshQuery so it takes priority over InMemoryMeshQuery (via TryAddSingleton)
        services.AddSingleton<IMeshQueryProvider>(sp =>
        {
            var adapter = sp.GetRequiredService<IStorageAdapter>() as CosmosStorageAdapter
                ?? throw new InvalidOperationException(
                    "CosmosMeshQuery requires CosmosStorageAdapter. " +
                    "Ensure Cosmos storage is configured.");
            var changeNotifier = sp.GetService<IDataChangeNotifier>();
            var meshConfig = sp.GetService<MeshConfiguration>();
            return new CosmosMeshQuery(adapter, changeNotifier, meshConfig);
        });

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
        return services.AddCosmosPersistence(storageAdapter);
    }

    /// <summary>
    /// Adds Cosmos DB persistence services using a pre-configured storage adapter.
    /// Registers CosmosMeshQuery for native Cosmos SQL queries.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="storageAdapter">The Cosmos storage adapter</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddCosmosPersistence(
        this IServiceCollection services,
        CosmosStorageAdapter storageAdapter)
    {
        // Register CosmosMeshQuery BEFORE AddPersistence so TryAddSingleton picks it up
        services.AddSingleton<IMeshQueryProvider>(sp =>
            new CosmosMeshQuery(storageAdapter, sp.GetService<IDataChangeNotifier>(), sp.GetService<MeshConfiguration>()));

        services.AddPersistence(storageAdapter);

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

        // Register the storage adapter
        var storageAdapter = new CosmosStorageAdapter(nodesContainer, partitionsContainer);
        services.AddSingleton(storageAdapter); // Register as concrete type for change feed setup

        // Register CosmosMeshQuery BEFORE AddPersistence so TryAddSingleton doesn't override it
        services.AddSingleton<IMeshQueryProvider>(sp =>
            new CosmosMeshQuery(
                storageAdapter,
                sp.GetService<IDataChangeNotifier>(),
                sp.GetService<MeshConfiguration>()));

        // Register core persistence services (IStorageAdapter, IPersistenceServiceCore, etc.)
        services.AddPersistence(storageAdapter);

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
    /// Adds partitioned Cosmos DB persistence where each top-level path segment
    /// gets its own container pair ({segment}-nodes, {segment}-partitions).
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="cosmosClient">The Cosmos DB client</param>
    /// <param name="databaseName">The database name</param>
    /// <param name="throughput">The throughput for containers (default: 400)</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPartitionedCosmosPersistence(
        this IServiceCollection services,
        CosmosClient cosmosClient,
        string databaseName,
        int throughput = 400)
    {
        services.AddSingleton<IDataChangeNotifier, DataChangeNotifier>();

        services.AddSingleton<IPartitionedStoreFactory>(sp =>
            new CosmosPartitionedStoreFactory(
                cosmosClient,
                databaseName,
                sp.GetService<IDataChangeNotifier>(),
                sp.GetService<MeshConfiguration>(),
                throughput));

        services.AddPartitionedCoreAndWrapperServices();

        return services;
    }

}
