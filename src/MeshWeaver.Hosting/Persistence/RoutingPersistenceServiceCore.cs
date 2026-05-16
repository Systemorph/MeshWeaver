using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Reduced-scope partition router: keeps a per-partition <see cref="IStorageAdapter"/>
/// dict + handles auto-provisioning and the <see cref="IMeshQueryProvider"/> registry.
///
/// <para>🚨 The router exposes ONLY <see cref="Save"/> + <see cref="Delete"/> + partition
/// discovery. Everything else (reads / enumeration / partition objects / comments /
/// security filtering) was deleted in the persistence cull (2026-05-11) — application
/// code now goes through <c>workspace.GetMeshNodeStream(path)</c> /
/// <c>workspace.GetQuery(id, queries…)</c> (per
/// <c>Doc/Architecture/CqrsAndContentAccess.md</c>). Per-node hubs hold their own
/// <see cref="IStorageAdapter"/> reference for content access.</para>
///
/// <para>The <see cref="QueryProviders"/> dict is the fan-out target for
/// <see cref="RoutingMeshQueryProvider"/>. Each partition either supplies its own
/// (Postgres native push-down, static-node provider) or — for adapter-only
/// pedestrian backends — gets a <see cref="Query.StorageAdapterMeshQueryProvider"/>
/// instance bound to the adapter.</para>
///
/// <para>API: <see cref="IObservable{T}"/> end-to-end (no <c>Task&lt;T&gt;</c>,
/// no <c>.ToTask()</c>). Composes with <c>SelectMany</c>/<c>Subscribe</c>.</para>
/// </summary>
internal class RoutingPersistenceServiceCore : IStorageAdapter
{
    private readonly IPartitionedStoreFactory _factory;
    private readonly IDataChangeNotifier? _changeNotifier;
    private readonly IEnumerable<IStaticNodeProvider> _staticNodeProviders;
    private readonly IEnumerable<IPartitionStorageProvider> _partitionStorageProviders;
    private readonly ConcurrentDictionary<string, IStorageAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IMeshQueryProvider> _queryProviders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IVersionQuery> _versionQueries = new(StringComparer.OrdinalIgnoreCase);

    // Reactive single-flight gates — replace the prior SemaphoreSlim that
    // covered BOTH global init and per-segment provisioning. Lazy<> with
    // ExecutionAndPublication ensures the value factory runs exactly once;
    // the AsyncSubject returned caches the eventual completion so every
    // late subscriber gets the result immediately. No await, no Task in the
    // hot path — boundary Tasks (factory.CreateStoreAsync, factory.
    // InitializeDefaultPartitionsAsync) stay wrapped in Observable.FromAsync
    // per Doc/Architecture/AsynchronousCalls.md.
    private Lazy<IObservable<Unit>>? _initObservable;
    private readonly ConcurrentDictionary<string, Lazy<IObservable<IStorageAdapter>>> _provisionInFlight =
        new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _initialized;

    // Fires (key, provider) on EVERY query-provider registration — constructor
    // seed, background DiscoverNewProviders, or write-triggered
    // GetOrCreateAdapterAsync. RoutingMeshQueryProvider's fan-out rides this so
    // a synced query opened BEFORE its target partition exists picks up that
    // partition the moment it is provisioned, instead of staying frozen on the
    // static provider snapshot captured at subscribe time. Replay so a
    // subscriber that subscribes-then-reconciles can't miss a registration.
    private readonly System.Reactive.Subjects.Subject<(string Key, IMeshQueryProvider Provider)> _providerAdded = new();

    /// <summary>
    /// Hot stream of query-provider registrations — one event per partition as
    /// it is provisioned. Consumed by <see cref="RoutingMeshQueryProvider"/> to
    /// keep an in-flight fan-out current with partitions created at runtime.
    /// </summary>
    internal IObservable<(string Key, IMeshQueryProvider Provider)> ProvidersAdded => _providerAdded;

