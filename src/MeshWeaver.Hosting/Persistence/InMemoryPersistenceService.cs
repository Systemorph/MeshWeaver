using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Query;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// In-memory implementation of IPersistenceService.
/// Suitable for development and testing.
/// Optionally backs to an IStorageAdapter for file system persistence.
/// </summary>
public class InMemoryPersistenceService(IStorageAdapter? storageAdapter = null) : IPersistenceService
{
    private readonly ConcurrentDictionary<string, MeshNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Comment> _comments = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
            return;

        if (storageAdapter != null)
        {
            await LoadFromStorageAsync("", ct);
        }

        _initialized = true;
    }

    private async Task LoadFromStorageAsync(string parentPath, CancellationToken ct)
    {
        var (nodePaths, directoryPaths) = await storageAdapter!.ListChildPathsAsync(parentPath, ct);

        // Load nodes from JSON files
        foreach (var path in nodePaths)
        {
            var node = await storageAdapter.ReadAsync(path, ct);
            if (node != null)
            {
                var normalizedPath = NormalizePath(path);
                _nodes[normalizedPath] = node;
                // Recursively load children under this node
                await LoadFromStorageAsync(path, ct);
            }
        }

        // Also recursively scan directories that don't have nodes
        foreach (var dirPath in directoryPaths)
        {
            await LoadFromStorageAsync(dirPath, ct);
        }
    }

    public Task<MeshNode?> GetNodeAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        _nodes.TryGetValue(normalizedPath, out var node);
        return Task.FromResult(node);
    }

    public async IAsyncEnumerable<MeshNode> GetChildrenAsync(string? parentPath)
    {
        var normalizedParent = NormalizePath(parentPath);
        var parentSegments = string.IsNullOrEmpty(normalizedParent)
            ? Array.Empty<string>()
            : normalizedParent.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var expectedDepth = parentSegments.Length + 1;

        var children = _nodes.Values
            .Where(n =>
            {
                var nodeSegments = n.Segments;
                if (nodeSegments.Length != expectedDepth)
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
            .OrderBy(n => n.DisplayOrder)
            .ThenBy(n => n.Name);

        foreach (var child in children)
        {
            yield return child;
        }
        await Task.CompletedTask; // Keep async signature
    }

    public async IAsyncEnumerable<MeshNode> GetDescendantsAsync(string? parentPath)
    {
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
                    var nodePath = NormalizePath(n.Prefix);
                    return nodePath.StartsWith(normalizedParent + "/", StringComparison.OrdinalIgnoreCase);
                });
        }

        foreach (var descendant in descendants.OrderBy(n => n.Prefix))
        {
            yield return descendant;
        }
        await Task.CompletedTask; // Keep async signature
    }

    public async Task<MeshNode> SaveNodeAsync(MeshNode node, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(node.Prefix);

        // Set LastModified to UtcNow if not specified (for in-memory case without file system)
        var savedNode = node with
        {
            Key = normalizedPath,
            LastModified = node.LastModified == default ? DateTimeOffset.UtcNow : node.LastModified
        };

        _nodes[normalizedPath] = savedNode;

        if (storageAdapter != null)
        {
            await storageAdapter.WriteAsync(savedNode, ct);
        }

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
                _nodes.TryRemove(key, out _);
                if (storageAdapter != null)
                {
                    await storageAdapter.DeleteAsync(key, ct);
                }
            }
        }
        else
        {
            _nodes.TryRemove(normalizedPath, out _);
            if (storageAdapter != null)
            {
                await storageAdapter.DeleteAsync(normalizedPath, ct);
            }
        }
    }

    public async Task<MeshNode> MoveNodeAsync(string sourcePath, string targetPath, CancellationToken ct = default)
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
            Key = normalizedTarget,
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
        await SaveNodeAsync(movedNode, ct);

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
                    Key = newPath,
                    Name = descendantNode.Name,
                    NodeType = descendantNode.NodeType,
                    Description = descendantNode.Description,
                    IconName = descendantNode.IconName,
                    DisplayOrder = descendantNode.DisplayOrder,
                    AddressSegments = descendantNode.AddressSegments,
                    IsPersistent = descendantNode.IsPersistent,
                    Content = descendantNode.Content,
                    ThumbNail = descendantNode.ThumbNail,
                    StreamProvider = descendantNode.StreamProvider,
                    AssemblyLocation = descendantNode.AssemblyLocation,
                    HubConfiguration = descendantNode.HubConfiguration,
                    StartupScript = descendantNode.StartupScript,
                    RoutingType = descendantNode.RoutingType,
                    InstantiationType = descendantNode.InstantiationType,
                    GlobalServiceConfigurations = descendantNode.GlobalServiceConfigurations,
                    AutocompleteAddress = descendantNode.AutocompleteAddress
                };
                await SaveNodeAsync(movedDescendant, ct);
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
                .Where(c => NormalizePath(c.NodePath).Equals(oldPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var comment in commentsToMigrate)
            {
                var migratedComment = comment with { NodePath = newPath };
                _comments[comment.Id] = migratedComment;
            }
        }

        // Delete originals (the main node and all descendants)
        _nodes.TryRemove(normalizedSource, out _);
        if (storageAdapter != null)
        {
            await storageAdapter.DeleteAsync(normalizedSource, ct);
        }

        foreach (var descendantPath in descendants)
        {
            _nodes.TryRemove(descendantPath, out _);
            if (storageAdapter != null)
            {
                await storageAdapter.DeleteAsync(descendantPath, ct);
            }
        }

        return movedNode;
    }

    public async IAsyncEnumerable<MeshNode> SearchAsync(string? parentPath, string query)
    {
        var normalizedParent = NormalizePath(parentPath);

        var results = _nodes.Values
            .Where(n =>
            {
                // Filter by parent path if specified
                if (!string.IsNullOrEmpty(normalizedParent))
                {
                    var nodePath = NormalizePath(n.Prefix);
                    if (!nodePath.StartsWith(normalizedParent + "/", StringComparison.OrdinalIgnoreCase) &&
                        !nodePath.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                // Search in Name, Description, and Content
                if (n.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                    return true;
                if (n.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                    return true;
                if (n.Content?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                    return true;

                return false;
            })
            .OrderBy(n => n.DisplayOrder)
            .ThenBy(n => n.Name);

        foreach (var result in results)
        {
            yield return result;
        }
        await Task.CompletedTask; // Keep async signature
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        return Task.FromResult(_nodes.ContainsKey(normalizedPath));
    }

    private static string NormalizePath(string? path) =>
        path?.Trim('/').ToLowerInvariant() ?? "";

    #region Comments

    public async IAsyncEnumerable<Comment> GetCommentsAsync(string nodePath)
    {
        var normalizedPath = NormalizePath(nodePath);
        var comments = _comments.Values
            .Where(c => NormalizePath(c.NodePath) == normalizedPath)
            .OrderBy(c => c.CreatedAt);

        foreach (var comment in comments)
        {
            yield return comment;
        }
        await Task.CompletedTask; // Keep async signature
    }

    public Task<Comment> AddCommentAsync(Comment comment, CancellationToken ct = default)
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
        var key = nodePath.Trim('/').ToLowerInvariant();
        if (!string.IsNullOrEmpty(subPath))
        {
            key = $"{key}/{subPath.Trim('/').ToLowerInvariant()}";
        }
        return key;
    }

    public async IAsyncEnumerable<object> GetPartitionObjectsAsync(
        string nodePath,
        string? subPath = null)
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
        if (storageAdapter != null)
        {
            var objects = new List<object>();
            await foreach (var obj in storageAdapter.GetPartitionObjectsAsync(nodePath, subPath))
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
        CancellationToken ct = default)
    {
        var key = GetPartitionKey(nodePath, subPath);

        // Update in-memory cache
        _partitionData[key] = objects.ToList();

        // Persist to storage adapter if available
        if (storageAdapter != null)
        {
            await storageAdapter.SavePartitionObjectsAsync(nodePath, subPath, objects, ct);
        }
    }

    public async Task DeletePartitionObjectsAsync(
        string nodePath,
        string? subPath = null,
        CancellationToken ct = default)
    {
        var key = GetPartitionKey(nodePath, subPath);

        // Remove from in-memory cache
        _partitionData.TryRemove(key, out _);

        // Delete from storage adapter if available
        if (storageAdapter != null)
        {
            await storageAdapter.DeletePartitionObjectsAsync(nodePath, subPath, ct);
        }
    }

    public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(
        string nodePath,
        string? subPath = null,
        CancellationToken ct = default)
    {
        // Delegate to storage adapter if available
        if (storageAdapter != null)
        {
            return storageAdapter.GetPartitionMaxTimestampAsync(nodePath, subPath, ct);
        }

        // For pure in-memory storage without adapter, return UtcNow as we can't track file timestamps
        return Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow);
    }

    #endregion

    #region Query

    public async IAsyncEnumerable<object> QueryAsync(string query, string path)
    {
        var parser = new RsqlParser();
        var parsedQuery = parser.Parse(query);
        var evaluator = new RsqlEvaluator();

        var normalizedPath = NormalizePath(path);

        // Determine paths to search based on scope
        var pathsToSearch = GetPathsForScope(normalizedPath, parsedQuery.Scope);

        // Collect results with fuzzy scores for ordering
        var results = new List<(object Item, int Score)>();

        foreach (var searchPath in pathsToSearch)
        {

            // Search MeshNodes at this path
            if (_nodes.TryGetValue(searchPath, out var node))
            {
                if (evaluator.Matches(node, parsedQuery))
                {
                    var score = evaluator.GetFuzzyScore(node, parsedQuery.TextSearch);
                    results.Add((node, score));
                }
            }

            // Search partition objects at this path
            await foreach (var obj in GetPartitionObjectsAsync(searchPath))
            {
                if (evaluator.Matches(obj, parsedQuery))
                {
                    var score = evaluator.GetFuzzyScore(obj, parsedQuery.TextSearch);
                    results.Add((obj, score));
                }
            }
        }

        // If we're doing scope=descendants, also search descendant paths
        if (parsedQuery.Scope == QueryScope.Descendants || parsedQuery.Scope == QueryScope.Hierarchy)
        {
            await foreach (var descendant in GetDescendantsAsync(normalizedPath))
            {
                var descendantPath = NormalizePath(descendant.Prefix);

                // Evaluate the node itself
                if (evaluator.Matches(descendant, parsedQuery))
                {
                    var score = evaluator.GetFuzzyScore(descendant, parsedQuery.TextSearch);
                    // Avoid duplicates
                    if (!results.Any(r => ReferenceEquals(r.Item, descendant)))
                        results.Add((descendant, score));
                }

                // Search partition objects under descendant
                await foreach (var obj in GetPartitionObjectsAsync(descendantPath))
                {
                    if (evaluator.Matches(obj, parsedQuery))
                    {
                        var score = evaluator.GetFuzzyScore(obj, parsedQuery.TextSearch);
                        results.Add((obj, score));
                    }
                }
            }
        }

        // Order by fuzzy score (higher first) for text searches, otherwise preserve order
        var orderedResults = !string.IsNullOrEmpty(parsedQuery.TextSearch)
            ? results.OrderByDescending(r => r.Score)
            : results.AsEnumerable();

        foreach (var (item, _) in orderedResults)
        {
            yield return item;
        }
    }

    private List<string> GetPathsForScope(string basePath, QueryScope scope)
    {
        var paths = new List<string>();

        // Always include the base path for Exact, Ancestors, Hierarchy
        if (scope != QueryScope.Descendants || string.IsNullOrEmpty(basePath))
        {
            paths.Add(basePath);
        }

        // Add ancestor paths for Ancestors and Hierarchy scopes
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

        // For Descendants scope, the base path is also searched
        if (scope == QueryScope.Descendants)
        {
            paths.Add(basePath);
        }

        return paths;
    }

    #endregion
}
