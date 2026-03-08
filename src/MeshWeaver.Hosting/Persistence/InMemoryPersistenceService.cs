using System.Collections.Concurrent;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// In-memory implementation of IMeshStorage.
/// Suitable for development and testing.
/// Optionally backs to an IStorageAdapter for file system persistence.
/// </summary>
public class InMemoryPersistenceService : IStorageService, IDisposable
{
    private readonly IStorageAdapter? _storageAdapter;
    private readonly IDataChangeNotifier? _changeNotifier;
    private readonly ConcurrentDictionary<string, MeshNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Comment> _comments = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDisposable? _changeSubscription;

    public InMemoryPersistenceService(
        IStorageAdapter? storageAdapter = null,
        IDataChangeNotifier? changeNotifier = null)
    {
        _storageAdapter = storageAdapter;
        _changeNotifier = changeNotifier;

        // Subscribe to external change notifications to invalidate cache
        _changeSubscription = _changeNotifier?.Subscribe(OnExternalChange);
    }

    private void OnExternalChange(DataChangeNotification notification)
    {
        if (notification.Kind == DataChangeKind.Deleted)
        {
            _nodes.TryRemove(notification.Path, out _);
        }
    }

    public void Dispose()
    {
        _changeSubscription?.Dispose();
    }

    public Task InitializeAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Initializes the service and loads existing data from storage adapter.
    /// </summary>
    /// <param name="options">JSON serializer options for deserialization</param>
    /// <param name="ct">Cancellation token</param>
    public async Task InitializeAsync(JsonSerializerOptions options, CancellationToken ct = default)
    {
        if (_storageAdapter == null)
            return;

        // Load nodes recursively from storage
        await LoadNodesRecursivelyAsync(null, options, ct);
    }

    private async Task LoadNodesRecursivelyAsync(string? parentPath, JsonSerializerOptions options, CancellationToken ct)
    {
        if (_storageAdapter == null)
            return;

        var (nodePaths, directoryPaths) = await _storageAdapter.ListChildPathsAsync(parentPath, ct);

        // Load nodes
        foreach (var nodePath in nodePaths)
        {
            var node = await _storageAdapter.ReadAsync(nodePath, options, ct);
            if (node != null)
            {
                var normalizedPath = NormalizePath(nodePath);
                _nodes[normalizedPath] = node;

                // Recursively load children
                await LoadNodesRecursivelyAsync(nodePath, options, ct);
            }
        }

        // Scan directories that aren't nodes
        foreach (var dirPath in directoryPaths)
        {
            await LoadNodesRecursivelyAsync(dirPath, options, ct);
        }
    }

    public async Task<MeshNode?> GetNodeAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        if (_nodes.TryGetValue(normalizedPath, out var node))
            return node;

        // Fall through to storage adapter on cache miss
        if (_storageAdapter != null)
        {
            node = await _storageAdapter.ReadAsync(path, options, ct);
            if (node != null)
            {
                _nodes[normalizedPath] = node;
                return node;
            }
        }

