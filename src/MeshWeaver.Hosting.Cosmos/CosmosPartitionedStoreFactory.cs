using System.Net;
using System.Text.RegularExpressions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Azure.Cosmos;

namespace MeshWeaver.Hosting.Cosmos;

/// <summary>
/// Factory for creating per-partition Cosmos DB persistence stores.
/// Each partition gets its own container pair ({segment}-nodes, {segment}-partitions).
/// </summary>
public partial class CosmosPartitionedStoreFactory : IPartitionedStoreFactory
{
    private readonly CosmosClient _cosmosClient;
    private readonly Database _database;
    private readonly IDataChangeNotifier? _changeNotifier;
    private readonly MeshConfiguration? _meshConfiguration;
    private readonly int _throughput;

    public CosmosPartitionedStoreFactory(
        CosmosClient cosmosClient,
        string databaseName,
        IDataChangeNotifier? changeNotifier = null,
        MeshConfiguration? meshConfiguration = null,
        int throughput = 400)
    {
        _cosmosClient = cosmosClient;
        _database = cosmosClient.GetDatabase(databaseName);
        _changeNotifier = changeNotifier;
        _meshConfiguration = meshConfiguration;
        _throughput = throughput;
    }

    public async Task<PartitionedStore> CreateStoreAsync(string firstSegment, CancellationToken ct = default)
    {
        var sanitized = SanitizeContainerName(firstSegment);
        var nodesContainerName = $"{sanitized}-nodes";
        var partitionsContainerName = $"{sanitized}-partitions";

        // Create containers if not exists (idempotent, with retry for transient 503)
        await CreateContainerWithRetryAsync(
            new ContainerProperties(nodesContainerName, "/namespace") { DefaultTimeToLive = -1 }, ct);
        await CreateContainerWithRetryAsync(
            new ContainerProperties(partitionsContainerName, "/partitionKey") { DefaultTimeToLive = -1 }, ct);

        var nodesContainer = _database.GetContainer(nodesContainerName);
        var partitionsContainer = _database.GetContainer(partitionsContainerName);

        var adapter = new CosmosStorageAdapter(nodesContainer, partitionsContainer);

        // Create persistence core and query provider
        var core = new InMemoryPersistenceService(adapter, _changeNotifier);
        var queryProvider = new CosmosMeshQuery(adapter, _changeNotifier, _meshConfiguration);

        return new PartitionedStore(core, queryProvider);
    }

    public async Task<IReadOnlyList<string>> DiscoverPartitionsAsync(CancellationToken ct = default)
    {
        var partitions = new List<string>();

        // List containers and identify partition pairs by naming convention
        using var iterator = _database.GetContainerQueryIterator<ContainerProperties>(
            "SELECT * FROM c");

        var nodesContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            foreach (var container in response)
            {
                // Look for containers matching the "{segment}-nodes" pattern
                if (container.Id.EndsWith("-nodes", StringComparison.OrdinalIgnoreCase))
                {
                    var segment = container.Id[..^"-nodes".Length];
                    nodesContainers.Add(segment);
                }
            }
        }

        partitions.AddRange(nodesContainers.OrderBy(s => s));
        return partitions;
    }

    /// <summary>
    /// Sanitizes a partition name into a valid Cosmos DB container name.
    /// Container names: 3-63 chars, lowercase letters, numbers, hyphens.
    /// </summary>
    public static string SanitizeContainerName(string segment)
    {
        // Lowercase and replace non-alphanumeric (except hyphen) with hyphen
        var sanitized = ContainerNameRegex().Replace(segment.ToLowerInvariant(), "-");
        // Remove leading/trailing hyphens
        sanitized = sanitized.Trim('-');
        // Ensure minimum length
        if (sanitized.Length < 3)
            sanitized = sanitized.PadRight(3, '0');
        // Truncate to fit with suffix (63 - "-partitions".Length = 52)
        if (sanitized.Length > 52)
            sanitized = sanitized[..52];
        return sanitized;
    }

    private async Task CreateContainerWithRetryAsync(
        ContainerProperties properties, CancellationToken ct)
    {
        const int maxRetries = 10;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _database.CreateContainerIfNotExistsAsync(properties, cancellationToken: ct);
                return;
            }
            catch (CosmosException ex) when (
                ex.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < maxRetries)
            {
                // Cosmos DB (including the emulator) can return 503 when partition services
                // are overloaded. Use exponential backoff with jitter.
                await Task.Delay(1000 * (attempt + 1), ct);
            }
        }
    }

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex ContainerNameRegex();
}
