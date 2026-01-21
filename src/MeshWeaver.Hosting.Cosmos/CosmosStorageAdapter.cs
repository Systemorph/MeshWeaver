using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;

using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Cosmos;

/// <summary>
/// Cosmos DB implementation of IStorageAdapter.
/// Stores MeshNodes and partition objects in Cosmos DB containers.
/// </summary>
public class CosmosStorageAdapter : IStorageAdapter, IAsyncDisposable
{
    private readonly Container _nodesContainer;
    private readonly Container _partitionsContainer;
    private readonly CosmosSqlGenerator _sqlGenerator = new();
    private readonly JsonSerializerOptions _jsonOptions;
    private CosmosChangeFeedProcessor? _changeFeedProcessor;

    /// <summary>
    /// Gets the nodes container for external use (e.g., change feed processing).
    /// </summary>
    public Container NodesContainer => _nodesContainer;

    /// <summary>
    /// Gets the partitions container.
    /// </summary>
    public Container PartitionsContainer => _partitionsContainer;

    public CosmosStorageAdapter(
        Container nodesContainer,
        Container partitionsContainer)
    {
        _nodesContainer = nodesContainer;
        _partitionsContainer = partitionsContainer;
        _jsonOptions = PersistenceJsonOptions.CreateForPersistence();
    }

    /// <summary>
    /// Attaches a change feed processor to this storage adapter.
    /// </summary>
    /// <param name="processor">The change feed processor to attach.</param>
    public void AttachChangeFeedProcessor(CosmosChangeFeedProcessor processor)
    {
        _changeFeedProcessor = processor;
    }

    /// <summary>
    /// Starts the attached change feed processor if one is attached.
    /// </summary>
    public async Task StartChangeFeedProcessorAsync(CancellationToken cancellationToken = default)
    {
        if (_changeFeedProcessor != null)
        {
            await _changeFeedProcessor.StartAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Stops the attached change feed processor if one is attached.
    /// </summary>
    public async Task StopChangeFeedProcessorAsync()
    {
        if (_changeFeedProcessor != null)
        {
            await _changeFeedProcessor.StopAsync();
        }
    }

    private static string NormalizePath(string? path) =>
        path?.Trim('/').ToLowerInvariant() ?? "";

    public async Task<MeshNode?> ReadAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath))
            return null;

        // Extract namespace (all but last segment) and id (last segment)
        var lastSlash = normalizedPath.LastIndexOf('/');
        var ns = lastSlash > 0 ? normalizedPath[..lastSlash] : "";
        var id = lastSlash > 0 ? normalizedPath[(lastSlash + 1)..] : normalizedPath;

        try
        {
            var response = await _nodesContainer.ReadItemAsync<MeshNode>(
                id,
                new PartitionKey(ns),
                cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task WriteAsync(MeshNode node, CancellationToken ct = default)
    {
        // Serialize manually to apply NotMapped property exclusion
        var json = JsonSerializer.Serialize(node, _jsonOptions);
        using var document = JsonDocument.Parse(json);
        await _nodesContainer.UpsertItemAsync(
            document.RootElement,
            new PartitionKey(node.Namespace ?? ""),
            cancellationToken: ct);
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath))
            return;

        // Extract namespace (all but last segment) and id (last segment)
        var lastSlash = normalizedPath.LastIndexOf('/');
        var ns = lastSlash > 0 ? normalizedPath[..lastSlash] : "";
        var id = lastSlash > 0 ? normalizedPath[(lastSlash + 1)..] : normalizedPath;

        try
        {
            await _nodesContainer.DeleteItemAsync<MeshNode>(
                id,
                new PartitionKey(ns),
                cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already deleted
        }
    }

    public async Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(
        string? parentPath,
        CancellationToken ct = default)
    {
        var normalizedParent = NormalizePath(parentPath);
        var depth = string.IsNullOrEmpty(normalizedParent)
            ? 1
            : normalizedParent.Split('/').Length + 1;

        // Query for direct children of the parent path
        var query = string.IsNullOrEmpty(normalizedParent)
            ? new QueryDefinition("SELECT c.id, c.namespace FROM c WHERE c.namespace = ''")
            : new QueryDefinition("SELECT c.id, c.namespace FROM c WHERE c.namespace = @parent")
                .WithParameter("@parent", normalizedParent);

        var paths = new List<string>();
        using var iterator = _nodesContainer.GetItemQueryIterator<dynamic>(query);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            foreach (var item in response)
            {
                var ns = (string?)item.@namespace ?? "";
                var id = (string)item.id;
                var path = string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}";
                paths.Add(path);
            }
        }