        return null;
    }

    public async IAsyncEnumerable<MeshNode> GetChildrenAsync(string? parentPath, JsonSerializerOptions options)
    {
        // Ensure children are loaded from storage adapter
        await EnsureChildrenLoadedAsync(parentPath, options);

        var normalizedParent = NormalizePath(parentPath);
        var parentSegments = string.IsNullOrEmpty(normalizedParent)
            ? Array.Empty<string>()
            : normalizedParent.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var expectedDepth = parentSegments.Length + 1;

        var children = _nodes.Values
            .Where(n =>
            {
                var nodeSegments = n.Segments;
                if (nodeSegments.Count != expectedDepth)
                    return false;

                // Check if parent matches
                if (parentSegments.Length == 0)
                    return true;

                for (int i = 0; i < parentSegments.Length; i++)
                {
                    if (!nodeSegments[i].Equals(parentSegments[i], StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                return true;
            })
            .OrderBy(n => n.Order ?? int.MaxValue)
            .ThenBy(n => n.Name);

        foreach (var child in children)
        {
            yield return child;
        }
    }

    public async IAsyncEnumerable<MeshNode> GetDescendantsAsync(string? parentPath, JsonSerializerOptions options)
    {
        // Ensure descendants are loaded from storage adapter
        await EnsureDescendantsLoadedAsync(parentPath, options);

        var normalizedParent = NormalizePath(parentPath);

        IEnumerable<MeshNode> descendants;
        if (string.IsNullOrEmpty(normalizedParent))
        {
            descendants = _nodes.Values;
        }
        else
        {
            descendants = _nodes.Values
                .Where(n =>
                {
                    var nodePath = NormalizePath(n.Path);
                    return nodePath.StartsWith(normalizedParent + "/", StringComparison.OrdinalIgnoreCase);
                });
        }

        foreach (var descendant in descendants.OrderBy(n => n.Path))
        {
            yield return descendant;
        }
    }

    /// <summary>
    /// Ensures direct children of a path are loaded from the storage adapter into the cache.
    /// </summary>
    private async Task EnsureChildrenLoadedAsync(string? parentPath, JsonSerializerOptions options)
    {
        if (_storageAdapter == null) return;

        var (nodePaths, _) = await _storageAdapter.ListChildPathsAsync(parentPath ?? "", default);
        foreach (var path in nodePaths)
        {
            var normalizedPath = NormalizePath(path);
            if (_nodes.ContainsKey(normalizedPath))
                continue;

            var node = await _storageAdapter.ReadAsync(path, options, default);
            if (node != null)
                _nodes[normalizedPath] = node;
        }
    }

    /// <summary>
    /// Ensures all descendants of a path are loaded from the storage adapter into the cache.
    /// </summary>
    private async Task EnsureDescendantsLoadedAsync(string? parentPath, JsonSerializerOptions options)
    {
        if (_storageAdapter == null) return;
        await LoadNodesRecursivelyAsync(parentPath, options, default);
    }

    public async Task<MeshNode> SaveNodeAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(node.Path);
        var isNew = !_nodes.ContainsKey(normalizedPath);

        // Set LastModified to UtcNow if not specified (for in-memory case without file system)
        var savedNode = node with
        {
            LastModified = node.LastModified == default ? DateTimeOffset.UtcNow : node.LastModified
        };

        _nodes[normalizedPath] = savedNode;

        if (_storageAdapter != null)
        {
            await _storageAdapter.WriteAsync(savedNode, options, ct);
        }

        // Notify change
        _changeNotifier?.NotifyChange(isNew
            ? DataChangeNotification.Created(normalizedPath, savedNode)
            : DataChangeNotification.Updated(normalizedPath, savedNode));

        return savedNode;
    }

    public async Task DeleteNodeAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);

        if (recursive)
        {
            var toDelete = _nodes.Keys
                .Where(k => k.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                            k.StartsWith(normalizedPath + "/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var key in toDelete)
            {
                _nodes.TryRemove(key, out var removedNode);
                if (_storageAdapter != null)
                {
                    await _storageAdapter.DeleteAsync(key, ct);
                }
                // Notify deletion
                _changeNotifier?.NotifyChange(DataChangeNotification.Deleted(key, removedNode));
            }
        }
        else
        {
            _nodes.TryRemove(normalizedPath, out var removedNode);
            if (_storageAdapter != null)
            {
                await _storageAdapter.DeleteAsync(normalizedPath, ct);
            }
            // Notify deletion
            _changeNotifier?.NotifyChange(DataChangeNotification.Deleted(normalizedPath, removedNode));
        }
    }

    public async Task<MeshNode> MoveNodeAsync(string sourcePath, string targetPath, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var normalizedSource = NormalizePath(sourcePath);
        var normalizedTarget = NormalizePath(targetPath);

        // Validate source exists
        if (!_nodes.TryGetValue(normalizedSource, out var sourceNode))
            throw new InvalidOperationException($"Source node not found: {sourcePath}");

        // Validate target doesn't exist
        if (_nodes.ContainsKey(normalizedTarget))
            throw new InvalidOperationException($"Target path already exists: {targetPath}");

        // Get all descendants
        var descendants = _nodes.Keys
            .Where(k => k.StartsWith(normalizedSource + "/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Move the main node - need to create new MeshNode to ensure Prefix is updated
        var movedNode = MeshNode.FromPath(targetPath) with
        {
            Name = sourceNode.Name,
            NodeType = sourceNode.NodeType,
            Icon = sourceNode.Icon,
            Order = sourceNode.Order,
            Content = sourceNode.Content,
            AssemblyLocation = sourceNode.AssemblyLocation,
            HubConfiguration = sourceNode.HubConfiguration,
            GlobalServiceConfigurations = sourceNode.GlobalServiceConfigurations
        };
        await SaveNodeAsync(movedNode, options, ct);

        // Move descendants with updated paths
        foreach (var descendantPath in descendants)
        {
            if (_nodes.TryGetValue(descendantPath, out var descendantNode))
            {
                // Calculate new path by replacing the source prefix with target
                var relativePath = descendantPath.Substring(normalizedSource.Length);
                var newPath = normalizedTarget + relativePath;

                // Create new MeshNode to ensure Prefix is updated
                var movedDescendant = MeshNode.FromPath(newPath) with
                {
                    Name = descendantNode.Name,
                    NodeType = descendantNode.NodeType,
                    Icon = descendantNode.Icon,
                    Order = descendantNode.Order,
                    Content = descendantNode.Content,
                    AssemblyLocation = descendantNode.AssemblyLocation,
                    HubConfiguration = descendantNode.HubConfiguration,
                    GlobalServiceConfigurations = descendantNode.GlobalServiceConfigurations
                };
                await SaveNodeAsync(movedDescendant, options, ct);
            }
        }

        // Migrate comments - update NodePath for all comments on moved nodes
        var allOldPaths = new[] { normalizedSource }.Concat(descendants).ToList();
        foreach (var oldPath in allOldPaths)
        {
            var newPath = oldPath == normalizedSource
                ? normalizedTarget
                : normalizedTarget + oldPath.Substring(normalizedSource.Length);

            var commentsToMigrate = _comments.Values
                .Where(c => NormalizePath(c.PrimaryNodePath ?? "").Equals(oldPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var comment in commentsToMigrate)
            {
                var migratedComment = comment with { PrimaryNodePath = newPath };
                _comments[comment.Id] = migratedComment;
            }
        }

        // Delete originals (the main node and all descendants)
        _nodes.TryRemove(normalizedSource, out _);
        if (_storageAdapter != null)
        {
            await _storageAdapter.DeleteAsync(normalizedSource, ct);
        }

        foreach (var descendantPath in descendants)
        {
            _nodes.TryRemove(descendantPath, out _);
            if (_storageAdapter != null)
            {
                await _storageAdapter.DeleteAsync(descendantPath, ct);
            }
        }

        return movedNode;
    }

    public async IAsyncEnumerable<MeshNode> SearchAsync(string? parentPath, string query, JsonSerializerOptions options)
    {
        var normalizedParent = NormalizePath(parentPath);

        var results = _nodes.Values
            .Where(n =>
            {
                // Filter by parent path if specified
                if (!string.IsNullOrEmpty(normalizedParent))
                {
                    var nodePath = NormalizePath(n.Path);
                    if (!nodePath.StartsWith(normalizedParent + "/", StringComparison.OrdinalIgnoreCase) &&
                        !nodePath.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                // Search in Name and Content
                if (n.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                    return true;
                if (n.Content?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                    return true;

                return false;
            })
            .OrderBy(n => n.Order ?? int.MaxValue)
            .ThenBy(n => n.Name);

        foreach (var result in results)
        {
            yield return result;
        }
        await Task.CompletedTask; // Keep async signature
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        if (_nodes.ContainsKey(normalizedPath))
            return true;

        // Fall through to storage adapter on cache miss
        if (_storageAdapter != null)
            return await _storageAdapter.ExistsAsync(path, ct);

        return false;
    }

    private static string NormalizePath(string? path) =>
        path?.Trim('/') ?? "";

    #region Comments

    public async IAsyncEnumerable<Comment> GetCommentsAsync(string nodePath, JsonSerializerOptions options)
    {
        var normalizedPath = NormalizePath(nodePath);
        var comments = _comments.Values
            .Where(c => NormalizePath(c.PrimaryNodePath ?? "") == normalizedPath)
            .OrderBy(c => c.CreatedAt);

        foreach (var comment in comments)
        {
            yield return comment;
        }
        await Task.CompletedTask; // Keep async signature
    }

    public Task<Comment> AddCommentAsync(Comment comment, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var savedComment = comment with
        {
            Id = string.IsNullOrEmpty(comment.Id) ? Guid.NewGuid().ToString() : comment.Id,
            CreatedAt = comment.CreatedAt == default ? DateTimeOffset.UtcNow : comment.CreatedAt
        };

        _comments[savedComment.Id] = savedComment;
        return Task.FromResult(savedComment);
    }

    public Task DeleteCommentAsync(string commentId, CancellationToken ct = default)
    {
        _comments.TryRemove(commentId, out _);
        return Task.CompletedTask;
    }

    public Task<Comment?> GetCommentAsync(string commentId, CancellationToken ct = default)
    {
        _comments.TryGetValue(commentId, out var comment);
        return Task.FromResult(comment);
    }

    #endregion

    #region Partition Storage

    private readonly ConcurrentDictionary<string, List<object>> _partitionData = new(StringComparer.OrdinalIgnoreCase);

    private static string GetPartitionKey(string nodePath, string? subPath)
    {
        var key = nodePath.Trim('/');
        if (!string.IsNullOrEmpty(subPath))
        {
            key = $"{key}/{subPath.Trim('/')}";
        }
        return key;
    }

    public async IAsyncEnumerable<object> GetPartitionObjectsAsync(
        string nodePath,
        string? subPath,
        JsonSerializerOptions options)
    {
        var key = GetPartitionKey(nodePath, subPath);

        // If we have cached data, return it
        if (_partitionData.TryGetValue(key, out var cached))
        {
            foreach (var obj in cached)
            {
                yield return obj;
            }
            yield break;
        }

        // Otherwise, load from storage adapter if available
        if (_storageAdapter != null)
        {
            var objects = new List<object>();
            await foreach (var obj in _storageAdapter.GetPartitionObjectsAsync(nodePath, subPath, options))
            {
                objects.Add(obj);
                yield return obj;
            }
            // Cache the loaded objects
            _partitionData[key] = objects;
        }
    }

    public async Task SavePartitionObjectsAsync(
        string nodePath,
        string? subPath,
        IReadOnlyCollection<object> objects,
        JsonSerializerOptions options,
        CancellationToken ct = default)
    {
        var key = GetPartitionKey(nodePath, subPath);
        var hadExisting = _partitionData.ContainsKey(key);

        // Update in-memory cache
        _partitionData[key] = objects.ToList();

        // Persist to storage adapter if available
        if (_storageAdapter != null)
        {
            await _storageAdapter.SavePartitionObjectsAsync(nodePath, subPath, objects, options, ct);
        }

        // Notify change for each object in the partition
        if (_changeNotifier != null)
        {
            foreach (var obj in objects)
            {
                var notification = hadExisting
                    ? DataChangeNotification.Updated(key, obj)
                    : DataChangeNotification.Created(key, obj);
                _changeNotifier.NotifyChange(notification);
            }
        }
    }

    public async Task DeletePartitionObjectsAsync(
        string nodePath,
        string? subPath = null,
        CancellationToken ct = default)
    {
        var key = GetPartitionKey(nodePath, subPath);

        // Get existing objects for notification before removal
        _partitionData.TryRemove(key, out var removedObjects);

        // Delete from storage adapter if available
        if (_storageAdapter != null)
        {
            await _storageAdapter.DeletePartitionObjectsAsync(nodePath, subPath, ct);
        }

        // Notify deletion
        if (_changeNotifier != null && removedObjects != null)
        {
            foreach (var obj in removedObjects)
            {
                _changeNotifier.NotifyChange(DataChangeNotification.Deleted(key, obj));
            }
        }
    }

    public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(
        string nodePath,
        string? subPath = null,
        CancellationToken ct = default)
    {
        // Delegate to storage adapter if available
        if (_storageAdapter != null)
        {
            return _storageAdapter.GetPartitionMaxTimestampAsync(nodePath, subPath, ct);
        }

        // For pure in-memory storage without adapter, return UtcNow as we can't track file timestamps
        return Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow);
    }

    #endregion
}
