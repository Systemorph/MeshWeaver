using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;

using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Caching.Memory;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// File system persistence service with in-memory caching.
/// Reads from file system on cache miss, with 10-minute sliding expiration.
/// </summary>
public class FileSystemPersistenceService : IStorageService, IDisposable
{
    private readonly IStorageAdapter _storageAdapter;
    private readonly IDataChangeNotifier? _changeNotifier;
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly MemoryCacheEntryOptions _cacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(15)
    };
    private readonly IDisposable? _changeSubscription;
    private long _versionCounter;

    public FileSystemPersistenceService(
        IStorageAdapter storageAdapter,
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
            _cache.Remove(NormalizePath(notification.Path));
        }
    }

    public void Dispose()
    {
        _changeSubscription?.Dispose();
        _cache.Dispose();
    }

    private static string NormalizePath(string? path) =>
        path?.Trim('/') ?? "";

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<MeshNode?> GetNodeAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var key = NormalizePath(path);

        if (_cache.TryGetValue(key, out MeshNode? cached))
            return cached;

        var node = await _storageAdapter.ReadAsync(path, options, ct);
        if (node != null)
        {
            _cache.Set(key, node, _cacheOptions);
        }
        return node;
    }

    public async IAsyncEnumerable<MeshNode> GetChildrenAsync(string? parentPath, JsonSerializerOptions options)
    {
        var (nodePaths, _) = await _storageAdapter.ListChildPathsAsync(parentPath ?? "", default);

        foreach (var path in nodePaths)
        {
            // Use GetNodeAsync to leverage cache
            var node = await GetNodeAsync(path, options);
            if (node != null)
                yield return node;
        }
    }

    public async IAsyncEnumerable<MeshNode> GetDescendantsAsync(string? parentPath, JsonSerializerOptions options)
    {
        var (nodePaths, directoryPaths) = await _storageAdapter.ListChildPathsAsync(parentPath ?? "", default);

        // Process node children
        foreach (var path in nodePaths)
        {
            var node = await GetNodeAsync(path, options);
            if (node != null)
            {
                yield return node;

                // Recursively get descendants of this node
                await foreach (var descendant in GetDescendantsAsync(path, options))
                {
                    yield return descendant;
                }
            }
        }

        // Also traverse non-node directories (e.g., Code/) that contain descendant nodes
        foreach (var dirPath in directoryPaths)
        {
            await foreach (var descendant in GetDescendantsAsync(dirPath, options))
            {
                yield return descendant;
            }
        }
    }

    public async Task<MeshNode> SaveNodeAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var key = NormalizePath(node.Path);
        var isNew = !_cache.TryGetValue(key, out MeshNode? _) && !await _storageAdapter.ExistsAsync(node.Path, ct);

        var savedNode = node with
        {
            LastModified = node.LastModified == default ? DateTimeOffset.UtcNow : node.LastModified,
            Version = Interlocked.Increment(ref _versionCounter)
        };

        // Update cache and notify BEFORE disk write so observers see the change immediately.
        // The re-query triggered by the notification will hit the warm cache.
        _cache.Set(key, savedNode, _cacheOptions);

        _changeNotifier?.NotifyChange(isNew
            ? DataChangeNotification.Created(key, savedNode)
            : DataChangeNotification.Updated(key, savedNode));

        try
        {
            await _storageAdapter.WriteAsync(savedNode, options, ct);
        }
        catch
        {
            // Rollback cache on write failure
            _cache.Remove(key);
            if (isNew)
                _changeNotifier?.NotifyChange(DataChangeNotification.Deleted(key, savedNode));
            throw;
        }

        return savedNode;
    }

    public async Task DeleteNodeAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        var key = NormalizePath(path);

        // Get the node before deletion for notification
        // Note: We cannot read the node here without options, so we rely on cached value only
        MeshNode? deletedNode = null;
        if (_cache.TryGetValue(key, out MeshNode? cached))
            deletedNode = cached;

        await _storageAdapter.DeleteAsync(path, ct);

        // Invalidate cache
        _cache.Remove(key);

        // Notify deletion
        _changeNotifier?.NotifyChange(DataChangeNotification.Deleted(key, deletedNode));
    }

    public async Task<MeshNode> MoveNodeAsync(string sourcePath, string targetPath, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var sourceNode = await _storageAdapter.ReadAsync(sourcePath, options, ct)
            ?? throw new InvalidOperationException($"Source node not found: {sourcePath}");

        if (await _storageAdapter.ExistsAsync(targetPath, ct))
            throw new InvalidOperationException($"Target path already exists: {targetPath}");

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

        await _storageAdapter.WriteAsync(movedNode, options, ct);
        await _storageAdapter.DeleteAsync(sourcePath, ct);

        // Update cache: remove old path, add new path
        var sourceKey = NormalizePath(sourcePath);
        var targetKey = NormalizePath(targetPath);
        _cache.Remove(sourceKey);
        _cache.Set(targetKey, movedNode, _cacheOptions);

        // Notify changes: deletion at source, creation at target
        _changeNotifier?.NotifyChange(DataChangeNotification.Deleted(sourceKey, sourceNode));
        _changeNotifier?.NotifyChange(DataChangeNotification.Created(targetKey, movedNode));

        return movedNode;
    }

    public async IAsyncEnumerable<MeshNode> SearchAsync(string? parentPath, string query, JsonSerializerOptions options)
    {
        await foreach (var node in GetDescendantsAsync(parentPath, options))
        {
            if (node.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
                node.Content?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            {
                yield return node;
            }
        }
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => _storageAdapter.ExistsAsync(path, ct);

    public async Task<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatchAsync(
        string fullPath, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(fullPath);
        if (string.IsNullOrEmpty(normalizedPath))
            return (null, 0);

        // Delegate to storage adapter (e.g., PostgreSQL with dedicated SQL)
        var (adapterNode, adapterSegments) = await _storageAdapter.FindBestPrefixMatchAsync(normalizedPath, options, ct);
        if (adapterNode != null)
            return (adapterNode, adapterSegments);

        // Fall back to walking up the path hierarchy using cache/adapter reads
        var segments = normalizedPath.Split('/');
        for (int depth = segments.Length; depth >= 1; depth--)
        {
            var testPath = string.Join("/", segments.Take(depth));
            var node = await GetNodeAsync(testPath, options, ct);
            if (node != null)
                return (node, depth);
        }

        return (null, 0);
    }

    #region Comments - stored in separate files

    public async IAsyncEnumerable<Comment> GetCommentsAsync(string nodePath, JsonSerializerOptions options)
    {
        await foreach (var obj in _storageAdapter.GetPartitionObjectsAsync(nodePath, "comments", options))
        {
            if (obj is Comment comment)
                yield return comment;
        }
    }

    public async Task<Comment> AddCommentAsync(Comment comment, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var savedComment = comment with
        {
            Id = string.IsNullOrEmpty(comment.Id) ? Guid.NewGuid().ToString() : comment.Id,
            CreatedAt = comment.CreatedAt == default ? DateTimeOffset.UtcNow : comment.CreatedAt
        };

        var commentNodePath = comment.PrimaryNodePath ?? "";
        var comments = new List<Comment>();
        await foreach (var existing in GetCommentsAsync(commentNodePath, options))
        {
            comments.Add(existing);
        }
        comments.Add(savedComment);

        await _storageAdapter.SavePartitionObjectsAsync(commentNodePath, "comments", comments.Cast<object>().ToList(), options, ct);
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

    public IAsyncEnumerable<object> GetPartitionObjectsAsync(string nodePath, string? subPath, JsonSerializerOptions options)
        => _storageAdapter.GetPartitionObjectsAsync(nodePath, subPath, options);

    public Task SavePartitionObjectsAsync(string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options, CancellationToken ct = default)
        => _storageAdapter.SavePartitionObjectsAsync(nodePath, subPath, objects, options, ct);

    public Task DeletePartitionObjectsAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
        => _storageAdapter.DeletePartitionObjectsAsync(nodePath, subPath, ct);

    public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
        => _storageAdapter.GetPartitionMaxTimestampAsync(nodePath, subPath, ct);

    #endregion

    #region Query

    public async IAsyncEnumerable<object> QueryAsync(string query, string path, JsonSerializerOptions options)
    {
        var parser = new QueryParser();
        var parsedQuery = parser.Parse(query);
        var evaluator = new QueryEvaluator();

        var normalizedPath = NormalizePath(path);
        var results = new List<(object Item, int Score)>();

        // Determine paths to search based on scope
        var pathsToSearch = GetPathsForScope(normalizedPath, parsedQuery.Scope);

        foreach (var searchPath in pathsToSearch)
        {
            // Search MeshNodes at this path
            var node = await GetNodeAsync(searchPath, options);
            if (node != null && evaluator.Matches(node, parsedQuery))
            {
                var score = evaluator.GetFuzzyScore(node, parsedQuery.TextSearch);
                results.Add((node, score));
            }

        }

        // For Descendants and Hierarchy scopes, also search descendant paths
        if (parsedQuery.Scope == QueryScope.Descendants || parsedQuery.Scope == QueryScope.Hierarchy)
        {
            await foreach (var descendant in GetDescendantsAsync(normalizedPath, options))
            {
                // Evaluate the node itself
                if (evaluator.Matches(descendant, parsedQuery))
                {
                    var score = evaluator.GetFuzzyScore(descendant, parsedQuery.TextSearch);
                    if (!results.Any(r => ReferenceEquals(r.Item, descendant)))
                        results.Add((descendant, score));
                }

            }
        }

        // Order by fuzzy score (higher first) for text searches
        var orderedResults = !string.IsNullOrEmpty(parsedQuery.TextSearch)
            ? results.OrderByDescending(r => r.Score)
            : results.AsEnumerable();

        foreach (var (item, _) in orderedResults)
        {
            yield return item;
        }
    }

    private static List<string> GetPathsForScope(string basePath, QueryScope scope)
    {
        var paths = new List<string>();

        if (scope != QueryScope.Descendants || string.IsNullOrEmpty(basePath))
        {
            paths.Add(basePath);
        }

        if (scope == QueryScope.Ancestors || scope == QueryScope.Hierarchy)
        {
            var segments = basePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                var ancestorPath = string.Join("/", segments.Take(i));
                if (!paths.Contains(ancestorPath, StringComparer.OrdinalIgnoreCase))
                    paths.Add(ancestorPath);
            }
        }

        if (scope == QueryScope.Descendants)
        {
            paths.Add(basePath);
        }

        return paths;
    }

    #endregion
}
