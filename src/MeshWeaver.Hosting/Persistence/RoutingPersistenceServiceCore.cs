using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Concurrency;
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
    private readonly IEnumerable<IPartitionStorageProvider> _partitionStorageProviders;
    private readonly ConcurrentDictionary<string, IStorageService> _stores = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IMeshQueryProvider> _queryProviders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IVersionQuery> _versionQueries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _provisionLock = new(1, 1);
    private volatile bool _initialized;


    public RoutingPersistenceServiceCore(
        IPartitionedStoreFactory factory,
        IDataChangeNotifier? changeNotifier = null,
        IEnumerable<IStaticNodeProvider>? staticNodeProviders = null,
        IEnumerable<IPartitionStorageProvider>? partitionStorageProviders = null)
    {
        _factory = factory;
        _changeNotifier = changeNotifier;
        _staticNodeProviders = staticNodeProviders ?? [];
        _partitionStorageProviders = partitionStorageProviders ?? [];

        // Synchronous seed: every IPartitionStorageProvider with an explicit
        // PartitionDefinition.Namespace is registered into _stores +
        // _queryProviders eagerly. This is the production wiring path for
        // read-only embedded-resource partitions (e.g. AddDocumentation), so
        // queries that fan out across all partitions reach Doc/Architecture/...
        // without waiting for a lazy InitializeAsync to run.
        //
        // Per Doc/Architecture/AsynchronousCalls.md: nothing async in
        // hub-reachable code. The query path that depends on _queryProviders
        // (RoutingMeshQueryProvider.QueryAsync / ObserveQuery) MUST NOT
        // .Wait() / .Result on EnsureInitializedAsync — that deadlocks the
        // hub action block. Pre-seed synchronously here instead.
        //
        // Backend-backed partitions (FileSystem subdirs, PostgreSQL schemas)
        // still discover lazily via DiscoverNewProvidersAsync — they have no
        // PartitionDefinition.Namespace at registration time.
        foreach (var provider in _partitionStorageProviders)
        {
            var ns = provider.PartitionDefinition?.Namespace;
            if (string.IsNullOrEmpty(ns)) continue;
            if (_stores.ContainsKey(ns)) continue;

            var core = new InMemoryPersistenceService(provider.Adapter, _changeNotifier);
            if (_stores.TryAdd(ns, core))
            {
                _queryProviders[ns] =
                    new Query.InMemoryMeshQuery(core, changeNotifier: _changeNotifier);
            }
        }
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
    /// Discovers partitions not yet provisioned, provisions each, and yields its
    /// key and query provider. Already-provisioned partitions are skipped. Safe
    /// to call concurrently.
    ///
    /// <para>Reactive shape: returns <see cref="IObservable{T}"/> so subscribers
    /// can compose without <c>await</c>. Inner <see cref="Observable.FromAsync{TResult}(Func{Task{TResult}})"/>
    /// runs the factory calls on the default scheduler (thread pool) — they
    /// never capture the caller's synchronization context, so a hub action
    /// block calling this won't deadlock waiting for its own pump. The
    /// <see cref="Observable.SelectMany{TSource,TResult}(IObservable{TSource},Func{TSource,IObservable{TResult}})"/>
    /// fan-out provisions partitions in parallel; each emits as its store is
    /// ready (so the slowest partition doesn't block the fastest).</para>
    /// </summary>
    internal IObservable<(string Key, IMeshQueryProvider Provider)> DiscoverNewProviders(CancellationToken ct = default)
    {
        // 30 s startup ceiling, composed with caller's token. The Observable.FromAsync
        // calls below pass `ct` through; the timeout is enforced via Observable.Timeout.
        return Observable
            .FromAsync(token => _factory.DiscoverPartitionsAsync(token), Scheduler.Default)
            .Timeout(TimeSpan.FromSeconds(30))
            .SelectMany(partitions => partitions.ToObservable())
            .Where(segment => !_stores.ContainsKey(segment))
            .SelectMany(segment =>
                Observable.FromAsync(token => _factory.CreateStoreAsync(segment, token), Scheduler.Default)
                    .Select(partition =>
                    {
                        var core = new InMemoryPersistenceService(partition.StorageAdapter, _changeNotifier);
                        if (!_stores.TryAdd(segment, core))
                            return ((string, IMeshQueryProvider)?)null;
                        var queryProvider = partition.QueryProvider
                            ?? new Query.InMemoryMeshQuery(core, changeNotifier: _changeNotifier);
                        _queryProviders[segment] = queryProvider;
                        if (partition.VersionQuery != null)
                            _versionQueries[segment] = partition.VersionQuery;
                        return (segment, queryProvider);
                    }))
            .Where(t => t.HasValue)
            .Select(t => t!.Value);
    }

    /// <summary>
    /// Backwards-compatible <see cref="IAsyncEnumerable{T}"/> wrapper around
    /// <see cref="DiscoverNewProviders"/> for existing <c>await foreach</c>
    /// callers. New code should prefer the observable form — see
    /// <c>Doc/Architecture/AsynchronousCalls.md</c>.
    /// </summary>
    internal async IAsyncEnumerable<(string Key, IMeshQueryProvider Provider)> DiscoverNewProvidersAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Bridge: subscribe the observable, push each emission into a channel,
        // and yield from the channel as the consumer awaits.
        var ch = System.Threading.Channels.Channel.CreateUnbounded<(string, IMeshQueryProvider)>();
        using var sub = DiscoverNewProviders(ct).Subscribe(
            value => ch.Writer.TryWrite(value),
            ex => ch.Writer.Complete(ex),
            () => ch.Writer.Complete());

        while (await ch.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (ch.Reader.TryRead(out var item))
                yield return item;
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

            // Sequential rule lookup: first IPartitionStorageProvider whose
            // Matches() returns true wins. This is the new routing model —
            // explicit rules in registration order, no DataSource string
            // discriminators, no special-cases inside the routing core.
            // See IPartitionStorageProvider.cs for the why.
            foreach (var provider in _partitionStorageProviders)
            {
                if (!provider.Matches(firstSegment))
                    continue;

                var providerCore = new InMemoryPersistenceService(provider.Adapter, _changeNotifier);
                await providerCore.InitializeAsync(System.Text.Json.JsonSerializerOptions.Default, ct);
                _stores[firstSegment] = providerCore;
                _queryProviders[firstSegment] =
                    new Query.InMemoryMeshQuery(providerCore, changeNotifier: _changeNotifier);
                return providerCore;
            }

            // No rule matched — fall through to the legacy partitioned-store
            // factory (FileSystem / Postgres / Cosmos). This is the implicit
            // catch-all until a wildcard provider is registered.
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
        // 0. Pre-seed stores for IPartitionStorageProvider rules that have
        //    a fixed PartitionDefinition (single-namespace rules like
        //    EmbeddedResource). Wildcard / pattern rules don't surface here
        //    — they'll be exercised lazily by GetOrCreateStoreAsync when
        //    a new first-segment path arrives. This keeps single-namespace
        //    partitions (e.g. Doc) visible to listings without forcing
        //    wildcard rules to enumerate every possible namespace upfront.
        foreach (var provider in _partitionStorageProviders)
        {
            var ns = provider.PartitionDefinition?.Namespace;
            if (string.IsNullOrEmpty(ns))
                continue;
            if (_stores.ContainsKey(ns))
                continue;

            var core = new InMemoryPersistenceService(provider.Adapter, _changeNotifier);
            await core.InitializeAsync(System.Text.Json.JsonSerializerOptions.Default, ct);
            if (_stores.TryAdd(ns, core))
            {
                _queryProviders[ns] =
                    new Query.InMemoryMeshQuery(core, changeNotifier: _changeNotifier);
            }
        }

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

    public IObservable<MeshNode> SaveNode(MeshNode node, JsonSerializerOptions options)
    {
        var segment = ResolvePartitionKey(node.Path)
            ?? PathPartition.GetFirstSegment(node.Path)
            ?? throw new ArgumentException($"Cannot save node: no partition found for path '{node.Path}'");

        return GetOrCreateStore(segment)
            .SelectMany(store => store.SaveNode(node, options))
            .SelectMany(saved =>
                node.Content is PartitionDefinition def && !string.IsNullOrEmpty(def.Namespace)
                    ? EnsurePartitionSchema(def).Select(_ => saved)
                    : Observable.Return(saved));
    }

    /// <summary>
    /// IObservable wrapper around <see cref="GetOrCreateStoreAsync"/>. Subscribers
    /// compose with <c>SelectMany</c> instead of bridging to Task — keeps the
    /// caller's reactive chain hot through the partition-resolution leg.
    /// </summary>
    private IObservable<IStorageService> GetOrCreateStore(string firstSegment) =>
        Observable.Defer(() =>
            _stores.TryGetValue(firstSegment, out var existing)
                ? Observable.Return(existing)
                : Observable.FromAsync(ct => GetOrCreateStoreAsync(firstSegment, ct), Scheduler.Default));

    /// <summary>
    /// Ensures the schema/tables exist for a partition definition. Returns
    /// IObservable so the caller composes via SelectMany; the only Task→Observable
    /// bridge is the leaf <see cref="IPartitionedStoreFactory"/> calls scheduled
    /// on TaskPool.
    /// </summary>
    private IObservable<Unit> EnsurePartitionSchema(PartitionDefinition def) =>
        Observable.Defer<Unit>(() =>
            Observable.FromAsync(
                ct => _factory.InitializeDefaultPartitionsAsync([def], ct),
                Scheduler.Default)
            .SelectMany(_ => _stores.ContainsKey(def.Namespace)
                ? Observable.Return(Unit.Default)
                : Observable.FromAsync(ct => _factory.CreateStoreAsync(def.Namespace, ct), Scheduler.Default)
                    .Select(partition =>
                    {
                        var core = new InMemoryPersistenceService(partition.StorageAdapter, _changeNotifier);
                        if (_stores.TryAdd(def.Namespace, core))
                        {
                            var queryProvider = partition.QueryProvider
                                ?? new Query.InMemoryMeshQuery(core, changeNotifier: _changeNotifier);
                            _queryProviders[def.Namespace] = queryProvider;
                            if (partition.VersionQuery != null)
                                _versionQueries[def.Namespace] = partition.VersionQuery;
                        }
                        return Unit.Default;
                    })));

    public IObservable<string> DeleteNode(string path, bool recursive = false)
    {
        var segment = PathPartition.GetFirstSegment(path);
        if (segment == null)
            return Observable.Return(path);

        var store = TryGetStore(path);
        if (store == null)
            return Observable.Return(path);

        return store.DeleteNode(path, recursive);
    }

    public IObservable<MeshNode> MoveNode(string sourcePath, string targetPath, JsonSerializerOptions options)
    {
        var sourceSegment = ResolvePartitionKey(sourcePath)
            ?? PathPartition.GetFirstSegment(sourcePath)
            ?? throw new ArgumentException($"No partition found for source path '{sourcePath}'");
        var targetSegment = ResolvePartitionKey(targetPath)
            ?? PathPartition.GetFirstSegment(targetPath)
            ?? throw new ArgumentException($"No partition found for target path '{targetPath}'");

        if (string.Equals(sourceSegment, targetSegment, StringComparison.OrdinalIgnoreCase))
        {
            // Same partition: delegate directly.
            return GetOrCreateStore(sourceSegment)
                .SelectMany(store => store.MoveNode(sourcePath, targetPath, options));
        }

        // Cross-partition move: move root node + all descendants. Compose via SelectMany;
        // the only Task→Observable bridge is the leaf IAsyncEnumerable enumeration over
        // descendants, scheduled on TaskPool via Observable.Create.
        return GetOrCreateStore(sourceSegment)
            .SelectMany(sourceStore => GetOrCreateStore(targetSegment)
                .SelectMany(targetStore => sourceStore.GetNode(sourcePath, options)
                    .SelectMany(sourceNode => sourceNode is null
                        ? Observable.Throw<MeshNode>(
                            new InvalidOperationException($"Source node not found: {sourcePath}"))
                        : MoveCrossPartitionImpl(
                            sourcePath, targetPath, options,
                            sourceStore, targetStore, targetSegment, sourceNode))));
    }

    private IObservable<MeshNode> MoveCrossPartitionImpl(
        string sourcePath, string targetPath, JsonSerializerOptions options,
        IStorageService sourceStore, IStorageService targetStore, string targetSegment,
        MeshNode sourceNode)
    {
        // Existence check on target — composed via the IObservable surface.
        return targetStore.Exists(targetPath)
            .SelectMany(targetExists => targetExists
                ? Observable.Throw<MeshNode>(
                    new InvalidOperationException($"Target path already exists: {targetPath}"))
                : Observable.FromAsync(
                        async ct =>
                        {
                            // Collect descendants — single async leaf into a list, no
                            // hub round-trips, runs on TaskPool.
                            var descendants = new List<MeshNode>();
                            await foreach (var desc in sourceStore.GetDescendantsAsync(sourcePath, options).WithCancellation(ct))
                                descendants.Add(desc);
                            return descendants;
                        }, Scheduler.Default)
                    .SelectMany(descendants => MoveDescendantsAndRoot(
                        sourcePath, targetPath, options,
                        sourceStore, targetStore, targetSegment,
                        sourceNode, descendants)));
    }

    private IObservable<MeshNode> MoveDescendantsAndRoot(
        string sourcePath, string targetPath, JsonSerializerOptions options,
        IStorageService sourceStore, IStorageService targetStore, string targetSegment,
        MeshNode sourceNode, List<MeshNode> descendants)
    {
        // Save each descendant in sequence (preserves prior semantics).
        // Concat ensures sequential subscription; each store.SaveNode is a cold
        // IObservable that doesn't fire until subscribed.
        var descSaves = descendants.Select(descendant =>
        {
            var newDescPath = targetPath + descendant.Path[sourcePath.Length..];
            var descTargetSeg = ResolvePartitionKey(newDescPath) ?? PathPartition.GetFirstSegment(newDescPath);
            var descStoreObs = descTargetSeg != null && !string.Equals(descTargetSeg, targetSegment, StringComparison.OrdinalIgnoreCase)
                ? GetOrCreateStore(descTargetSeg)
                : Observable.Return(targetStore);

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

            return descStoreObs.SelectMany(s => s.SaveNode(movedDesc, options));
        });

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

        // Run all descendant saves, then the root save, then the source delete.
        return (descSaves.Any()
                ? descSaves.Aggregate((a, b) => a.IgnoreElements().Concat(b))
                : Observable.Empty<MeshNode>())
            .IgnoreElements()
            .Concat(targetStore.SaveNode(movedNode, options))
            .SelectMany(saved => sourceStore.DeleteNode(sourcePath, recursive: true)
                .Select(_ => saved));
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

    public IObservable<bool> Exists(string path) =>
        Observable.FromAsync(ct => EnsureInitializedAsync(ct), Scheduler.Default)
            .SelectMany(_ =>
            {
                var store = TryGetStore(path);
                return store == null
                    ? Observable.Return(false)
                    : store.Exists(path);
            });

    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options) =>
        PathPartition.GetFirstSegment(fullPath) is null
            ? Observable.Return<(MeshNode?, int)>((null, 0))
            : Observable.FromAsync(ct => EnsureInitializedAsync(ct), Scheduler.Default)
                .SelectMany(_ =>
                {
                    var store = TryGetStore(fullPath);
                    return store == null
                        ? Observable.Return<(MeshNode?, int)>((null, 0))
                        : store.FindBestPrefixMatch(fullPath, options);
                });

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

    public IObservable<Comment> AddComment(Comment comment, JsonSerializerOptions options) =>
        Observable.Defer(() =>
        {
            var store = TryGetStore(comment.PrimaryNodePath)
                ?? throw new ArgumentException($"No partition found for comment path '{comment.PrimaryNodePath}'");
            return store.AddComment(comment, options);
        });

    public IObservable<string> DeleteComment(string commentId) =>
        // Fan out to all partitions since we don't know which one has the comment.
        // Concat sequentially subscribes to each — none of them touch a hub.
        Observable.Defer(() =>
            _stores.Values
                .Select(store => store.DeleteComment(commentId))
                .Aggregate(
                    Observable.Empty<string>().AsObservable(),
                    (acc, next) => acc.IgnoreElements().Concat(next))
                .DefaultIfEmpty(commentId));

    public IObservable<Comment?> GetComment(string commentId) =>
        // Fan out to all partitions sequentially; emit the first non-null match.
        // Concat preserves cold/sequential semantics — no Task bridges between hops.
        _stores.Values
            .Select(store => store.GetComment(commentId))
            .Aggregate(
                (IObservable<Comment?>)Observable.Return<Comment?>(null),
                (acc, next) => acc.SelectMany(found => found != null
                    ? Observable.Return(found)
                    : next));

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

    public IObservable<IReadOnlyCollection<object>> SavePartitionObjects(string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options) =>
        Observable.Defer(() =>
        {
            var store = TryGetStore(nodePath)
                ?? throw new ArgumentException($"No partition found for path '{nodePath}'");
            return store.SavePartitionObjects(nodePath, subPath, objects, options);
        });

    public IObservable<string> DeletePartitionObjects(string nodePath, string? subPath = null) =>
        Observable.Defer(() =>
        {
            var store = TryGetStore(nodePath);
            return store == null
                ? Observable.Return(subPath ?? nodePath)
                : store.DeletePartitionObjects(nodePath, subPath);
        });

    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
    {
        var store = TryGetStore(nodePath);
        return store == null
            ? Observable.Return<DateTimeOffset?>(null)
            : store.GetPartitionMaxTimestamp(nodePath, subPath);
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
