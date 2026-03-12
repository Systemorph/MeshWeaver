using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Routing persistence core that maintains per-partition IStorageService instances.
/// Routes operations based on the first segment of the path.
/// Auto-provisions new partitions on first access via IPartitionedStoreFactory.
/// </summary>
internal class RoutingPersistenceServiceCore : IStorageService
{
    private readonly IPartitionedStoreFactory _factory;
    private readonly IDataChangeNotifier? _changeNotifier;
    private readonly IEnumerable<IStaticNodeProvider> _staticNodeProviders;
    private readonly ConcurrentDictionary<string, IStorageService> _stores = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IMeshQueryProvider> _queryProviders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IVersionQuery> _versionQueries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _provisionLock = new(1, 1);
    private volatile bool _initialized;

    /// <summary>
    /// Maps base path prefixes to partition names (e.g., "Doc" -> "Admin" for Documentation).
    /// Populated from Partition nodes in Admin/Partition namespace.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _basePathToPartition = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Partition metadata keyed by partition node ID (e.g., "Documentation" -> set of base paths).
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _partitionNamespaces = new(StringComparer.OrdinalIgnoreCase);

    public RoutingPersistenceServiceCore(
        IPartitionedStoreFactory factory,
        IDataChangeNotifier? changeNotifier = null,
        IEnumerable<IStaticNodeProvider>? staticNodeProviders = null)
    {
        _factory = factory;
        _changeNotifier = changeNotifier;
        _staticNodeProviders = staticNodeProviders ?? [];
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
    internal IReadOnlyDictionary<string, IVersionQuery> VersionQueries => _versionQueries;

    /// <summary>
    /// Gets all registered partition names.
    /// </summary>
    internal IEnumerable<string> PartitionNames => _stores.Keys;

    /// <summary>
    /// Gets the base-path-to-partition mapping for partition access filtering.
    /// </summary>
    internal IReadOnlyDictionary<string, string> BasePathToPartition => _basePathToPartition;

    /// <summary>
    /// Gets the partition metadata (partition ID -> namespace).
    /// </summary>
    internal IReadOnlyDictionary<string, string> PartitionNamespaces => _partitionNamespaces;

    /// <summary>
    /// Discovers partitions not yet provisioned, provisions each, and yields its key and query provider.
    /// Already-provisioned partitions are skipped. Safe to call concurrently.
    /// </summary>
    internal async IAsyncEnumerable<(string Key, IMeshQueryProvider Provider)> DiscoverNewProvidersAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var partitions = await _factory.DiscoverPartitionsAsync(ct);

        foreach (var segment in partitions)
        {
            if (_stores.ContainsKey(segment))
                continue;

            var partition = await _factory.CreateStoreAsync(segment, ct);
            var core = new InMemoryPersistenceService(partition.StorageAdapter, _changeNotifier);
            if (_stores.TryAdd(segment, core))
            {
                var queryProvider = partition.QueryProvider
                    ?? new Query.InMemoryMeshQuery(core, changeNotifier: _changeNotifier);
                _queryProviders[segment] = queryProvider;
                if (partition.VersionQuery != null)
                    _versionQueries[segment] = partition.VersionQuery;
                yield return (segment, queryProvider);
            }
        }
    }

    private async Task<IStorageService> GetOrCreateStoreAsync(string firstSegment, CancellationToken ct)
    {
        if (_stores.TryGetValue(firstSegment, out var existing))
            return existing;

        await _provisionLock.WaitAsync(ct);
        try
        {
            if (_stores.TryGetValue(firstSegment, out existing))
                return existing;

            var partition = await _factory.CreateStoreAsync(firstSegment, ct);
            var core = new InMemoryPersistenceService(partition.StorageAdapter, _changeNotifier);
            _stores[firstSegment] = core;
            var queryProvider = partition.QueryProvider
                ?? new Query.InMemoryMeshQuery(core, changeNotifier: _changeNotifier);
            _queryProviders[firstSegment] = queryProvider;
            if (partition.VersionQuery != null)
                _versionQueries[firstSegment] = partition.VersionQuery;
            return core;
        }
        finally
        {
            _provisionLock.Release();
        }
    }

    private IStorageService? TryGetStore(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        // Use ResolvePartitionKey for consistent routing (root-level → Admin)
        var resolved = ResolvePartitionKey(path);
        if (resolved != null && _stores.TryGetValue(resolved, out var resolvedStore))
            return resolvedStore;

        // Fall back to longest-prefix matching on store keys
        var prefix = PathPartition.FindLongestMatchingPrefix(path, _stores.Keys);
        if (prefix == null) return null;
        return _stores.TryGetValue(prefix, out var store) ? store : null;
    }

    /// <summary>
    /// Gets the partition prefix for a given path (longest matching registered prefix).
    /// </summary>
    internal string? GetPartitionPrefix(string? path)
        => PathPartition.FindLongestMatchingPrefix(path, _stores.Keys);