    /// <summary>
    /// Single funnel for every <c>_queryProviders[…] = …</c> assignment so no
    /// registration is silent — sets the dict entry then publishes on
    /// <see cref="_providerAdded"/>.
    /// </summary>
    private void RegisterQueryProvider(string key, IMeshQueryProvider provider)
    {
        _queryProviders[key] = provider;
        _providerAdded.OnNext((key, provider));
    }

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

        // Lazy ensures the AsyncSubject pipeline (drives Initialize()) is
        // built exactly once even under concurrent EnsureInitialized() calls.
        _initObservable = new Lazy<IObservable<Unit>>(
            BuildInitObservable,
            LazyThreadSafetyMode.ExecutionAndPublication);

        // Eager registration for IPartitionStorageProvider rules with a fixed
        // PartitionDefinition.Namespace (single-namespace rules like EmbeddedResource).
        // Wildcard / pattern rules are exercised lazily by GetOrCreateAdapterAsync
        // on first first-segment access.
        foreach (var provider in _partitionStorageProviders)
        {
            var ns = provider.PartitionDefinition?.Namespace;
            if (string.IsNullOrEmpty(ns)) continue;
            if (_adapters.ContainsKey(ns)) continue;

            if (_adapters.TryAdd(ns, provider.Adapter))
            {
                RegisterQueryProvider(ns,
                    new Query.StorageAdapterMeshQueryProvider(persistence: provider.Adapter, changeNotifier: _changeNotifier));
            }
        }
    }

    internal IReadOnlyDictionary<string, IMeshQueryProvider> QueryProviders => _queryProviders;
    internal IReadOnlyDictionary<string, IVersionQuery> VersionQueries => _versionQueries;
    internal IEnumerable<string> PartitionNames => _adapters.Keys;

    /// <summary>
    /// Resolves the partition key for a path via longest-prefix match against the
    /// registered partitions, then returns the matching <see cref="IStorageAdapter"/>.
    /// Returns null if no partition matches (caller decides what to do).
    /// </summary>
    internal IStorageAdapter? TryGetAdapter(string? path)
    {
        var key = ResolvePartitionKey(path);
        return key != null && _adapters.TryGetValue(key, out var adapter) ? adapter : null;
    }

    private string? ResolvePartitionKey(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var test = path;
        while (true)
        {
            if (_adapters.ContainsKey(test)) return test;
            var lastSlash = test.LastIndexOf('/');
            if (lastSlash < 0) break;
            test = test[..lastSlash];
        }
        return null;
    }

    /// <summary>
    /// Saves a node by routing to the adapter for the node's partition. Auto-provisions
    /// the partition if it doesn't yet exist. Publishes a change notification on success.
    /// </summary>
    public IObservable<MeshNode> Save(MeshNode node, JsonSerializerOptions options)
    {
        var segment = ResolvePartitionKey(node.Path)
            ?? PathPartition.GetFirstSegment(node.Path)
            ?? throw new ArgumentException($"Cannot save node: no partition found for path '{node.Path}'");

        var savedNode = node with
        {
            LastModified = node.LastModified == default ? DateTimeOffset.UtcNow : node.LastModified
        };

        return GetOrCreateAdapter(segment)
            .SelectMany(adapter => adapter.Write(savedNode, options))
            .Select(_ => savedNode)
            .Do(saved => _changeNotifier?.NotifyChange(
                DataChangeNotification.Updated(NormalizePath(saved.Path), saved)));
    }

    /// <summary>
    /// Deletes a node by routing to the adapter for the node's partition.
    /// Publishes a change notification on success.
    /// </summary>
    public IObservable<string> Delete(string path)
    {
        var adapter = TryGetAdapter(path);
        if (adapter == null)
            return Observable.Return(path);

        var normalized = NormalizePath(path);
        return adapter.Delete(path)
            .Select(_ => path)
            .Do(_ => _changeNotifier?.NotifyChange(
                DataChangeNotification.Deleted(normalized, null)));
    }

    /// <summary>
    /// Reactive init gate — single-flight via <see cref="Lazy{T}"/>. Every
    /// downstream observable (<see cref="Read"/>, <see cref="Exists"/>,
    /// <see cref="ListChildPaths"/>) flat-maps through here so the first
    /// caller drives <see cref="Initialize"/> and concurrent callers
    /// subscribe to the same AsyncSubject.
    /// </summary>
    internal IObservable<Unit> EnsureInitialized() =>
        _initialized
            ? Observable.Return(Unit.Default)
            : _initObservable!.Value;

    /// <summary>
    /// Task adapter for boundary callers (Aspire bootstrap, tests). Internal
    /// code MUST use the observable form to stay inside the reactive surface.
    /// </summary>
    public Task EnsureInitializedAsync(CancellationToken ct = default) =>
        EnsureInitialized().ToTask(ct);

    private IObservable<Unit> BuildInitObservable()
    {
        var subject = new AsyncSubject<Unit>();
        Initialize().Subscribe(
            _ => { },
            ex =>
            {
                // Allow a retry — wipe the Lazy so the next caller rebuilds.
                _initObservable = new Lazy<IObservable<Unit>>(
                    BuildInitObservable, LazyThreadSafetyMode.ExecutionAndPublication);
                subject.OnError(ex);
            },
            () =>
            {
                _initialized = true;
                subject.OnNext(Unit.Default);
                subject.OnCompleted();
            });
        return subject;
    }

    internal IObservable<(string Key, IMeshQueryProvider Provider)> DiscoverNewProviders(CancellationToken ct = default)
    {
        return Observable
            .FromAsync(token => _factory.DiscoverPartitionsAsync(token), Scheduler.Default)
            .Timeout(TimeSpan.FromSeconds(30))
            .SelectMany(partitions => partitions.ToObservable())
            .Where(segment => !_adapters.ContainsKey(segment))
            .SelectMany(segment =>
                Observable.FromAsync(token => _factory.CreateStoreAsync(segment, token), Scheduler.Default)
                    .Select(partition =>
                    {
                        if (!_adapters.TryAdd(segment, partition.StorageAdapter!))
                            return ((string, IMeshQueryProvider)?)null;
                        var queryProvider = partition.QueryProvider
                            ?? new Query.StorageAdapterMeshQueryProvider(persistence: partition.StorageAdapter!, changeNotifier: _changeNotifier);
                        RegisterQueryProvider(segment, queryProvider);
                        if (partition.VersionQuery != null)
                            _versionQueries[segment] = partition.VersionQuery;
                        return (segment, queryProvider);
                    }))
            .Where(t => t.HasValue)
            .Select(t => t!.Value);
    }

    /// <summary>
    /// async-IAsyncEnumerable adapter kept ONLY because external callers
    /// (<c>RoutingMeshQueryProvider</c>, <c>UserAccessiblePartitionsCache</c>)
    /// drive partition discovery via <c>await foreach</c>. Internal init uses
    /// <see cref="DiscoverNewProviders"/> directly.
    /// </summary>
    internal async IAsyncEnumerable<(string Key, IMeshQueryProvider Provider)> DiscoverNewProvidersAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
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

    /// <summary>
    /// Reactive per-segment single-flight: dedup concurrent provisioning of
    /// the same partition key. Lazy ensures the value factory runs once even
    /// under contention; AsyncSubject (via Replay(1).RefCount()) caches the
    /// terminal value so late subscribers don't re-trigger the factory call.
    /// </summary>
    private IObservable<IStorageAdapter> GetOrCreateAdapter(string firstSegment) =>
        Observable.Defer(() =>
        {
            if (_adapters.TryGetValue(firstSegment, out var existing))
                return Observable.Return(existing);

            var lazy = _provisionInFlight.GetOrAdd(firstSegment, key =>
                new Lazy<IObservable<IStorageAdapter>>(
                    () => ProvisionAdapter(key)
                        .Do(_ => _provisionInFlight.TryRemove(key, out Lazy<IObservable<IStorageAdapter>>? _))
                        .Catch<IStorageAdapter, Exception>(ex =>
                        {
                            _provisionInFlight.TryRemove(key, out Lazy<IObservable<IStorageAdapter>>? _);
                            return Observable.Throw<IStorageAdapter>(ex);
                        })
                        .Replay(1)
                        .RefCount(),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            return lazy.Value;
        });

    private IObservable<IStorageAdapter> ProvisionAdapter(string firstSegment) =>
        Observable.Defer(() =>
        {
            // Re-check after winning the single-flight slot.
            if (_adapters.TryGetValue(firstSegment, out var existing))
                return Observable.Return(existing);

            // Try IPartitionStorageProvider rules first (wildcard / pattern).
            foreach (var provider in _partitionStorageProviders)
            {
                if (!provider.Matches(firstSegment)) continue;
                _adapters[firstSegment] = provider.Adapter;
                RegisterQueryProvider(firstSegment,
                    new Query.StorageAdapterMeshQueryProvider(
                        persistence: provider.Adapter, changeNotifier: _changeNotifier));
                return Observable.Return(provider.Adapter);
            }

            // Fall through to factory.CreateStoreAsync (boundary Task → FromAsync).
            return Observable
                .FromAsync(ct => _factory.CreateStoreAsync(firstSegment, ct), Scheduler.Default)
                .Select(partition =>
                {
                    _adapters[firstSegment] = partition.StorageAdapter!;
                    var queryProvider = partition.QueryProvider
                        ?? new Query.StorageAdapterMeshQueryProvider(
                            persistence: partition.StorageAdapter!, changeNotifier: _changeNotifier);
                    RegisterQueryProvider(firstSegment, queryProvider);
                    if (partition.VersionQuery != null)
                        _versionQueries[firstSegment] = partition.VersionQuery;
                    return partition.StorageAdapter!;
                });
        });

    /// <summary>
    /// Reactive init: registers eager partition providers, initialises
    /// defaults, drains <see cref="DiscoverNewProviders"/>, then registers
    /// static-data adapters for static partitions. Composed via
    /// <c>SelectMany</c>; the only awaits are at the IPartitionedStoreFactory
    /// boundary (<see cref="Observable.FromAsync{T}(Func{Task{T}})"/>).
    /// </summary>
    public IObservable<Unit> Initialize() =>
        Observable.Defer(() =>
        {
            foreach (var provider in _partitionStorageProviders)
            {
                var ns = provider.PartitionDefinition?.Namespace;
                if (string.IsNullOrEmpty(ns)) continue;
                if (_adapters.ContainsKey(ns)) continue;

                if (_adapters.TryAdd(ns, provider.Adapter))
                {
                    RegisterQueryProvider(ns,
                        new Query.StorageAdapterMeshQueryProvider(
                            persistence: provider.Adapter, changeNotifier: _changeNotifier));
                }
            }

            var allStaticNodes = _staticNodeProviders
                .SelectMany(p => p.GetStaticNodes())
                .ToList();

            var allPartitionDefs = allStaticNodes
                .Select(n => n.Content)
                .OfType<PartitionDefinition>()
                .ToList();

            var writableDefs = allPartitionDefs
                .Where(d => !string.Equals(d.DataSource, "static", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var initDefaults = writableDefs.Count > 0
                ? Observable.FromAsync(
                    ct => _factory.InitializeDefaultPartitionsAsync(writableDefs, ct),
                    Scheduler.Default)
                : Observable.Return(Unit.Default);

            // DiscoverNewProviders terminates once it has emitted every newly-
            // provisioned partition; .LastOrDefaultAsync waits for OnCompleted
            // and projects to a terminal Unit. Falls back to default if the
            // stream completes with zero emissions (no new partitions found).
            return initDefaults
                .SelectMany(_ => DiscoverNewProviders(CancellationToken.None)
                    .LastOrDefaultAsync()
                    .Select(_ => Unit.Default)
                    .DefaultIfEmpty(Unit.Default))
                .Select(_ =>
                {
                    foreach (var def in allPartitionDefs)
                    {
                        if (!string.Equals(def.DataSource, "static", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (string.IsNullOrEmpty(def.Namespace)) continue;
                        if (_adapters.ContainsKey(def.Namespace)) continue;

                        var nodesInPartition = allStaticNodes
                            .Where(n => string.Equals(
                                PathPartition.GetFirstSegment(n.Path),
                                def.Namespace,
                                StringComparison.OrdinalIgnoreCase));
                        var staticAdapter = new StaticNodeStorageAdapter(nodesInPartition);
                        if (_adapters.TryAdd(def.Namespace, staticAdapter))
                        {
                            RegisterQueryProvider(def.Namespace,
                                new Query.StorageAdapterMeshQueryProvider(
                                    persistence: staticAdapter, changeNotifier: _changeNotifier));
                        }
                    }
                    return Unit.Default;
                });
        });

    /// <summary>
    /// Task adapter for boundary callers (tests). Internal init paths use
    /// <see cref="Initialize"/> directly.
    /// </summary>
    public Task InitializeAsync(CancellationToken ct = default) =>
        Initialize().ToTask(ct);

    private static string NormalizePath(string? path) => path?.Trim('/') ?? "";

    // ── IStorageAdapter — routes every operation to the per-partition adapter. ──

    /// <inheritdoc />
    public IObservable<MeshNode?> Read(string path, System.Text.Json.JsonSerializerOptions options)
        => EnsureInitialized()
            .SelectMany(_ => TryGetAdapter(path)?.Read(path, options) ?? Observable.Return<MeshNode?>(null));

    /// <inheritdoc />
    public IObservable<MeshNode> Write(MeshNode node, System.Text.Json.JsonSerializerOptions options)
        => Save(node, options);

    /// <inheritdoc />
    IObservable<string> IStorageAdapter.Delete(string path)
        => Delete(path);

    /// <inheritdoc />
    public IObservable<bool> Exists(string path)
        => EnsureInitialized()
            .SelectMany(_ => TryGetAdapter(path)?.Exists(path) ?? Observable.Return(false));

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, System.Text.Json.JsonSerializerOptions options)
        => TryGetAdapter(fullPath)?.FindBestPrefixMatch(fullPath, options)
            ?? Observable.Return<(MeshNode?, int)>((null, 0));

    /// <inheritdoc />
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
    {
        // Root-level (empty/null) — fan out across every registered partition's
        // root entry. Returns each partition's namespace key as a "node path"
        // so consumers see the per-partition root nodes (ACME, Contoso, …).
        // The adapter dictionary populates lazily; ensure init before snapshotting
        // the keys, otherwise the engine's children/descendants walk sees an empty
        // root and the entire mesh-wide query returns nothing.
        if (string.IsNullOrEmpty(parentPath))
        {
            return EnsureInitialized()
                .Select(_ =>
                {
                    var nodes = _adapters.Keys.ToList();
                    return ((IEnumerable<string>)nodes, (IEnumerable<string>)Array.Empty<string>());
                });
        }

        return EnsureInitialized()
            .SelectMany(_ => TryGetAdapter(parentPath)?.ListChildPaths(parentPath)
                ?? Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], [])));
    }

    /// <inheritdoc />
    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => TryGetAdapter(nodePath)?.ListPartitionSubPaths(nodePath)
            ?? Observable.Return(Enumerable.Empty<string>());

    /// <inheritdoc />
    public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, System.Text.Json.JsonSerializerOptions options)
        => TryGetAdapter(nodePath)?.GetPartitionObjects(nodePath, subPath, options)
            ?? Observable.Empty<object>();

    /// <inheritdoc />
    public IObservable<Unit> SavePartitionObjects(string nodePath, string? subPath, IReadOnlyCollection<object> objects, System.Text.Json.JsonSerializerOptions options)
        => TryGetAdapter(nodePath)?.SavePartitionObjects(nodePath, subPath, objects, options)
            ?? Observable.Return(Unit.Default);

    /// <inheritdoc />
    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => TryGetAdapter(nodePath)?.DeletePartitionObjects(nodePath, subPath)
            ?? Observable.Return(Unit.Default);

    /// <inheritdoc />
    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => TryGetAdapter(nodePath)?.GetPartitionMaxTimestamp(nodePath, subPath)
            ?? Observable.Return<DateTimeOffset?>(null);
}
