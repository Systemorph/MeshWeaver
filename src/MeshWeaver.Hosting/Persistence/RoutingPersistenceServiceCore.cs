using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
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
/// pedestrian backends — gets a <see cref="Query.MeshQueryEngine"/> instance bound
/// to the adapter.</para>
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
                _queryProviders[ns] =
                    new Query.MeshQueryEngine(persistence: null!, changeNotifier: _changeNotifier);
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
    /// Ensures partitions have been discovered at least once.
    /// </summary>
    public async Task EnsureInitializedAsync(CancellationToken ct = default)
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
                            ?? new Query.MeshQueryEngine(persistence: null!, changeNotifier: _changeNotifier);
                        _queryProviders[segment] = queryProvider;
                        if (partition.VersionQuery != null)
                            _versionQueries[segment] = partition.VersionQuery;
                        return (segment, queryProvider);
                    }))
            .Where(t => t.HasValue)
            .Select(t => t!.Value);
    }

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

    private IObservable<IStorageAdapter> GetOrCreateAdapter(string firstSegment) =>
        Observable.Defer(() =>
            _adapters.TryGetValue(firstSegment, out var existing)
                ? Observable.Return(existing)
                : Observable.FromAsync(ct => GetOrCreateAdapterAsync(firstSegment, ct), Scheduler.Default));

    private async Task<IStorageAdapter> GetOrCreateAdapterAsync(string firstSegment, CancellationToken ct)
    {
        if (_adapters.TryGetValue(firstSegment, out var existing)) return existing;

        await _provisionLock.WaitAsync(ct);
        try
        {
            if (_adapters.TryGetValue(firstSegment, out existing)) return existing;

            foreach (var provider in _partitionStorageProviders)
            {
                if (!provider.Matches(firstSegment)) continue;
                _adapters[firstSegment] = provider.Adapter;
                _queryProviders[firstSegment] =
                    new Query.MeshQueryEngine(persistence: null!, changeNotifier: _changeNotifier);
                return provider.Adapter;
            }

            var partition = await _factory.CreateStoreAsync(firstSegment, ct);
            _adapters[firstSegment] = partition.StorageAdapter!;
            var queryProvider = partition.QueryProvider
                ?? new Query.MeshQueryEngine(persistence: null!, changeNotifier: _changeNotifier);
            _queryProviders[firstSegment] = queryProvider;
            if (partition.VersionQuery != null)
                _versionQueries[firstSegment] = partition.VersionQuery;
            return partition.StorageAdapter!;
        }
        finally
        {
            _provisionLock.Release();
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        foreach (var provider in _partitionStorageProviders)
        {
            var ns = provider.PartitionDefinition?.Namespace;
            if (string.IsNullOrEmpty(ns)) continue;
            if (_adapters.ContainsKey(ns)) continue;

            if (_adapters.TryAdd(ns, provider.Adapter))
            {
                _queryProviders[ns] =
                    new Query.MeshQueryEngine(persistence: null!, changeNotifier: _changeNotifier);
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
        if (writableDefs.Count > 0)
            await _factory.InitializeDefaultPartitionsAsync(writableDefs, ct);

        await foreach (var (_, _) in DiscoverNewProvidersAsync(ct))
        { }

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
                _queryProviders[def.Namespace] = new Query.MeshQueryEngine(persistence: null!, changeNotifier: _changeNotifier);
            }
        }
    }

    private static string NormalizePath(string? path) => path?.Trim('/') ?? "";

    // ── IStorageAdapter — routes every operation to the per-partition adapter. ──

    /// <inheritdoc />
    public IObservable<MeshNode?> Read(string path, System.Text.Json.JsonSerializerOptions options)
        => Observable.FromAsync(ct => EnsureInitializedAsync(ct), Scheduler.Default)
            .SelectMany(_ => TryGetAdapter(path)?.Read(path, options) ?? Observable.Return<MeshNode?>(null));

    /// <inheritdoc />
    public IObservable<MeshNode> Write(MeshNode node, System.Text.Json.JsonSerializerOptions options)
        => Save(node, options);

    /// <inheritdoc />
    IObservable<string> IStorageAdapter.Delete(string path)
        => Delete(path);

    /// <inheritdoc />
    public IObservable<bool> Exists(string path)
        => Observable.FromAsync(ct => EnsureInitializedAsync(ct), Scheduler.Default)
            .SelectMany(_ => TryGetAdapter(path)?.Exists(path) ?? Observable.Return(false));

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, System.Text.Json.JsonSerializerOptions options)
        => TryGetAdapter(fullPath)?.FindBestPrefixMatch(fullPath, options)
            ?? Observable.Return<(MeshNode?, int)>((null, 0));

    /// <inheritdoc />
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
        => TryGetAdapter(parentPath)?.ListChildPaths(parentPath)
            ?? Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], []));

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
