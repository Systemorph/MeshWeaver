using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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

    public IObservable<MeshNode?> GetNode(string path, JsonSerializerOptions options)
        => Observable.FromAsync(ct => GetNodeAsyncCore(path, options, ct));

    /// <summary>
    /// Test/back-compat shim. Production callers go through <see cref="GetNode"/>;
    /// concrete-typed test code may keep using this Task-returning entry point.
    /// </summary>
    public Task<MeshNode?> GetNodeAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
        => GetNodeAsyncCore(path, options, ct);

    private async Task<MeshNode?> GetNodeAsyncCore(string path, JsonSerializerOptions options, CancellationToken ct)
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
                // Exclude satellite nodes (MainNode != Path) from browsing.
                // Satellite queries go through FindMatchingNodesAsync which scans _nodes directly.
                if (n.MainNode != null && n.MainNode != n.Path)
                    return false;

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

    public async IAsyncEnumerable<MeshNode> GetAllChildrenAsync(string? parentPath, JsonSerializerOptions options)
    {
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
            yield return child;
    }

    public async IAsyncEnumerable<MeshNode> GetDescendantsAsync(string? parentPath, JsonSerializerOptions options)
    {
        // Ensure descendants are loaded from storage adapter
        await EnsureDescendantsLoadedAsync(parentPath, options);

        var normalizedParent = NormalizePath(parentPath);

        IEnumerable<MeshNode> descendants;
        if (string.IsNullOrEmpty(normalizedParent))
        {
            // Exclude satellite nodes from browsing
            descendants = _nodes.Values.Where(n => n.MainNode == n.Path);
        }
        else
        {
            descendants = _nodes.Values
                .Where(n =>
                {
                    // Exclude satellite nodes from browsing
                    if (n.MainNode != null && n.MainNode != n.Path)
                        return false;
                    var nodePath = NormalizePath(n.Path);
                    return nodePath.StartsWith(normalizedParent + "/", StringComparison.OrdinalIgnoreCase);
                });
        }

        foreach (var descendant in descendants.OrderBy(n => n.Path))
        {
            yield return descendant;
        }
    }

    public async IAsyncEnumerable<MeshNode> GetAllDescendantsAsync(string? parentPath, JsonSerializerOptions options)
    {
        await EnsureDescendantsLoadedAsync(parentPath, options);
        var normalizedParent = NormalizePath(parentPath);

        var descendants = string.IsNullOrEmpty(normalizedParent)
            ? _nodes.Values
            : _nodes.Values.Where(n =>
            {
                var nodePath = NormalizePath(n.Path);
                return nodePath.StartsWith(normalizedParent + "/", StringComparison.OrdinalIgnoreCase);
            });

        foreach (var descendant in descendants.OrderBy(n => n.Path))
            yield return descendant;
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

    /// <summary>
    /// Seeds a node into the in-memory cache WITHOUT writing to the backing storage adapter.
    /// Used for static nodes from IStaticNodeProvider that should be queryable but not persisted.
    /// Only seeds if the node is not already in the cache (doesn't overwrite persisted data).
    /// </summary>
    public void SeedIfAbsent(MeshNode node)
    {
        var normalizedPath = NormalizePath(node.Path);
        _nodes.TryAdd(normalizedPath, node);
    }

    public IObservable<MeshNode> SaveNode(MeshNode node, JsonSerializerOptions options) =>
        Observable.Defer(() =>
        {
            var normalizedPath = NormalizePath(node.Path);
            var isNew = !_nodes.ContainsKey(normalizedPath);

            var savedNode = node with
            {
                LastModified = node.LastModified == default ? DateTimeOffset.UtcNow : node.LastModified
            };

            _nodes[normalizedPath] = savedNode;

            // The only Task→IObservable bridge: the underlying IStorageAdapter
            // (Npgsql/blob/file) is Task-based. Scheduler.Default ensures the
            // wrapped Task starts on TaskPool — never on a hub/grain scheduler.
            var writeAdapter = _storageAdapter is null
                ? Observable.Return(savedNode)
                : Observable.FromAsync(
                        ct => _storageAdapter.WriteAsync(savedNode, options, ct),
                        Scheduler.Default)
                    .Select(_ => savedNode);

            return writeAdapter.Do(_ =>
                _changeNotifier?.NotifyChange(isNew
                    ? DataChangeNotification.Created(normalizedPath, savedNode)
                    : DataChangeNotification.Updated(normalizedPath, savedNode)));
        });

    public IObservable<string> DeleteNode(string path, bool recursive = false) =>
        Observable.Defer(() =>
        {
            var normalizedPath = NormalizePath(path);
            var keys = recursive
                ? _nodes.Keys
                    .Where(k => k.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)
                                || k.StartsWith(normalizedPath + "/", StringComparison.OrdinalIgnoreCase))
                    .ToList()
                : new List<string> { normalizedPath };

            // Remove each key in turn — synchronous dict ops, then a single
            // Task→IObservable bridge per key for the storage-adapter delete.
            // Aggregate via IgnoreElements().Concat so the chain stays cold and
            // sequential without bridging back to Task between hops.
            var deleteOps = keys.Select(key =>
                Observable.Defer(() =>
                {
                    _nodes.TryRemove(key, out var removedNode);

                    var adapterDelete = _storageAdapter is null
                        ? Observable.Return(System.Reactive.Unit.Default)
                        : Observable.FromAsync(
                            ct => _storageAdapter.DeleteAsync(key, ct),
                            Scheduler.Default);

                    return adapterDelete.Do(_ =>
                        _changeNotifier?.NotifyChange(DataChangeNotification.Deleted(key, removedNode)));
                }));

            return deleteOps
                .Aggregate(
                    Observable.Return(System.Reactive.Unit.Default),
                    (acc, next) => acc.IgnoreElements().Concat(next))
                .Select(_ => path);
        });

    public IObservable<MeshNode> MoveNode(string sourcePath, string targetPath, JsonSerializerOptions options) =>
        Observable.Defer(() =>
        {
            var normalizedSource = NormalizePath(sourcePath);
            var normalizedTarget = NormalizePath(targetPath);

            if (!_nodes.TryGetValue(normalizedSource, out var sourceNode))
                return Observable.Throw<MeshNode>(
                    new InvalidOperationException($"Source node not found: {sourcePath}"));

            if (_nodes.ContainsKey(normalizedTarget))
                return Observable.Throw<MeshNode>(
                    new InvalidOperationException($"Target path already exists: {targetPath}"));

            var descendantKeys = _nodes.Keys
                .Where(k => k.StartsWith(normalizedSource + "/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Build moved-node projections — pure data transforms, no I/O yet.
            var movedRoot = MeshNode.FromPath(targetPath) with
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

            var movedDescendants = descendantKeys
                .Select(descPath =>
                {
                    if (!_nodes.TryGetValue(descPath, out var descNode))
                        return ((string Old, MeshNode Moved)?)null;
                    var newPath = normalizedTarget + descPath[normalizedSource.Length..];
                    var moved = MeshNode.FromPath(newPath) with
                    {
                        Name = descNode.Name,
                        NodeType = descNode.NodeType,
                        Icon = descNode.Icon,
                        Order = descNode.Order,
                        Content = descNode.Content,
                        AssemblyLocation = descNode.AssemblyLocation,
                        HubConfiguration = descNode.HubConfiguration,
                        GlobalServiceConfigurations = descNode.GlobalServiceConfigurations
                    };
                    return ((string Old, MeshNode Moved)?)(descPath, moved);
                })
                .Where(t => t.HasValue)
                .Select(t => t!.Value)
                .ToList();

            // Compose the move pipeline: save root → save descendants → migrate
            // comments → delete sources. Each step is a cold IObservable; Concat
            // sequentializes without bridging to Task.
            var saveDescendantOps = movedDescendants
                .Select(m => SaveNode(m.Moved, options).IgnoreElements().Cast<Unit>());

            var saveRoot = SaveNode(movedRoot, options);

            var descendantsObs = saveDescendantOps.Aggregate(
                (IObservable<Unit>)Observable.Return(Unit.Default),
                (acc, next) => acc.IgnoreElements().Concat(next));

            return saveRoot
                .SelectMany(saved => descendantsObs.Select(_ => saved))
                .Do(_ =>
                {
                    // Migrate comments — pure dict ops, synchronous.
                    var allOldPaths = new[] { normalizedSource }.Concat(descendantKeys);
                    foreach (var oldPath in allOldPaths)
                    {
                        var newPath = oldPath == normalizedSource
                            ? normalizedTarget
                            : normalizedTarget + oldPath[normalizedSource.Length..];

                        var commentsToMigrate = _comments.Values
                            .Where(c => NormalizePath(c.PrimaryNodePath ?? "").Equals(oldPath, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        foreach (var comment in commentsToMigrate)
                            _comments[comment.Id] = comment with { PrimaryNodePath = newPath };
                    }
                })
                .SelectMany(saved =>
                {
                    // Delete originals — bridge per-key at the IStorageAdapter leaf.
                    var oldPaths = new[] { normalizedSource }.Concat(descendantKeys).ToList();
                    var deleteOps = oldPaths.Select(oldPath => Observable.Defer<Unit>(() =>
                    {
                        _nodes.TryRemove(oldPath, out _);
                        return _storageAdapter is null
                            ? Observable.Return(Unit.Default)
                            : Observable.FromAsync(
                                ct => _storageAdapter.DeleteAsync(oldPath, ct),
                                Scheduler.Default);
                    }));

                    return deleteOps
                        .Aggregate(
                            Observable.Return(Unit.Default),
                            (acc, next) => acc.IgnoreElements().Concat(next))
                        .Select(_ => saved);
                });
        });

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

    public IObservable<bool> Exists(string path) =>
        Observable.Defer(() =>
        {
            var normalizedPath = NormalizePath(path);
            if (_nodes.ContainsKey(normalizedPath))
                return Observable.Return(true);
            return _storageAdapter is null
                ? Observable.Return(false)
                : Observable.FromAsync(ct => _storageAdapter.ExistsAsync(path, ct), Scheduler.Default);
        });

    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options) =>
        Observable.FromAsync(ct => FindBestPrefixMatchAsyncImpl(fullPath, options, ct), Scheduler.Default);

    private async Task<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatchAsyncImpl(
        string fullPath, JsonSerializerOptions options, CancellationToken ct)
    {
        var normalizedPath = NormalizePath(fullPath);
        if (string.IsNullOrEmpty(normalizedPath))
            return (null, 0);

        // Try storage adapter first (e.g., PostgreSQL with dedicated SQL).
        // The adapter is expected to return the longest prefix it can find;
        // if it covers the full path we accept it directly. If it covers only
        // a partial prefix, we still walk down the rest because the cached
        // adapter view may lag behind the on-disk state.
        var pathSegments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var bestMatch = (Node: (MeshNode?)null, Depth: 0);
        if (_storageAdapter != null)
        {
            var (adapterNode, adapterSegments) = await _storageAdapter.FindBestPrefixMatchAsync(normalizedPath, options, ct);
            if (adapterNode != null && adapterSegments == pathSegments.Length)
                return (adapterNode, adapterSegments);
            if (adapterNode != null && adapterSegments > bestMatch.Depth)
                bestMatch = (adapterNode, adapterSegments);
        }

        // In-memory LINQ scan. Mirrors the longest-prefix semantics of the
        // adapter result: a node at "ACME/ProductLaunch" can prefix-match
        // "ACME/ProductLaunch/Todo/LaunchEvent" with depth=2.
        var inMemory = _nodes.Values
            .Where(n =>
            {
                var nodePath = NormalizePath(n.Path);
                return normalizedPath.Equals(nodePath, StringComparison.OrdinalIgnoreCase)
                    || normalizedPath.StartsWith(nodePath + "/", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(n => n.Path.Length)
            .FirstOrDefault();
        if (inMemory != null)
        {
            var depth = inMemory.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
            if (depth == pathSegments.Length)
                return (inMemory, depth);
            if (depth > bestMatch.Depth)
                bestMatch = (inMemory, depth);
        }

        // Walk down via the storage adapter for paths deeper than what the
        // in-memory scan covered. Without this, a stale cache entry for an
        // ancestor (e.g. ACME/ProductLaunch loaded by an earlier lookup)
        // short-circuits the resolver and downstream routing reports
        // "No node found at X. Closest ancestor is ACME/ProductLaunch" even
        // though the leaf file (ACME/ProductLaunch/Todo/LaunchEvent.json)
        // sits right next to a sibling that resolved correctly. Read each
        // depth from full → shallowest; the first hit wins (deepest match).
        if (_storageAdapter != null)
        {
            for (int depth = pathSegments.Length; depth > bestMatch.Depth; depth--)
            {
                var testPath = string.Join("/", pathSegments.Take(depth));
                var node = await GetNodeAsyncCore(testPath, options, ct);
                if (node != null)
                    return (node, depth);
            }
        }

        return bestMatch.Node != null ? (bestMatch.Node, bestMatch.Depth) : (null, 0);
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

    public IObservable<Comment> AddComment(Comment comment, JsonSerializerOptions options) =>
        Observable.Defer(() =>
        {
            var savedComment = comment with
            {
                Id = string.IsNullOrEmpty(comment.Id) ? Guid.NewGuid().ToString() : comment.Id,
                CreatedAt = comment.CreatedAt == default ? DateTimeOffset.UtcNow : comment.CreatedAt
            };
            _comments[savedComment.Id] = savedComment;
            return Observable.Return(savedComment);
        });

    public IObservable<string> DeleteComment(string commentId) =>
        Observable.Defer(() =>
        {
            _comments.TryRemove(commentId, out _);
            return Observable.Return(commentId);
        });

    public IObservable<Comment?> GetComment(string commentId) =>
        Observable.Defer(() =>
        {
            _comments.TryGetValue(commentId, out var comment);
            return Observable.Return(comment);
        });

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

    public IObservable<IReadOnlyCollection<object>> SavePartitionObjects(
        string nodePath,
        string? subPath,
        IReadOnlyCollection<object> objects,
        JsonSerializerOptions options) =>
        Observable.Defer(() =>
        {
            var key = GetPartitionKey(nodePath, subPath);
            var hadExisting = _partitionData.ContainsKey(key);

            _partitionData[key] = objects.ToList();

            var adapterWrite = _storageAdapter is null
                ? Observable.Return(System.Reactive.Unit.Default)
                : Observable.FromAsync(
                    ct => _storageAdapter.SavePartitionObjectsAsync(nodePath, subPath, objects, options, ct),
                    Scheduler.Default);

            return adapterWrite
                .Do(_ =>
                {
                    if (_changeNotifier != null)
                    {
                        foreach (var obj in objects)
                            _changeNotifier.NotifyChange(hadExisting
                                ? DataChangeNotification.Updated(key, obj)
                                : DataChangeNotification.Created(key, obj));
                    }
                })
                .Select(_ => objects);
        });

    public IObservable<string> DeletePartitionObjects(
        string nodePath,
        string? subPath = null) =>
        Observable.Defer<string>(() =>
        {
            var key = GetPartitionKey(nodePath, subPath);
            _partitionData.TryRemove(key, out var removedObjects);

            var adapterDelete = _storageAdapter is null
                ? Observable.Return(System.Reactive.Unit.Default)
                : Observable.FromAsync(
                    ct => _storageAdapter.DeletePartitionObjectsAsync(nodePath, subPath, ct),
                    Scheduler.Default);

            return adapterDelete
                .Do(_ =>
                {
                    if (_changeNotifier != null && removedObjects != null)
                        foreach (var obj in removedObjects)
                            _changeNotifier.NotifyChange(DataChangeNotification.Deleted(key, obj));
                })
                .Select(_ => subPath ?? nodePath);
        });

    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(
        string nodePath,
        string? subPath = null) =>
        _storageAdapter is null
            ? Observable.Return<DateTimeOffset?>(DateTimeOffset.UtcNow)
            : Observable.FromAsync(
                ct => _storageAdapter.GetPartitionMaxTimestampAsync(nodePath, subPath, ct),
                Scheduler.Default);

    #endregion
}
