using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
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
        path?.Trim('/') ?? "";

    public async Task<MeshNode?> ReadAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
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

    public async Task WriteAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
    {
        // Ensure namespace is never null — Cosmos partition key /namespace requires the field to be present
        var nodeToWrite = node.Namespace is null ? node with { Namespace = "" } : node;
        // The CosmosClient is configured with UseSystemTextJsonSerializerWithOptions,
        // so UpsertItemAsync uses System.Text.Json with the hub's serialization pipeline
        await _nodesContainer.UpsertItemAsync(
            nodeToWrite,
            new PartitionKey(nodeToWrite.Namespace),
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
        using var iterator = _nodesContainer.GetItemQueryIterator<JsonElement>(query);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            foreach (var item in response)
            {
                var ns = item.TryGetProperty("namespace", out var nsProp) ? nsProp.GetString() ?? "" : "";
                var id = item.GetProperty("id").GetString()!;
                var path = string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}";
                paths.Add(path);
            }
        }

        // Cosmos doesn't have directory concept, so we return only node paths
        return (paths, Enumerable.Empty<string>());
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var node = await ReadAsync(path, new JsonSerializerOptions(), ct);
        return node != null;
    }

    #region Partition Storage

    public async IAsyncEnumerable<object> GetPartitionObjectsAsync(
        string nodePath,
        string? subPath,
        JsonSerializerOptions options,
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
        JsonSerializerOptions options,
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

        using var iterator = _partitionsContainer.GetItemQueryIterator<JsonElement>(query);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            foreach (var item in response)
            {
                try
                {
                    var id = item.GetProperty("id").GetString()!;
                    await _partitionsContainer.DeleteItemAsync<object>(
                        id,
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

        using var iterator = _partitionsContainer.GetItemQueryIterator<JsonElement>(query);

        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            foreach (var item in response)
            {
                if (item.TryGetProperty("maxTime", out var maxTimeProp) && maxTimeProp.ValueKind != JsonValueKind.Null)
                {
                    return DateTimeOffset.Parse(maxTimeProp.GetString()!);
                }
            }
        }

        return null;
    }

    #endregion

    #region Query Support

    /// <summary>
    /// Queries nodes using RSQL syntax, translated to Cosmos DB SQL.
    /// Now supports ORDER BY, LIMIT, and scope-based path filtering.
    /// </summary>
    public async IAsyncEnumerable<MeshNode> QueryNodesAsync(
        ParsedQuery query,
        string? basePath = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (sql, parameters) = _sqlGenerator.GenerateSelectQuery(query);

        // Integrate scope-based path filtering if path is specified
        var effectivePath = query.Path ?? basePath;
        if (!string.IsNullOrEmpty(effectivePath))
        {
            var (scopeClause, scopeParams) = _sqlGenerator.GenerateScopeClause(
                effectivePath,
                query.Scope);

            if (!string.IsNullOrEmpty(scopeClause))
            {
                // Merge scope parameters
                foreach (var (k, v) in scopeParams)
                    parameters[k] = v;

                // Insert scope clause into SQL
                if (sql.Contains("WHERE"))
                {
                    sql = sql.Replace("WHERE", $"WHERE {scopeClause} AND");
                }
                else if (sql.Contains("ORDER BY"))
                {
                    sql = sql.Replace("ORDER BY", $"WHERE {scopeClause} ORDER BY");
                }
                else
                {
                    sql += $" WHERE {scopeClause}";
                }
            }
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

    /// <summary>
    /// Performs vector similarity search for semantic queries.
    /// </summary>
    /// <param name="queryVector">The query embedding vector</param>
    /// <param name="filter">Optional filter to apply before vector search</param>
    /// <param name="namespacePath">Optional namespace path to scope the search</param>
    /// <param name="topK">Number of results to return (default: 10)</param>
    /// <param name="embeddingField">The field containing embeddings (default: "embedding")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Stream of matching nodes ordered by similarity</returns>
    public async IAsyncEnumerable<MeshNode> VectorSearchAsync(
        float[] queryVector,
        ParsedQuery? filter = null,
        string? namespacePath = null,
        int topK = 10,
        string embeddingField = "embedding",
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (sql, parameters) = _sqlGenerator.GenerateVectorSearchQuery(
            filter,
            queryVector,
            topK,
            embeddingField);

        // Add namespace scoping if specified
        if (!string.IsNullOrEmpty(namespacePath))
        {
            var normalizedPath = NormalizePath(namespacePath);
            parameters["@nsPrefix"] = $"{normalizedPath}/";

            if (sql.Contains("WHERE"))
            {
                // Insert after existing WHERE
                sql = sql.Replace("WHERE", "WHERE STARTSWITH(c.path, @nsPrefix) AND");
            }
            else
            {
                // Insert before ORDER BY
                sql = sql.Replace("ORDER BY", "WHERE STARTSWITH(c.path, @nsPrefix) ORDER BY");
            }
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
