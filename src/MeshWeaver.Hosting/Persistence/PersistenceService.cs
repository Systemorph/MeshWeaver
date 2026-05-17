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
/// emits <c>true</c> for the target path, and forwards every operation to
/// that provider's <see cref="IPartitionStorageProvider.Adapter"/>.
///
/// <para><b>Observable routing.</b> Each provider's <see cref="IPartitionStorageProvider.Matches"/>
/// is an <see cref="IObservable{T}"/> backed by a <see cref="System.Reactive.Subjects.ReplaySubject{T}"/>
/// — Postgres's wildcard provider re-emits when the partition catalog
/// changes (organization creation, partition drop). Resolve composes the
/// per-provider predicates with <c>Concat</c> + <c>FirstAsync(true)</c>
/// so the routing path stays observable end-to-end: no <c>.Wait()</c>, no
/// blocking call on the grain scheduler.</para>
///
/// <para>Replaces <c>RoutingPersistenceServiceCore</c> + its factory
/// infrastructure as the <see cref="IStorageAdapter"/> singleton.</para>
/// </summary>
public sealed class PersistenceService : IStorageAdapter
{
    private readonly IReadOnlyList<IPartitionStorageProvider> _providersSpecific;
    private readonly IReadOnlyList<IPartitionStorageProvider> _providersWildcard;
    private readonly IReadOnlyList<IPartitionStorageProvider> _allOrdered;

    public PersistenceService(IEnumerable<IPartitionStorageProvider> providers)
    {
        // Split providers into "specific" (own a fixed PartitionDefinition.Namespace —
        // e.g. EmbeddedResource("Doc"), InMemoryPartitionStorageProvider scoped to
        // /Release/) and "wildcard" (no fixed namespace — Postgres + InMemory
        // catch-alls that lazily mint per-first-segment definitions in
        // ResolveDefinition). Specific providers iterate first so a /Doc/...
        // path lands on the EmbeddedResource adapter even when the wildcard
        // InMemory provider was registered earlier in DI order.
        var all = providers.ToList();
        _providersSpecific = all
            .Where(p => p.PartitionDefinition != null
                        && !string.IsNullOrEmpty(p.PartitionDefinition.Namespace))
            .OrderByDescending(p => p.Priority)
            .ToList();
        // Wildcards: schema-aware (Postgres, Priority=100) ahead of catch-all
        // (InMemory/FileSystem, Priority=0) so a Postgres-backed namespace
        // doesn't accidentally route to an empty in-memory adapter.
        _providersWildcard = all
            .Where(p => p.PartitionDefinition == null
                        || string.IsNullOrEmpty(p.PartitionDefinition.Namespace))
            .OrderByDescending(p => p.Priority)
            .ToList();
        _allOrdered = _providersSpecific.Concat(_providersWildcard).ToList();
    }

    /// <summary>
    /// Emits the first <see cref="IStorageAdapter"/> whose owning provider's
    /// <see cref="IPartitionStorageProvider.Matches"/> reports <c>true</c>
    /// for <paramref name="path"/>. Concat-then-FirstOrDefault preserves
    /// registration order — specific providers first, wildcards last — and
    /// each per-provider <c>Take(1)</c> bounds the subscription to a single
    /// reading so a never-emitting subject can't strand the routing call.
    /// </summary>
    private IObservable<IStorageAdapter?> Resolve(string? path)
    {
        if (string.IsNullOrEmpty(path)) return Observable.Return<IStorageAdapter?>(null);

        return _allOrdered
            .Select(p => p.Matches(path).Take(1).Select(match => match ? p.Adapter : null))
            .Concat()
            .Where(a => a is not null)
            .FirstOrDefaultAsync()
            .Select(a => a!);
    }

    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => Resolve(path).SelectMany(a => a?.Read(path, options) ?? Observable.Return<MeshNode?>(null));

    public IObservable<MeshNode> Write(MeshNode node, JsonSerializerOptions options)
        => Resolve(node.Path).SelectMany(a => a?.Write(node, options)
            ?? Observable.Throw<MeshNode>(new InvalidOperationException(
                $"Cannot write '{node.Path}': no IPartitionStorageProvider matches.")));

    public IObservable<string> Delete(string path)
        => Resolve(path).SelectMany(a => a?.Delete(path) ?? Observable.Return(path));

    public IObservable<bool> Exists(string path)
        => Resolve(path).SelectMany(a => a?.Exists(path) ?? Observable.Return(false));

    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => Resolve(fullPath).SelectMany(a => a?.FindBestPrefixMatch(fullPath, options)
            ?? Observable.Return<(MeshNode?, int)>((null, 0)));

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
        => Resolve(fullPath).SelectMany(a => a?.ResolvePath(fullPath, options)
            ?? Observable.Return<(MeshNode?, int)>((null, 0)));

    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
        ListChildPaths(string? parentPath)
        => string.IsNullOrEmpty(parentPath)
            ? AggregateRootListings()
            : Resolve(parentPath).SelectMany(a => a?.ListChildPaths(parentPath)
                ?? Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], [])));

    /// <summary>
    /// Root-level listing fan-out: each <see cref="IPartitionStorageProvider"/>
    /// knows its own top-level entries (file-system directories, in-memory
    /// partition keys, …). Iterate every provider, ask its adapter for
    /// <c>ListChildPaths(null)</c>, and union the results. Per-provider
    /// failures (e.g. a Postgres routing adapter that refuses root listing
    /// by design) are swallowed so one backend can't blank the whole walk.
    /// </summary>
    private IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
        AggregateRootListings()
        => _allOrdered
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
        => Resolve(nodePath).SelectMany(a => a?.ListPartitionSubPaths(nodePath)
            ?? Observable.Return(Enumerable.Empty<string>()));

    public IObservable<object> GetPartitionObjects(
        string nodePath, string? subPath, JsonSerializerOptions options)
        => Resolve(nodePath).SelectMany(a => a?.GetPartitionObjects(nodePath, subPath, options)
            ?? Observable.Empty<object>());

    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => Resolve(nodePath).SelectMany(a => a?.SavePartitionObjects(nodePath, subPath, objects, options)
            ?? Observable.Return(Unit.Default));

    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => Resolve(nodePath).SelectMany(a => a?.DeletePartitionObjects(nodePath, subPath)
            ?? Observable.Return(Unit.Default));

    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => Resolve(nodePath).SelectMany(a => a?.GetPartitionMaxTimestamp(nodePath, subPath)
            ?? Observable.Return<DateTimeOffset?>(null));
}