        // Cosmos doesn't have directory concept, so we return only node paths
        return (paths, Enumerable.Empty<string>());
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var node = await ReadAsync(path, ct);
        return node != null;
    }

    #region Partition Storage

    public async IAsyncEnumerable<object> GetPartitionObjectsAsync(
        string nodePath,
        string? subPath = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var partitionKey = GetPartitionStorageKey(nodePath, subPath);

        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.partitionKey = @partitionKey")
            .WithParameter("@partitionKey", partitionKey);

        using var iterator = _partitionsContainer.GetItemQueryIterator<JsonElement>(query);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            foreach (var item in response)
            {
                // Deserialize using $type discriminator if present
                yield return item;
            }
        }
    }

    public async Task SavePartitionObjectsAsync(
        string nodePath,
        string? subPath,
        IReadOnlyCollection<object> objects,
        CancellationToken ct = default)
    {
        var partitionKey = GetPartitionStorageKey(nodePath, subPath);

        // Delete existing objects first
        await DeletePartitionObjectsAsync(nodePath, subPath, ct);

        // Insert new objects
        foreach (var obj in objects)
        {
            var id = GetObjectId(obj);
            var document = new
            {
                id,
                partitionKey,
                data = obj,
                type = obj.GetType().AssemblyQualifiedName,
                lastModified = DateTimeOffset.UtcNow
            };

            await _partitionsContainer.UpsertItemAsync(
                document,
                new PartitionKey(partitionKey),
                cancellationToken: ct);
        }
    }

    public async Task DeletePartitionObjectsAsync(
        string nodePath,
        string? subPath = null,
        CancellationToken ct = default)
    {
        var partitionKey = GetPartitionStorageKey(nodePath, subPath);

        var query = new QueryDefinition(
            "SELECT c.id FROM c WHERE c.partitionKey = @partitionKey")
            .WithParameter("@partitionKey", partitionKey);

        using var iterator = _partitionsContainer.GetItemQueryIterator<dynamic>(query);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            foreach (var item in response)
            {
                try
                {
                    await _partitionsContainer.DeleteItemAsync<object>(
                        (string)item.id,
                        new PartitionKey(partitionKey),
                        cancellationToken: ct);
                }
                catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Already deleted
                }
            }
        }
    }

    public async Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(
        string nodePath,
        string? subPath = null,
        CancellationToken ct = default)
    {
        var partitionKey = GetPartitionStorageKey(nodePath, subPath);

        var query = new QueryDefinition(
            "SELECT MAX(c.lastModified) as maxTime FROM c WHERE c.partitionKey = @partitionKey")
            .WithParameter("@partitionKey", partitionKey);

        using var iterator = _partitionsContainer.GetItemQueryIterator<dynamic>(query);

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            var result = response.FirstOrDefault();
            if (result?.maxTime != null)
            {
                return DateTimeOffset.Parse((string)result.maxTime);
            }
        }

        return null;
    }

    #endregion

    #region Query Support

    /// <summary>
    /// Queries nodes using RSQL syntax, translated to Cosmos DB SQL.
    /// </summary>
    public async IAsyncEnumerable<MeshNode> QueryNodesAsync(
        ParsedQuery query,
        string? basePath = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (sql, parameters) = _sqlGenerator.GenerateSelectQuery(query);

        // Add path filter if specified
        if (!string.IsNullOrEmpty(basePath))
        {
            var normalizedPath = NormalizePath(basePath);
            sql = sql.Replace("WHERE", $"WHERE STARTSWITH(c.namespace, @basePath) AND");
            if (!sql.Contains("WHERE"))
                sql += " WHERE STARTSWITH(c.namespace, @basePath)";
            parameters["@basePath"] = normalizedPath;
        }

        var queryDefinition = new QueryDefinition(sql);
        foreach (var (name, value) in parameters)
        {
            queryDefinition = queryDefinition.WithParameter(name, value);
        }

        using var iterator = _nodesContainer.GetItemQueryIterator<MeshNode>(queryDefinition);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            foreach (var node in response)
            {
                yield return node;
            }
        }
    }

    #endregion

    private static string GetPartitionStorageKey(string nodePath, string? subPath)
    {
        var key = NormalizePath(nodePath);
        if (!string.IsNullOrEmpty(subPath))
        {
            key = $"{key}/{NormalizePath(subPath)}";
        }
        return key;
    }

    private static string GetObjectId(object obj)
    {
        // Try to get Id property via reflection
        var idProp = obj.GetType().GetProperty("Id") ?? obj.GetType().GetProperty("id");
        var id = idProp?.GetValue(obj)?.ToString();
        return id ?? Guid.NewGuid().ToString();
    }

    public async ValueTask DisposeAsync()
    {
        // Stop and dispose the change feed processor if attached
        if (_changeFeedProcessor != null)
        {
            await _changeFeedProcessor.DisposeAsync();
            _changeFeedProcessor = null;
        }

        // CosmosClient is typically shared and disposed elsewhere
    }
}
