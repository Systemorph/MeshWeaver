using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MeshWeaver.Hosting.Persistence.Parsers;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;

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
    // Blob I/O leaves are bridged to IObservable through this pool — never via a
    // bare Observable.FromAsync, which only moves the subscribe onto the pool and
    // leaves the await continuation free to resume on a captured scheduler and
    // deadlock under a blocking subscriber. See IoPoolExtensions and
    // Doc/Architecture/AsynchronousCalls.md.
    private readonly IIoPool _ioPool;

    /// <summary>
    /// Supported file extensions in priority order for reading.
    /// </summary>
    private static readonly string[] SupportedExtensions = [".md", ".json"];

    public AzureBlobStorageAdapter(BlobContainerClient containerClient, IoPoolRegistry? ioPoolRegistry = null)
    {
        _containerClient = containerClient;
        // Blob pool cap governs blob-storage concurrency; Unbounded is the
        // DI-less fallback (still offloads to the ThreadPool with ConfigureAwait).
        _ioPool = ioPoolRegistry?.Get(IoPoolNames.Blob) ?? IoPool.Unbounded;
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
                var exists = await blobClient.ExistsAsync(ct).ConfigureAwait(false);
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

    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => _ioPool.Run(ct => ReadAsyncCore(path, options, ct));

    private async Task<MeshNode?> ReadAsyncCore(string path, JsonSerializerOptions options, CancellationToken ct)
    {
        var (blobClient, extension) = await FindBlobWithExtensionAsync(path, ct).ConfigureAwait(false);
        if (blobClient == null)
            return null;

        try
        {
            var response = await blobClient.DownloadContentAsync(ct).ConfigureAwait(false);
            var content = response.Value.Content.ToString();

            // Try to use parsers for non-JSON formats (with fallback support for .md files)
            MeshNode? node;
            var parsers = _parserRegistry.GetParsers(extension);
            if (parsers.Count > 0)
            {
                // Use TryParse for fallback support (e.g., AgentFileParser -> MarkdownFileParser)
                node = _parserRegistry.TryParse(extension, blobClient.Name, content, path);
            }
            else
            {
                // Default to JSON deserialization
                node = JsonSerializer.Deserialize<MeshNode>(content, options);
            }

            if (node != null)
            {
                // Use blob last modified time if not set
                var properties = await blobClient.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
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

    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => _ioPool.Run<MeshNode?>(async ct => { await WriteAsyncCore(node, options, ct).ConfigureAwait(false); return node; });

    private async Task WriteAsyncCore(MeshNode node, JsonSerializerOptions options, CancellationToken ct)
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
            content = serializer.Serialize(nodeToSave);
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
            cancellationToken: ct).ConfigureAwait(false);

        // Clean up old blobs with different extensions (e.g., if originally read from .json)
        await CleanupOtherExtensionsAsync(key, extension, ct).ConfigureAwait(false);
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
            await blobClient.DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
        }
    }

    public IObservable<string> Delete(string path)
        => _ioPool.Run(async ct => { await DeleteAsyncCore(path, ct).ConfigureAwait(false); return path; });

    private async Task DeleteAsyncCore(string path, CancellationToken ct)
    {
        // Delete blobs with all supported extensions
        foreach (var ext in SupportedExtensions)
        {
            var blobPath = GetBlobPath(path, ext);
            var blobClient = _containerClient.GetBlobClient(blobPath);
            await blobClient.DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
        }
    }

    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
        => _ioPool.Run(ct => ListChildPathsAsyncCore(parentPath, ct));

    private async Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsyncCore(
        string? parentPath, CancellationToken ct)
    {
        var normalizedParent = NormalizePath(parentPath);
        var prefix = string.IsNullOrEmpty(normalizedParent) ? "nodes/" : $"nodes/{normalizedParent}/";

        var nodePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var blobItem in _containerClient.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct).ConfigureAwait(false))
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

    public IObservable<bool> Exists(string path)
        => _ioPool.Run(async ct =>
        {
            var (blobClient, _) = await FindBlobWithExtensionAsync(path, ct).ConfigureAwait(false);
            return blobClient != null;
        });

    #region Partition Storage

    private static string GetPartitionBlobPath(string nodePath, string? subPath, string objectId) =>
        string.IsNullOrEmpty(subPath)
            ? $"partitions/{NormalizePath(nodePath)}/{objectId}.json"
            : $"partitions/{NormalizePath(nodePath)}/{NormalizePath(subPath)}/{objectId}.json";

    private static string GetPartitionPrefix(string nodePath, string? subPath) =>
        string.IsNullOrEmpty(subPath)
            ? $"partitions/{NormalizePath(nodePath)}/"
            : $"partitions/{NormalizePath(nodePath)}/{NormalizePath(subPath)}/";

    public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
        => _ioPool.RunStream(ct => GetPartitionObjectsAsyncCore(nodePath, subPath, options, ct));

    private async IAsyncEnumerable<object> GetPartitionObjectsAsyncCore(
        string nodePath,
        string? subPath,
        JsonSerializerOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var prefix = GetPartitionPrefix(nodePath, subPath);

        await foreach (var blobItem in _containerClient.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct).ConfigureAwait(false))
        {
            if (!blobItem.Name.EndsWith(".json"))
                continue;

            var blobClient = _containerClient.GetBlobClient(blobItem.Name);

            BinaryData content;
            try
            {
                var response = await blobClient.DownloadContentAsync(ct).ConfigureAwait(false);
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

    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath,
        IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => _ioPool.Run(async ct => { await SavePartitionObjectsAsyncCore(nodePath, subPath, objects, options, ct).ConfigureAwait(false); return Unit.Default; });

    private async Task SavePartitionObjectsAsyncCore(
        string nodePath,
        string? subPath,
        IReadOnlyCollection<object> objects,
        JsonSerializerOptions options,
        CancellationToken ct)
    {
        // Delete existing objects first
        await DeletePartitionObjectsAsyncCore(nodePath, subPath, ct).ConfigureAwait(false);

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
                cancellationToken: ct).ConfigureAwait(false);
        }
    }

    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => _ioPool.Run(async ct => { await DeletePartitionObjectsAsyncCore(nodePath, subPath, ct).ConfigureAwait(false); return Unit.Default; });

    private async Task DeletePartitionObjectsAsyncCore(string nodePath, string? subPath, CancellationToken ct)
    {
        var prefix = GetPartitionPrefix(nodePath, subPath);

        await foreach (var blobItem in _containerClient.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct).ConfigureAwait(false))
        {
            var blobClient = _containerClient.GetBlobClient(blobItem.Name);
            await blobClient.DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
        }
    }

    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => _ioPool.Run(ct => GetPartitionMaxTimestampAsyncCore(nodePath, subPath, ct));

    private async Task<DateTimeOffset?> GetPartitionMaxTimestampAsyncCore(string nodePath, string? subPath, CancellationToken ct)
    {
        var prefix = GetPartitionPrefix(nodePath, subPath);
        DateTimeOffset? maxTimestamp = null;

        await foreach (var blobItem in _containerClient.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, prefix, ct).ConfigureAwait(false))
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
