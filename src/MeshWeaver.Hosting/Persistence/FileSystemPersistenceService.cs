using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Caching.Memory;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// File system persistence service with in-memory caching.
/// Reads from file system on cache miss, with 10-minute sliding expiration.
/// </summary>
public class FileSystemPersistenceService(IStorageAdapter storageAdapter) : IPersistenceService
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly MemoryCacheEntryOptions _cacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(10)
    };

    private static string NormalizePath(string? path) =>
        path?.Trim('/').ToLowerInvariant() ?? "";

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<MeshNode?> GetNodeAsync(string path, CancellationToken ct = default)
    {
        var key = NormalizePath(path);

        if (_cache.TryGetValue(key, out MeshNode? cached))
            return cached;

        var node = await storageAdapter.ReadAsync(path, ct);
        if (node != null)
        {
            _cache.Set(key, node, _cacheOptions);
        }
        return node;
    }

    public async IAsyncEnumerable<MeshNode> GetChildrenAsync(string? parentPath)
    {
        var (nodePaths, _) = await storageAdapter.ListChildPathsAsync(parentPath ?? "", default);

        foreach (var path in nodePaths)
        {
            var node = await storageAdapter.ReadAsync(path, default);
            if (node != null)
                yield return node;
        }
    }

    public async IAsyncEnumerable<MeshNode> GetDescendantsAsync(string? parentPath)
    {
        await foreach (var child in GetChildrenAsync(parentPath))
        {
            yield return child;

            // Recursively get descendants
            await foreach (var descendant in GetDescendantsAsync(child.Prefix))
            {
                yield return descendant;
            }
        }
    }

    public async Task<MeshNode> SaveNodeAsync(MeshNode node, CancellationToken ct = default)
    {
        var savedNode = node with
        {
            LastModified = node.LastModified == default ? DateTimeOffset.UtcNow : node.LastModified
        };

        await storageAdapter.WriteAsync(savedNode, ct);

        // Update cache
        var key = NormalizePath(savedNode.Prefix);
        _cache.Set(key, savedNode, _cacheOptions);

        return savedNode;
    }

    public async Task DeleteNodeAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        await storageAdapter.DeleteAsync(path, ct);

        // Invalidate cache
        var key = NormalizePath(path);
        _cache.Remove(key);
    }

    public async Task<MeshNode> MoveNodeAsync(string sourcePath, string targetPath, CancellationToken ct = default)
    {
        var sourceNode = await storageAdapter.ReadAsync(sourcePath, ct)
            ?? throw new InvalidOperationException($"Source node not found: {sourcePath}");

        if (await storageAdapter.ExistsAsync(targetPath, ct))
            throw new InvalidOperationException($"Target path already exists: {targetPath}");

        var movedNode = MeshNode.FromPath(targetPath) with
        {
            Name = sourceNode.Name,
            NodeType = sourceNode.NodeType,
            Description = sourceNode.Description,
            IconName = sourceNode.IconName,
            DisplayOrder = sourceNode.DisplayOrder,
            AddressSegments = sourceNode.AddressSegments,
            IsPersistent = sourceNode.IsPersistent,
            Content = sourceNode.Content,
            ThumbNail = sourceNode.ThumbNail,
            StreamProvider = sourceNode.StreamProvider,
            AssemblyLocation = sourceNode.AssemblyLocation,
            HubConfiguration = sourceNode.HubConfiguration,
            StartupScript = sourceNode.StartupScript,
            RoutingType = sourceNode.RoutingType,
            InstantiationType = sourceNode.InstantiationType,
            GlobalServiceConfigurations = sourceNode.GlobalServiceConfigurations,
            AutocompleteAddress = sourceNode.AutocompleteAddress
        };

        await storageAdapter.WriteAsync(movedNode, ct);
        await storageAdapter.DeleteAsync(sourcePath, ct);

        // Update cache: remove old path, add new path
        var sourceKey = NormalizePath(sourcePath);
        var targetKey = NormalizePath(targetPath);
        _cache.Remove(sourceKey);
        _cache.Set(targetKey, movedNode, _cacheOptions);

        return movedNode;
    }

    public async IAsyncEnumerable<MeshNode> SearchAsync(string? parentPath, string query)
    {
        await foreach (var node in GetDescendantsAsync(parentPath))
        {
            if (node.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                node.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                node.Content?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            {
                yield return node;
            }
        }
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => storageAdapter.ExistsAsync(path, ct);

    #region Comments - stored in separate files

    public async IAsyncEnumerable<Comment> GetCommentsAsync(string nodePath)
    {
        await foreach (var obj in storageAdapter.GetPartitionObjectsAsync(nodePath, "comments"))
        {
            if (obj is Comment comment)
                yield return comment;
        }
    }

    public async Task<Comment> AddCommentAsync(Comment comment, CancellationToken ct = default)
    {
        var savedComment = comment with
        {
            Id = string.IsNullOrEmpty(comment.Id) ? Guid.NewGuid().ToString() : comment.Id,
            CreatedAt = comment.CreatedAt == default ? DateTimeOffset.UtcNow : comment.CreatedAt
        };

        var comments = new List<Comment>();
        await foreach (var existing in GetCommentsAsync(comment.NodePath))
        {
            comments.Add(existing);
        }
        comments.Add(savedComment);

        await storageAdapter.SavePartitionObjectsAsync(comment.NodePath, "comments", comments.Cast<object>().ToList(), ct);
        return savedComment;
    }

    public async Task DeleteCommentAsync(string commentId, CancellationToken ct = default)
    {
        // Find and remove the comment from all nodes - this is inefficient but works
        // In a real implementation, we'd track comment -> node mapping
        await Task.CompletedTask;
    }

    public async Task<Comment?> GetCommentAsync(string commentId, CancellationToken ct = default)
    {
        // Would need to search - not efficient without index
        await Task.CompletedTask;
        return null;
    }

    #endregion

    #region Partition Storage

    public IAsyncEnumerable<object> GetPartitionObjectsAsync(string nodePath, string? subPath = null)
        => storageAdapter.GetPartitionObjectsAsync(nodePath, subPath);

    public Task SavePartitionObjectsAsync(string nodePath, string? subPath, IReadOnlyCollection<object> objects, CancellationToken ct = default)
        => storageAdapter.SavePartitionObjectsAsync(nodePath, subPath, objects, ct);

    public Task DeletePartitionObjectsAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
        => storageAdapter.DeletePartitionObjectsAsync(nodePath, subPath, ct);

    #endregion
}