    /// <summary>
    /// Resolves the partition key for a given path.
    /// Root-level nodes (no '/' in path, e.g. "roland_Access") that don't match
    /// a known partition are routed to "Admin" to prevent rogue schema creation.
    /// </summary>
    private string? ResolvePartitionKey(string? path)
    {
        var firstSegment = PathPartition.GetFirstSegment(path);
        if (firstSegment == null) return null;

        // If the first segment matches a known partition, use it directly
        if (_stores.ContainsKey(firstSegment))
            return firstSegment;

        // Check base-path-to-partition mapping
        if (_basePathToPartition.TryGetValue(firstSegment, out var mapped))
            return mapped;

        // Root-level node (path has no '/') that doesn't match any partition → route to Admin
        if (path != null && !path.Contains('/') && _stores.ContainsKey("Admin"))
            return "Admin";

        // Multi-segment path with unknown first segment → auto-provision as before
        return firstSegment;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // 1. Initialize default partitions from static providers (creates schemas/tables for PostgreSQL)
        var defaultPartitions = _staticNodeProviders
            .SelectMany(p => p.GetStaticNodes())
            .Select(n => n.Content)
            .OfType<PartitionDefinition>()
            .ToList();

        if (defaultPartitions.Count > 0)
            await _factory.InitializeDefaultPartitionsAsync(defaultPartitions, ct);

        // 2. Discover all existing partitions (including the ones just created)
        await foreach (var (_, _) in DiscoverNewProvidersAsync(ct))
        { }

        // 3. Load partition metadata from Admin/Partition namespace
        await LoadPartitionMetadataAsync(ct);
    }

    /// <summary>
    /// Loads Partition nodes from the "Admin" store to populate base-path-to-partition mapping.
    /// Each Partition node's Content (PartitionDefinition) declares which base paths it serves.
    /// </summary>
    private async Task LoadPartitionMetadataAsync(CancellationToken ct = default)
    {
        if (!_stores.TryGetValue("Admin", out var adminStore))
            return;

        // Use default JsonSerializerOptions for reading — this is internal bootstrap
        var options = new JsonSerializerOptions();

        await foreach (var child in adminStore.GetChildrenAsync("Admin/Partition", options))
        {
            if (ct.IsCancellationRequested) break;

            if (child.Content is PartitionDefinition def && !string.IsNullOrEmpty(def.Namespace))
            {
                _partitionNamespaces[child.Id] = def.Namespace;

                // Map namespace to the partition that actually stores it.
                var storeKey = _stores.ContainsKey(def.Namespace) ? def.Namespace
                    : _stores.ContainsKey(child.Id) ? child.Id
                    : PathPartition.GetFirstSegment(def.Namespace);
                if (storeKey != null)
                    _basePathToPartition[def.Namespace] = storeKey;
            }
        }
    }

    #region Node Operations

    public async Task<MeshNode?> GetNodeAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        // Use ResolvePartitionKey so root-level nodes route to Admin
        var segment = ResolvePartitionKey(path);
        if (segment == null) return null;

        if (_stores.TryGetValue(segment, out var store))
            return await store.GetNodeAsync(path, options, ct);

        return null;
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
        var segment = ResolvePartitionKey(node.Path)
            ?? throw new ArgumentException("Cannot save node with empty path");

        var store = await GetOrCreateStoreAsync(segment, ct);
        var result = await store.SaveNodeAsync(node, options, ct);

        // When a Partition node is saved, initialize the schema for the new partition
        if (node.Content is PartitionDefinition def && !string.IsNullOrEmpty(def.Namespace))
            await EnsurePartitionSchemaAsync(def, ct);

        return result;
    }

    /// <summary>
    /// Ensures the schema/tables exist for a partition definition.
    /// Called when a Partition node is created (e.g., organization creation).
    /// </summary>
    private async Task EnsurePartitionSchemaAsync(PartitionDefinition def, CancellationToken ct)
    {
        // Initialize schema and satellite tables (idempotent)
        await _factory.InitializeDefaultPartitionsAsync([def], ct);

        // Provision the store for routing if not already present
        if (!_stores.ContainsKey(def.Namespace))
        {
            var partition = await _factory.CreateStoreAsync(def.Namespace, ct);
            var core = new InMemoryPersistenceService(partition.StorageAdapter, _changeNotifier);
            if (_stores.TryAdd(def.Namespace, core))
            {
                var queryProvider = partition.QueryProvider
                    ?? new Query.InMemoryMeshQuery(core, changeNotifier: _changeNotifier);
                _queryProviders[def.Namespace] = queryProvider;
                if (partition.VersionQuery != null)
                    _versionQueries[def.Namespace] = partition.VersionQuery;
            }
        }

        // Update partition metadata
        _partitionNamespaces[def.Namespace] = def.Namespace;
        _basePathToPartition[def.Namespace] = def.Namespace;
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

    public async Task<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatchAsync(
        string fullPath, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var segment = PathPartition.GetFirstSegment(fullPath);
        if (segment == null) return (null, 0);

        await EnsureInitializedAsync(ct);
        var store = TryGetStore(fullPath);
        if (store == null) return (null, 0);

        return await store.FindBestPrefixMatchAsync(fullPath, options, ct);
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
