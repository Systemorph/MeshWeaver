using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Pure-delegation <see cref="IStorageAdapter"/> facade. Iterates the
/// registered <see cref="IPartitionStorageProvider"/>s on every call,
/// picks the first one whose <see cref="IPartitionStorageProvider.Matches"/>
/// returns true for the target path, and forwards every operation to that
/// provider's <see cref="IPartitionStorageProvider.Adapter"/>.
///
/// <para>No internal cache, no init phase, no factory, no change-notifier
/// wrapping. Each adapter is the sole source of truth for its own paths;
/// any connection-reuse caching lives inside the backend's provider (e.g.
/// Postgres caches <c>NpgsqlDataSource</c> per schema internally). Change
/// notifications are fired by the backing adapters' own Write/Delete
/// implementations.</para>
///
/// <para>Replaces <c>RoutingPersistenceServiceCore</c> + its factory
/// infrastructure as the <see cref="IStorageAdapter"/> singleton.</para>
/// </summary>
public sealed class PersistenceService : IStorageAdapter
{
    private readonly IReadOnlyList<IPartitionStorageProvider> _providersSpecific;
    private readonly IReadOnlyList<IPartitionStorageProvider> _providersWildcard;

    public PersistenceService(IEnumerable<IPartitionStorageProvider> providers)
    {
        // Split providers into "specific" (own a fixed PartitionDefinition.Namespace —
        // e.g. EmbeddedResource("Doc"), InMemoryPartitionStorageProvider scoped to
        // /Release/) and "wildcard" (no fixed namespace — Postgres + InMemory
        // catch-alls that lazily mint per-first-segment definitions in
        // ResolveDefinition). Specific providers iterate first so a /Doc/...
        // path lands on the EmbeddedResource adapter even when the wildcard
        // InMemory provider was registered earlier in DI order. Without this
        // priority, AddPartitionedInMemoryPersistence + AddDocumentation in
        // either order pinned /Doc/... to the empty in-memory store and broke
        // CessionLayoutAreaTest.MotorXL_PathResolves.
        var all = providers.ToList();
        _providersSpecific = all
            .Where(p => p.PartitionDefinition != null
                        && !string.IsNullOrEmpty(p.PartitionDefinition.Namespace))
            .ToList();
        _providersWildcard = all
            .Where(p => p.PartitionDefinition == null
                        || string.IsNullOrEmpty(p.PartitionDefinition.Namespace))
            .ToList();
    }

    private IStorageAdapter? Resolve(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var p in _providersSpecific)
            if (p.Matches(path))
                return p.Adapter;
        foreach (var p in _providersWildcard)
            if (p.Matches(path))
                return p.Adapter;
        return null;
    }

    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => Resolve(path)?.Read(path, options) ?? Observable.Return<MeshNode?>(null);

    public IObservable<MeshNode> Write(MeshNode node, JsonSerializerOptions options)
        => Resolve(node.Path)?.Write(node, options)
            ?? Observable.Throw<MeshNode>(new InvalidOperationException(
                $"Cannot write '{node.Path}': no IPartitionStorageProvider matches."));

    public IObservable<string> Delete(string path)
        => Resolve(path)?.Delete(path) ?? Observable.Return(path);

    public IObservable<bool> Exists(string path)
        => Resolve(path)?.Exists(path) ?? Observable.Return(false);

    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => Resolve(fullPath)?.FindBestPrefixMatch(fullPath, options)
            ?? Observable.Return<(MeshNode?, int)>((null, 0));

    /// <summary>
    /// Forwards to the matching provider's <see cref="IStorageAdapter.ResolvePath"/>
    /// so backends that override it (e.g. Postgres satellite-UNION) keep their
    /// stronger contract. Without this forward, the interface default routes
    /// through <c>this.FindBestPrefixMatch</c> which selects only the primary
    /// table — satellite-only matches (e.g. <c>rbuergi/_Activity/&lt;id&gt;</c>)
    /// collapse to <c>(null, 0)</c>.
    /// </summary>
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => Resolve(fullPath)?.ResolvePath(fullPath, options)
            ?? Observable.Return<(MeshNode?, int)>((null, 0));

    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
        ListChildPaths(string? parentPath)
        => string.IsNullOrEmpty(parentPath)
            ? AggregateRootListings()
            : Resolve(parentPath)?.ListChildPaths(parentPath)
                ?? Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], []));

    /// <summary>
    /// Root-level listing fan-out: each <see cref="IPartitionStorageProvider"/>
    /// knows its own top-level entries (file-system directories, in-memory
    /// partition keys, …). Iterate every provider, ask its adapter for
    /// <c>ListChildPaths(null)</c>, and union the results. Per-provider
    /// failures (e.g. a Postgres routing adapter that refuses root listing
    /// by design) are swallowed so one backend can't blank the whole walk —
    /// every other provider's roots still flow through.
    ///
    /// <para>Without this, <see cref="Query.StorageAdapterMeshQueryProvider"/>'s
    /// <c>WalkLevel(null)</c> ends up with zero discoverable top-level
    /// partitions and autocomplete / scope:subtree queries return empty
    /// (repro: <c>AutocompleteIconTests.Autocomplete_ReturnsIconForACME</c>).</para>
    /// </summary>
    private IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
        AggregateRootListings()
        => _providersSpecific.Concat(_providersWildcard)
            .ToObservable()
            .SelectMany(p => p.Adapter.ListChildPaths(null)
                .Catch<(IEnumerable<string>, IEnumerable<string>), Exception>(_ =>
                    Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], []))))
            .Aggregate(
                seed: (Nodes: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                       Dirs: new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                accumulator: (acc, level) =>
                {
                    foreach (var n in level.Item1 ?? Enumerable.Empty<string>()) acc.Nodes.Add(n);
                    foreach (var d in level.Item2 ?? Enumerable.Empty<string>()) acc.Dirs.Add(d);
                    return acc;
                })
            .Select(acc => ((IEnumerable<string>)acc.Nodes, (IEnumerable<string>)acc.Dirs));

    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => Resolve(nodePath)?.ListPartitionSubPaths(nodePath)
            ?? Observable.Return(Enumerable.Empty<string>());

    public IObservable<object> GetPartitionObjects(
        string nodePath, string? subPath, JsonSerializerOptions options)
        => Resolve(nodePath)?.GetPartitionObjects(nodePath, subPath, options)
            ?? Observable.Empty<object>();

    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => Resolve(nodePath)?.SavePartitionObjects(nodePath, subPath, objects, options)
            ?? Observable.Return(Unit.Default);

    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => Resolve(nodePath)?.DeletePartitionObjects(nodePath, subPath)
            ?? Observable.Return(Unit.Default);

    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => Resolve(nodePath)?.GetPartitionMaxTimestamp(nodePath, subPath)
            ?? Observable.Return<DateTimeOffset?>(null);
}
