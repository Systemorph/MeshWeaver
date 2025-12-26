using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.AzureStorage;

/// <summary>
/// Azure Blob Storage implementation of IStorageAdapter.
/// Stores MeshNodes and partition objects as JSON blobs in a container.
/// </summary>
public class AzureBlobStorageAdapter : IStorageAdapter
{
    private readonly BlobContainerClient _containerClient;
    private readonly JsonSerializerOptions _jsonOptions;

    public AzureBlobStorageAdapter(
        BlobContainerClient containerClient,
        JsonSerializerOptions? jsonOptions = null)
    {
        _containerClient = containerClient;
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
    }

    private static string NormalizePath(string? path) =>
        path?.Trim('/').ToLowerInvariant() ?? "";

    private static string GetBlobPath(string path) =>
        $"nodes/{NormalizePath(path)}.json";

    public async Task<MeshNode?> ReadAsync(string path, CancellationToken ct = default)
    {
        var blobPath = GetBlobPath(path);
        var blobClient = _containerClient.GetBlobClient(blobPath);

        try
        {
            var response = await blobClient.DownloadContentAsync(ct);
            var node = JsonSerializer.Deserialize<MeshNode>(
                response.Value.Content.ToString(),
                _jsonOptions);

            if (node != null)
            {
                // Use blob last modified time
                var properties = await blobClient.GetPropertiesAsync(cancellationToken: ct);
                if (node.LastModified == default && properties.Value.LastModified != default)
                {
                    node = node with { LastModified = properties.Value.LastModified };
                }
            }

            return node;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task WriteAsync(MeshNode node, CancellationToken ct = default)
    {
        var key = NormalizePath(node.Path);
        var nodeToSave = node with
        {
            LastModified = node.LastModified == default ? DateTimeOffset.UtcNow : node.LastModified
        };

        var blobPath = GetBlobPath(key);
        var blobClient = _containerClient.GetBlobClient(blobPath);

        var json = JsonSerializer.Serialize(nodeToSave, _jsonOptions);
        await blobClient.UploadAsync(
            BinaryData.FromString(json),
            overwrite: true,
            cancellationToken: ct);
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var blobPath = GetBlobPath(path);
        var blobClient = _containerClient.GetBlobClient(blobPath);

        await blobClient.DeleteIfExistsAsync(cancellationToken: ct);
    }

    public async Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(
        string? parentPath,
        CancellationToken ct = default)
    {
        var normalizedParent = NormalizePath(parentPath);
        var prefix = string.IsNullOrEmpty(normalizedParent) ? "nodes/" : $"nodes/{normalizedParent}/";

        var nodePaths = new List<string>();
        var directoryPaths = new HashSet<string>();

        await foreach (var blobItem in _containerClient.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
        {
            // Remove "nodes/" prefix and ".json" suffix
            var fullPath = blobItem.Name;
            if (!fullPath.StartsWith("nodes/") || !fullPath.EndsWith(".json"))
                continue;

            var path = fullPath[6..^5]; // Remove "nodes/" and ".json"

            // Check if this is a direct child
            var relativePath = string.IsNullOrEmpty(normalizedParent)
                ? path
                : path.StartsWith(normalizedParent + "/")
                    ? path[(normalizedParent.Length + 1)..]
                    : null;

            if (relativePath == null)
                continue;

            var slashIndex = relativePath.IndexOf('/');
            if (slashIndex == -1)
            {
                // Direct child node
                nodePaths.Add(path);
            }
            else
            {
                // This is a deeper path - record the immediate child directory
                var childDir = string.IsNullOrEmpty(normalizedParent)
                    ? relativePath[..slashIndex]
                    : $"{normalizedParent}/{relativePath[..slashIndex]}";
                directoryPaths.Add(childDir);
            }
        }

        return (nodePaths, directoryPaths);
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var blobPath = GetBlobPath(path);
        var blobClient = _containerClient.GetBlobClient(blobPath);

        var response = await blobClient.ExistsAsync(ct);
        return response.Value;
    }

    #region Partition Storage

    private static string GetPartitionBlobPath(string nodePath, string? subPath, string objectId) =>
        string.IsNullOrEmpty(subPath)
            ? $"partitions/{NormalizePath(nodePath)}/{objectId}.json"
            : $"partitions/{NormalizePath(nodePath)}/{NormalizePath(subPath)}/{objectId}.json";

    private static string GetPartitionPrefix(string nodePath, string? subPath) =>
        string.IsNullOrEmpty(subPath)
            ? $"partitions/{NormalizePath(nodePath)}/"
            : $"partitions/{NormalizePath(nodePath)}/{NormalizePath(subPath)}/";

    public async IAsyncEnumerable<object> GetPartitionObjectsAsync(
        string nodePath,
        string? subPath = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var prefix = GetPartitionPrefix(nodePath, subPath);

        await foreach (var blobItem in _containerClient.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
        {
            if (!blobItem.Name.EndsWith(".json"))
                continue;

            var blobClient = _containerClient.GetBlobClient(blobItem.Name);

            BinaryData content;
            try
            {
                var response = await blobClient.DownloadContentAsync(ct);
                content = response.Value.Content;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                continue;
            }

            // Return as JsonElement - the caller can deserialize based on $type if needed
            var element = JsonSerializer.Deserialize<JsonElement>(content.ToString(), _jsonOptions);
            yield return element;
        }
    }

    public async Task SavePartitionObjectsAsync(
        string nodePath,
        string? subPath,
        IReadOnlyCollection<object> objects,
        CancellationToken ct = default)
    {
        // Delete existing objects first
        await DeletePartitionObjectsAsync(nodePath, subPath, ct);

        // Save new objects
        foreach (var obj in objects)
        {
            var id = GetObjectId(obj);
            var blobPath = GetPartitionBlobPath(nodePath, subPath, id);
            var blobClient = _containerClient.GetBlobClient(blobPath);

            var wrapper = new
            {
                id,
                type = obj.GetType().AssemblyQualifiedName,
                data = obj,
                lastModified = DateTimeOffset.UtcNow
            };

            var json = JsonSerializer.Serialize(wrapper, _jsonOptions);
            await blobClient.UploadAsync(
                BinaryData.FromString(json),
                overwrite: true,
                cancellationToken: ct);
        }
    }

    public async Task DeletePartitionObjectsAsync(
        string nodePath,
        string? subPath = null,
        CancellationToken ct = default)
    {
        var prefix = GetPartitionPrefix(nodePath, subPath);

        await foreach (var blobItem in _containerClient.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
        {
            var blobClient = _containerClient.GetBlobClient(blobItem.Name);
            await blobClient.DeleteIfExistsAsync(cancellationToken: ct);
        }
    }

    public async Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(
        string nodePath,
        string? subPath = null,
        CancellationToken ct = default)
    {
        var prefix = GetPartitionPrefix(nodePath, subPath);
        DateTimeOffset? maxTimestamp = null;

        await foreach (var blobItem in _containerClient.GetBlobsAsync(
            traits: BlobTraits.Metadata,
            prefix: prefix,
            cancellationToken: ct))
        {
            if (blobItem.Properties.LastModified.HasValue)
            {
                var timestamp = blobItem.Properties.LastModified.Value;
                if (maxTimestamp == null || timestamp > maxTimestamp)
                {
                    maxTimestamp = timestamp;
                }
            }
        }

        return maxTimestamp;
    }

    #endregion

    private static string GetObjectId(object obj)
    {
        var idProp = obj.GetType().GetProperty("Id") ?? obj.GetType().GetProperty("id");
        var id = idProp?.GetValue(obj)?.ToString();
        return id ?? Guid.NewGuid().ToString();
    }
}
