using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Routing persistence core that maintains per-partition IPersistenceServiceCore instances.
/// Routes operations based on the first segment of the path.
/// Auto-provisions new partitions on first access via IPartitionedStoreFactory.
/// </summary>
public class RoutingPersistenceServiceCore : IPersistenceServiceCore
{
    private readonly IPartitionedStoreFactory _factory;
    private readonly ConcurrentDictionary<string, IPersistenceServiceCore> _stores = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IMeshQueryProvider> _queryProviders = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _provisionLock = new(1, 1);
    private volatile bool _initialized;

    public RoutingPersistenceServiceCore(IPartitionedStoreFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Ensures partitions have been discovered at least once.
    /// Uses double-checked locking for thread safety.
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _provisionLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await InitializeAsync(ct);
            _initialized = true;
        }
        finally
        {
            _provisionLock.Release();
        }
    }

    /// <summary>
    /// Gets all registered query providers (for use by RoutingMeshQueryProvider).
    /// </summary>
    internal IReadOnlyDictionary<string, IMeshQueryProvider> QueryProviders => _queryProviders;

    /// <summary>
    /// Gets all registered partition names.
    /// </summary>
    internal IEnumerable<string> PartitionNames => _stores.Keys;

    /// <summary>
    /// Discovers partitions not yet provisioned, provisions each, and yields its query provider.
    /// Already-provisioned partitions are skipped. Safe to call concurrently.
    /// </summary>
    internal async IAsyncEnumerable<IMeshQueryProvider> DiscoverNewProvidersAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var partitions = await _factory.DiscoverPartitionsAsync(ct);

        foreach (var segment in partitions)
        {
            if (_stores.ContainsKey(segment))
                continue;

            var partition = await _factory.CreateStoreAsync(segment, ct);
            if (_stores.TryAdd(segment, partition.PersistenceCore))
            {
                if (partition.QueryProvider != null)
                {
                    _queryProviders[segment] = partition.QueryProvider;
                    yield return partition.QueryProvider;
                }
            }
        }
    }

    private async Task<IPersistenceServiceCore> GetOrCreateStoreAsync(string firstSegment, CancellationToken ct)
    {
        if (_stores.TryGetValue(firstSegment, out var existing))
            return existing;

        await _provisionLock.WaitAsync(ct);
        try
        {
            if (_stores.TryGetValue(firstSegment, out existing))
                return existing;

            var partition = await _factory.CreateStoreAsync(firstSegment, ct);
            _stores[firstSegment] = partition.PersistenceCore;
            if (partition.QueryProvider != null)
                _queryProviders[firstSegment] = partition.QueryProvider;
            return partition.PersistenceCore;
        }
        finally
        {
            _provisionLock.Release();
        }
    }

    private IPersistenceServiceCore? TryGetStore(string? path)
    {
        var segment = PathPartition.GetFirstSegment(path);
        if (segment == null) return null;
        return _stores.TryGetValue(segment, out var store) ? store : null;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Drain to ensure all partitions are provisioned
        await foreach (var _ in DiscoverNewProvidersAsync(ct))
        { }
    }

    #region Node Operations

    public async Task<MeshNode?> GetNodeAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var segment = PathPartition.GetFirstSegment(path);
        if (segment == null) return null;

        await EnsureInitializedAsync(ct);
        var store = TryGetStore(path);
        if (store == null) return null;

        return await store.GetNodeAsync(path, options, ct);
    }

    public async IAsyncEnumerable<MeshNode> GetChildrenAsync(
        string? parentPath,
        JsonSerializerOptions options)
    {
        var segment = PathPartition.GetFirstSegment(parentPath);

        if (segment == null)
        {
            // Root level: each partition contributes its root node
            foreach (var (seg, store) in _stores)
            {
                var rootNode = await store.GetNodeAsync(seg, options);
                if (rootNode != null)
                    yield return rootNode;
            }
            yield break;
        }

        var core = await GetOrCreateStoreAsync(segment, default);
        await foreach (var child in core.GetChildrenAsync(parentPath, options))
            yield return child;
    }

    public async IAsyncEnumerable<MeshNode> GetDescendantsAsync(
        string? parentPath,
        JsonSerializerOptions options)
    {
        var segment = PathPartition.GetFirstSegment(parentPath);

        if (segment == null)
        {
            // Root level: each partition contributes root node + all descendants
            foreach (var (seg, store) in _stores)
            {
                var rootNode = await store.GetNodeAsync(seg, options);
                if (rootNode != null)
                    yield return rootNode;

                await foreach (var desc in store.GetDescendantsAsync(seg, options))
                    yield return desc;
            }
            yield break;
        }

        var core = await GetOrCreateStoreAsync(segment, default);
        await foreach (var desc in core.GetDescendantsAsync(parentPath, options))
            yield return desc;
    }

    public async Task<MeshNode> SaveNodeAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var segment = PathPartition.GetFirstSegment(node.Path)
            ?? throw new ArgumentException("Cannot save node with empty path");

        var store = await GetOrCreateStoreAsync(segment, ct);
        return await store.SaveNodeAsync(node, options, ct);
    }

    public async Task DeleteNodeAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        var segment = PathPartition.GetFirstSegment(path);
        if (segment == null) return;

        var store = TryGetStore(path);
        if (store == null) return;

        await store.DeleteNodeAsync(path, recursive, ct);
    }

    public async Task<MeshNode> MoveNodeAsync(string sourcePath, string targetPath, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var sourceSegment = PathPartition.GetFirstSegment(sourcePath)
            ?? throw new ArgumentException("Source path cannot be empty");
        var targetSegment = PathPartition.GetFirstSegment(targetPath)
            ?? throw new ArgumentException("Target path cannot be empty");

        if (string.Equals(sourceSegment, targetSegment, StringComparison.OrdinalIgnoreCase))
        {
            // Same partition: delegate directly
            var store = await GetOrCreateStoreAsync(sourceSegment, ct);
            return await store.MoveNodeAsync(sourcePath, targetPath, options, ct);
        }

        // Cross-partition move
        var sourceStore = await GetOrCreateStoreAsync(sourceSegment, ct);
        var targetStore = await GetOrCreateStoreAsync(targetSegment, ct);

        var sourceNode = await sourceStore.GetNodeAsync(sourcePath, options, ct)
            ?? throw new InvalidOperationException($"Source node not found: {sourcePath}");

        if (await targetStore.ExistsAsync(targetPath, ct))
            throw new InvalidOperationException($"Target path already exists: {targetPath}");

        // Create moved node at target
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

        await targetStore.SaveNodeAsync(movedNode, options, ct);
        await sourceStore.DeleteNodeAsync(sourcePath, false, ct);

        return movedNode;
    }

    public async IAsyncEnumerable<MeshNode> SearchAsync(
        string? parentPath,
        string query,
        JsonSerializerOptions options)
    {
        var segment = PathPartition.GetFirstSegment(parentPath);

        if (segment == null)
        {
            // Fan out to all partitions, scoping each to its own segment
            foreach (var (seg, store) in _stores)
            {
                await foreach (var node in store.SearchAsync(seg, query, options))
                    yield return node;
            }
            yield break;
        }

        var core = await GetOrCreateStoreAsync(segment, default);
        await foreach (var node in core.SearchAsync(parentPath, query, options))
            yield return node;
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var store = TryGetStore(path);
        if (store == null) return false;
        return await store.ExistsAsync(path, ct);
    }

    #endregion

    #region Comments

    public async IAsyncEnumerable<Comment> GetCommentsAsync(
        string nodePath,
        JsonSerializerOptions options)
    {
        var segment = PathPartition.GetFirstSegment(nodePath);
        if (segment == null) yield break;

        var store = TryGetStore(nodePath);
        if (store == null) yield break;

        await foreach (var comment in store.GetCommentsAsync(nodePath, options))
            yield return comment;
    }

    public async Task<Comment> AddCommentAsync(Comment comment, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var segment = PathPartition.GetFirstSegment(comment.PrimaryNodePath)
            ?? throw new ArgumentException("Comment must have a primary node path");

        var store = await GetOrCreateStoreAsync(segment, ct);
        return await store.AddCommentAsync(comment, options, ct);
    }

    public async Task DeleteCommentAsync(string commentId, CancellationToken ct = default)
    {
        // Fan out to all partitions since we don't know which one has the comment
        foreach (var store in _stores.Values)
        {
            await store.DeleteCommentAsync(commentId, ct);
        }
    }

    public async Task<Comment?> GetCommentAsync(string commentId, CancellationToken ct = default)
    {
        // Fan out to all partitions
        foreach (var store in _stores.Values)
        {
            var comment = await store.GetCommentAsync(commentId, ct);
            if (comment != null)
                return comment;
        }
        return null;
    }

    #endregion

    #region Partition Storage

    public async IAsyncEnumerable<object> GetPartitionObjectsAsync(string nodePath, string? subPath, JsonSerializerOptions options)
    {
        await EnsureInitializedAsync();
        var store = TryGetStore(nodePath);
        if (store == null) yield break;
        await foreach (var obj in store.GetPartitionObjectsAsync(nodePath, subPath, options))
            yield return obj;
    }

    public async Task SavePartitionObjectsAsync(string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var segment = PathPartition.GetFirstSegment(nodePath)
            ?? throw new ArgumentException("Node path cannot be empty for partition storage");

        var store = await GetOrCreateStoreAsync(segment, ct);
        await store.SavePartitionObjectsAsync(nodePath, subPath, objects, options, ct);
    }

    public async Task DeletePartitionObjectsAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
    {
        var store = TryGetStore(nodePath);
        if (store == null) return;
        await store.DeletePartitionObjectsAsync(nodePath, subPath, ct);
    }

    public async Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
    {
        var store = TryGetStore(nodePath);
        if (store == null) return null;
        return await store.GetPartitionMaxTimestampAsync(nodePath, subPath, ct);
    }

    #endregion

    #region Secure Operations

    public async Task<MeshNode?> GetNodeSecureAsync(string path, string? userId, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var store = TryGetStore(path);
        if (store == null) return null;
        return await store.GetNodeSecureAsync(path, userId, options, ct);
    }

    public async IAsyncEnumerable<MeshNode> GetChildrenSecureAsync(
        string? parentPath,
        string? userId,
        JsonSerializerOptions options)
    {
        var segment = PathPartition.GetFirstSegment(parentPath);

        if (segment == null)
        {
            // Root level: each partition contributes its root node
            foreach (var (seg, store) in _stores)
            {
                var rootNode = await store.GetNodeSecureAsync(seg, userId, options);
                if (rootNode != null)
                    yield return rootNode;
            }
            yield break;
        }

        var core = await GetOrCreateStoreAsync(segment, default);
        await foreach (var child in core.GetChildrenSecureAsync(parentPath, userId, options))
            yield return child;
    }

    public async IAsyncEnumerable<MeshNode> GetDescendantsSecureAsync(
        string? parentPath,
        string? userId,
        JsonSerializerOptions options)
    {
        var segment = PathPartition.GetFirstSegment(parentPath);

        if (segment == null)
        {
            // Root level: each partition contributes root node + all descendants
            foreach (var (seg, store) in _stores)
            {
                var rootNode = await store.GetNodeSecureAsync(seg, userId, options);
                if (rootNode != null)
                    yield return rootNode;

                await foreach (var desc in store.GetDescendantsSecureAsync(seg, userId, options))
                    yield return desc;
            }
            yield break;
        }

        var core = await GetOrCreateStoreAsync(segment, default);
        await foreach (var desc in core.GetDescendantsSecureAsync(parentPath, userId, options))
            yield return desc;
    }

    #endregion
}
