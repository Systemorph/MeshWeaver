using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.AzureStorage;

/// <summary>
/// Azure Blob Storage implementation of IStorageAdapter.
/// Stores MeshNodes and partition objects in a container.
/// Supports multiple file formats: .md (with YAML front matter), .json
/// </summary>
public class AzureBlobStorageAdapter : IStorageAdapter
{
    private readonly BlobContainerClient _containerClient;
    private readonly FileFormatParserRegistry _parserRegistry = new();

    /// <summary>
    /// Supported file extensions in priority order for reading.
    /// </summary>
    private static readonly string[] SupportedExtensions = [".md", ".json"];

    public AzureBlobStorageAdapter(BlobContainerClient containerClient)
    {
        _containerClient = containerClient;
    }

    private static string NormalizePath(string? path) =>
        path?.Trim('/').ToLowerInvariant() ?? "";

    private static string GetBlobPath(string path, string extension) =>
        $"nodes/{NormalizePath(path)}{extension}";

    /// <summary>
    /// Finds a blob with any supported extension for the given path.
    /// Returns the first matching blob in priority order: .md, .json
    /// </summary>
    private async Task<(BlobClient? Client, string Extension)> FindBlobWithExtensionAsync(string path, CancellationToken ct)
    {
        foreach (var ext in SupportedExtensions)
        {
            var blobPath = GetBlobPath(path, ext);
            var blobClient = _containerClient.GetBlobClient(blobPath);

            try
            {
                var exists = await blobClient.ExistsAsync(ct);
                if (exists.Value)
                    return (blobClient, ext);
            }
            catch (Azure.RequestFailedException)
            {
                // Continue to next extension
            }
        }

        return (null, ".json");
    }

    public async Task<MeshNode?> ReadAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var (blobClient, extension) = await FindBlobWithExtensionAsync(path, ct);
        if (blobClient == null)
            return null;

        try
        {
            var response = await blobClient.DownloadContentAsync(ct);
            var content = response.Value.Content.ToString();

            // Try to use parsers for non-JSON formats (with fallback support for .md files)
            MeshNode? node;
            var parsers = _parserRegistry.GetParsers(extension);
            if (parsers.Count > 0)
            {
                // Use TryParseAsync for fallback support (e.g., AgentFileParser -> MarkdownFileParser)
                node = await _parserRegistry.TryParseAsync(extension, blobClient.Name, content, path, ct);
            }
            else
            {
                // Default to JSON deserialization
                node = JsonSerializer.Deserialize<MeshNode>(content, options);
            }

            if (node != null)
            {
                // Use blob last modified time if not set
                var properties = await blobClient.GetPropertiesAsync(cancellationToken: ct);
                if (node.LastModified == default && properties.Value.LastModified != default)
                {
                    node = node with { LastModified = properties.Value.LastModified };
                }

                // Derive namespace from path if not set
                var normalizedPath = path.Trim('/');
                var lastSlash = normalizedPath.LastIndexOf('/');
                if (lastSlash > 0 && string.IsNullOrEmpty(node.Namespace))
                {
                    var derivedNamespace = normalizedPath[..lastSlash];
                    node = node with { Namespace = derivedNamespace };
                }
            }

            return node;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task WriteAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var key = NormalizePath(node.Path);
        var nodeToSave = node with
        {
            LastModified = node.LastModified == default ? DateTimeOffset.UtcNow : node.LastModified
        };

        string content;
        string extension;

        // Check if we have a serializer for this node type (e.g., Markdown)
        var serializer = _parserRegistry.GetSerializerFor(nodeToSave);
        if (serializer != null)
        {
            content = await serializer.SerializeAsync(nodeToSave, ct);
            extension = serializer.SupportedExtensions[0]; // Use the primary extension
        }
        else
        {
            // Default to JSON serialization for type preservation
            content = JsonSerializer.Serialize(nodeToSave, options);
            extension = ".json";
        }

        var blobPath = GetBlobPath(key, extension);
        var blobClient = _containerClient.GetBlobClient(blobPath);

        await blobClient.UploadAsync(
            BinaryData.FromString(content),
            overwrite: true,
            cancellationToken: ct);

        // Clean up old blobs with different extensions (e.g., if originally read from .json)
        await CleanupOtherExtensionsAsync(key, extension, ct);
    }

    /// <summary>
    /// Removes blobs with other extensions when a new blob is written.
    /// This prevents having both .json and .md blobs for the same node.
    /// </summary>
    private async Task CleanupOtherExtensionsAsync(string path, string keepExtension, CancellationToken ct)
    {
        foreach (var ext in SupportedExtensions)
        {
            if (ext == keepExtension)
                continue;

            var blobPath = GetBlobPath(path, ext);
            var blobClient = _containerClient.GetBlobClient(blobPath);
            await blobClient.DeleteIfExistsAsync(cancellationToken: ct);
        }
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        // Delete blobs with all supported extensions
        foreach (var ext in SupportedExtensions)
        {
            var blobPath = GetBlobPath(path, ext);
            var blobClient = _containerClient.GetBlobClient(blobPath);
            await blobClient.DeleteIfExistsAsync(cancellationToken: ct);
        }
    }

    public async Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(
        string? parentPath,
        CancellationToken ct = default)
    {
        var normalizedParent = NormalizePath(parentPath);
        var prefix = string.IsNullOrEmpty(normalizedParent) ? "nodes/" : $"nodes/{normalizedParent}/";

        var nodePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var blobItem in _containerClient.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct))
        {
            var fullPath = blobItem.Name;
            if (!fullPath.StartsWith("nodes/"))
                continue;

            // Check if this is a supported file extension
            string? matchedExtension = null;
            foreach (var ext in SupportedExtensions)
            {
                if (fullPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    matchedExtension = ext;
                    break;
                }
            }

            if (matchedExtension == null)
                continue;

            // Remove "nodes/" prefix and extension suffix
            var path = fullPath[6..^matchedExtension.Length];

            // Check if this is a direct child
            var relativePath = string.IsNullOrEmpty(normalizedParent)
                ? path
                : path.StartsWith(normalizedParent + "/", StringComparison.OrdinalIgnoreCase)
                    ? path[(normalizedParent.Length + 1)..]
                    : null;

            if (relativePath == null)
                continue;

            var slashIndex = relativePath.IndexOf('/');
            if (slashIndex == -1)
            {
                // Direct child node - add to set (deduplicates .md and .json)
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

        return (nodePaths.ToList(), directoryPaths.ToList());
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var (blobClient, _) = await FindBlobWithExtensionAsync(path, ct);
        return blobClient != null;
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
        string? subPath,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var prefix = GetPartitionPrefix(nodePath, subPath);

        await foreach (var blobItem in _containerClient.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct))
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
            var element = JsonSerializer.Deserialize<JsonElement>(content.ToString(), options);
            yield return element;
        }
    }

    public async Task SavePartitionObjectsAsync(
        string nodePath,
        string? subPath,
        IReadOnlyCollection<object> objects,
        JsonSerializerOptions options,
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

            var json = JsonSerializer.Serialize(wrapper, options);
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

        await foreach (var blobItem in _containerClient.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct))
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

        await foreach (var blobItem in _containerClient.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, prefix, ct))
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
