using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
    /// Discovers partitions not yet provisioned, provisions each, and yields its key and query provider.
    /// Already-provisioned partitions are skipped. Safe to call concurrently.
    /// </summary>
    internal async IAsyncEnumerable<(string Key, IMeshQueryProvider Provider)> DiscoverNewProvidersAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Link caller's token with a 30s startup timeout
        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startupCts.CancelAfter(TimeSpan.FromSeconds(30));
        var startupCt = startupCts.Token;
        var partitions = await _factory.DiscoverPartitionsAsync(startupCt);

        foreach (var segment in partitions)
        {
            if (_stores.ContainsKey(segment))
                continue;

            var partition = await _factory.CreateStoreAsync(segment, startupCt);
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
        var key = ResolvePartitionKey(path);
        return key != null && _stores.TryGetValue(key, out var store) ? store : null;
    }

    /// <summary>
    /// Gets the partition prefix for a given path (longest matching registered prefix).
    /// </summary>
    internal string? GetPartitionPrefix(string? path)
        => PathPartition.FindLongestMatchingPrefix(path, _stores.Keys);

    /// <summary>
    /// Resolves the partition key for a given path using longest-prefix matching.
    /// Walks from full path down to first segment, returns the first _stores key that matches.
    /// </summary>
    private string? ResolvePartitionKey(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        // Walk from full path down to first segment
        var test = path;
        while (true)
        {
            if (_stores.ContainsKey(test))
                return test;

            var lastSlash = test.LastIndexOf('/');
            if (lastSlash < 0) break;
            test = test[..lastSlash];
        }

        return null;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // 1. Collect every PartitionDefinition declared by any static-provider /
        //    config-time AddMeshNodes seed. These tell the routing layer which
        //    partitions exist and where they live. See `AddDocumentation` for the
        //    canonical pattern: registers a `Documentation` Partition node with
        //    `Content = new PartitionDefinition { Namespace = "Doc", DataSource = "static" }`.
        var allStaticNodes = _staticNodeProviders
            .SelectMany(p => p.GetStaticNodes())
            .ToList();

        var allPartitionDefs = allStaticNodes
            .Select(n => n.Content)
            .OfType<PartitionDefinition>()
            .ToList();

        // 2. Pre-init writable partitions (DataSource != "static") on the backend
        //    factory so PostgreSQL `CREATE SCHEMA`, Cosmos containers, etc. are ready.
        //    Static-only partitions skip this — they have no backing store to provision.
        var writableDefs = allPartitionDefs
            .Where(d => !string.Equals(d.DataSource, "static", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (writableDefs.Count > 0)
            await _factory.InitializeDefaultPartitionsAsync(writableDefs, ct);

        // 3. Discover existing partitions on the writable backend (FS subdirs,
        //    PostgreSQL schemas, Cosmos containers) — this also re-registers the
        //    ones we just init'd in step 2.
        await foreach (var (_, _) in DiscoverNewProvidersAsync(ct))
        { }

        // 4. Register a read-only StaticNodePartitionStore for every PartitionDefinition
        //    whose DataSource == "static". The store is populated with all
        //    IStaticNodeProvider nodes whose first segment matches the partition's
        //    Namespace. This surfaces NodeType definitions, doc namespaces, and test
        //    seed nodes through the same routing path as writable partitions — without
        //    leaking IStaticNodeProvider into the writable persisters
        //    (InMemoryPersistenceService, FileSystemPersistenceService, ...).
        //    See Doc/Architecture/PartitionedPersistence.md §"Where Partitions Come From".
        foreach (var def in allPartitionDefs)
        {
            if (!string.Equals(def.DataSource, "static", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrEmpty(def.Namespace))
                continue;
            if (_stores.ContainsKey(def.Namespace))
                continue; // a writable partition already claimed this name

            var nodesInPartition = allStaticNodes
                .Where(n => string.Equals(
                    PathPartition.GetFirstSegment(n.Path),
                    def.Namespace,
                    StringComparison.OrdinalIgnoreCase));
            var store = new StaticNodePartitionStore(nodesInPartition);
            if (_stores.TryAdd(def.Namespace, store))
            {
                _queryProviders[def.Namespace] = new Query.InMemoryMeshQuery(store, changeNotifier: _changeNotifier);
            }
        }
    }


    #region Node Operations

    public IObservable<MeshNode?> GetNode(string path, JsonSerializerOptions options)
        => Observable.FromAsync(ct => GetNodeAsyncCore(path, options, ct));

    /// <summary>
    /// Test/back-compat shim. Production callers go through <see cref="GetNode"/>.
    /// </summary>
    [Obsolete("Use GetNode(path, options) which returns IObservable<MeshNode?>.")]
    public Task<MeshNode?> GetNodeAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
        => GetNodeAsyncCore(path, options, ct);

    private async Task<MeshNode?> GetNodeAsyncCore(string path, JsonSerializerOptions options, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);

        var partitionKey = ResolvePartitionKey(path);
        if (partitionKey == null || !_stores.TryGetValue(partitionKey, out var store))
            return null;

        return await store.GetNode(path, options).FirstAsync().ToTask(ct);
    }

    public async IAsyncEnumerable<MeshNode> GetChildrenAsync(
        string? parentPath,
        JsonSerializerOptions options)
    {
        await EnsureInitializedAsync();
        var segment = PathPartition.GetFirstSegment(parentPath);

        if (segment == null)
        {
            // Root level: each partition contributes its root node
            foreach (var (seg, store) in _stores)
            {
                var rootNode = await store.GetNode(seg, options).FirstAsync().ToTask();
                if (rootNode != null)
                    yield return rootNode;
            }
            yield break;
        }

        var core = TryGetStore(parentPath);
        if (core == null) yield break;
        await foreach (var child in core.GetChildrenAsync(parentPath, options))
            yield return child;
    }

    public async IAsyncEnumerable<MeshNode> GetAllChildrenAsync(
        string? parentPath,
        JsonSerializerOptions options)
    {
        await EnsureInitializedAsync();
        var segment = PathPartition.GetFirstSegment(parentPath);
        if (segment == null) yield break;

        var core = TryGetStore(parentPath);
        if (core == null) yield break;
        await foreach (var child in core.GetAllChildrenAsync(parentPath, options))
            yield return child;
    }

    public async IAsyncEnumerable<MeshNode> GetDescendantsAsync(
        string? parentPath,
        JsonSerializerOptions options)
    {
        await EnsureInitializedAsync();
        var segment = PathPartition.GetFirstSegment(parentPath);

        if (segment == null)
        {
            // Root level: each partition contributes root node + all descendants
            foreach (var (seg, store) in _stores)
            {
                var rootNode = await store.GetNode(seg, options).FirstAsync().ToTask();
                if (rootNode != null)
                    yield return rootNode;

                await foreach (var desc in store.GetDescendantsAsync(seg, options))
                    yield return desc;
            }
            yield break;
        }

        var core = TryGetStore(parentPath);
        if (core == null) yield break;
        await foreach (var desc in core.GetDescendantsAsync(parentPath, options))
            yield return desc;
    }

    public async IAsyncEnumerable<MeshNode> GetAllDescendantsAsync(
        string? parentPath,
        JsonSerializerOptions options)
    {
        await EnsureInitializedAsync();
        var segment = PathPartition.GetFirstSegment(parentPath);

        if (segment == null)
        {
            foreach (var (seg, store) in _stores)
            {
                var rootNode = await store.GetNode(seg, options).FirstAsync().ToTask();
                if (rootNode != null)
                    yield return rootNode;

                await foreach (var desc in store.GetAllDescendantsAsync(seg, options))
                    yield return desc;
            }
            yield break;
        }

        var core = TryGetStore(parentPath);
        if (core == null) yield break;
        await foreach (var desc in core.GetAllDescendantsAsync(parentPath, options))
            yield return desc;
    }

    public async Task<MeshNode> SaveNodeAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var segment = ResolvePartitionKey(node.Path)
            ?? PathPartition.GetFirstSegment(node.Path)
            ?? throw new ArgumentException($"Cannot save node: no partition found for path '{node.Path}'");

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
        var sourceSegment = ResolvePartitionKey(sourcePath)
            ?? PathPartition.GetFirstSegment(sourcePath)
            ?? throw new ArgumentException($"No partition found for source path '{sourcePath}'");
        var targetSegment = ResolvePartitionKey(targetPath)
            ?? PathPartition.GetFirstSegment(targetPath)
            ?? throw new ArgumentException($"No partition found for target path '{targetPath}'");

        if (string.Equals(sourceSegment, targetSegment, StringComparison.OrdinalIgnoreCase))
        {
            // Same partition: delegate directly
            var store = await GetOrCreateStoreAsync(sourceSegment, ct);
            return await store.MoveNodeAsync(sourcePath, targetPath, options, ct);
        }

        // Cross-partition move: move root node + all descendants
        var sourceStore = await GetOrCreateStoreAsync(sourceSegment, ct);
        var targetStore = await GetOrCreateStoreAsync(targetSegment, ct);

        var sourceNode = await sourceStore.GetNode(sourcePath, options).FirstAsync().ToTask(ct)
            ?? throw new InvalidOperationException($"Source node not found: {sourcePath}");

        if (await targetStore.ExistsAsync(targetPath, ct))
            throw new InvalidOperationException($"Target path already exists: {targetPath}");

        // Collect all descendants from source
        var descendants = new List<MeshNode>();
        await foreach (var desc in sourceStore.GetDescendantsAsync(sourcePath, options))
            descendants.Add(desc);

        // Move descendants first
        foreach (var descendant in descendants)
        {
            var newDescPath = targetPath + descendant.Path[sourcePath.Length..];
            var descTargetSeg = ResolvePartitionKey(newDescPath) ?? PathPartition.GetFirstSegment(newDescPath);
            var descStore = descTargetSeg != null && !string.Equals(descTargetSeg, targetSegment, StringComparison.OrdinalIgnoreCase)
                ? await GetOrCreateStoreAsync(descTargetSeg, ct)
                : targetStore;

            var movedDesc = MeshNode.FromPath(newDescPath) with
            {
                Name = descendant.Name,
                NodeType = descendant.NodeType,
                Icon = descendant.Icon,
                Order = descendant.Order,
                Content = descendant.Content,
                AssemblyLocation = descendant.AssemblyLocation,
                HubConfiguration = descendant.HubConfiguration,
                GlobalServiceConfigurations = descendant.GlobalServiceConfigurations
            };

            await descStore.SaveNodeAsync(movedDesc, options, ct);
        }

        // Move root node
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

        // Delete source tree (recursive)
        await sourceStore.DeleteNodeAsync(sourcePath, recursive: true, ct);

        return movedNode;
    }

    public async IAsyncEnumerable<MeshNode> SearchAsync(
        string? parentPath,
        string query,
        JsonSerializerOptions options)
    {
        await EnsureInitializedAsync();
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

        var core = TryGetStore(parentPath);
        if (core == null) yield break;
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
        var store = TryGetStore(comment.PrimaryNodePath)
            ?? throw new ArgumentException($"No partition found for comment path '{comment.PrimaryNodePath}'");

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
        var store = TryGetStore(nodePath)
            ?? throw new ArgumentException($"No partition found for path '{nodePath}'");

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

    public IObservable<MeshNode?> GetNodeSecure(string path, string? userId, JsonSerializerOptions options)
        => Observable.FromAsync(ct => EnsureInitializedAsync(ct))
            .SelectMany(_ =>
            {
                var store = TryGetStore(path);
                if (store == null)
                    return Observable.Return<MeshNode?>(null);
                return store.GetNodeSecure(path, userId, options);
            });

    public IObservable<MeshNode> GetChildrenSecure(
        string? parentPath,
        string? userId,
        JsonSerializerOptions options)
        => Observable.FromAsync(() => EnsureInitializedAsync())
            .SelectMany(_ =>
            {
                var segment = PathPartition.GetFirstSegment(parentPath);

                if (segment == null)
                {
                    // Root level: each partition contributes its root node
                    return _stores
                        .Select(kvp => kvp.Value.GetNodeSecure(kvp.Key, userId, options))
                        .Concat()
                        .Where(n => n != null)
                        .Select(n => n!);
                }

                var core = TryGetStore(parentPath);
                if (core == null)
                    return Observable.Empty<MeshNode>();
                return core.GetChildrenSecure(parentPath, userId, options);
            });

    public IObservable<MeshNode> GetDescendantsSecure(
        string? parentPath,
        string? userId,
        JsonSerializerOptions options)
        => Observable.FromAsync(() => EnsureInitializedAsync())
            .SelectMany(_ =>
            {
                var segment = PathPartition.GetFirstSegment(parentPath);

                if (segment == null)
                {
                    // Root level: each partition contributes root node + all descendants
                    return _stores
                        .Select(kvp =>
                        {
                            var rootObs = kvp.Value.GetNodeSecure(kvp.Key, userId, options)
                                .Where(n => n != null)
                                .Select(n => n!);
                            var descObs = kvp.Value.GetDescendantsSecure(kvp.Key, userId, options);
                            return rootObs.Concat(descObs);
                        })
                        .Concat();
                }

                var core = TryGetStore(parentPath);
                if (core == null)
                    return Observable.Empty<MeshNode>();
                return core.GetDescendantsSecure(parentPath, userId, options);
            });

    #endregion
}
