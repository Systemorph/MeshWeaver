using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// In-memory pedestrian <see cref="SimpleMeshNodeStorage"/> for non-persistent
/// partitions (test fixtures, the catch-all wildcard partition in samples).
/// Holds nodes in a path-keyed <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// that IS the storage of record — there is no separate persistence-service
/// cache on top.
/// </summary>
public sealed class InMemoryStorageAdapter : SimpleMeshNodeStorage, IStorageAdapter
{
    private readonly ConcurrentDictionary<string, MeshNode> _nodes;
    private readonly ConcurrentDictionary<string, List<object>> _partitionObjects;
    private readonly ILogger? _logger;
    private readonly Subject<DataChangeNotification> _changes = new();

    /// <inheritdoc />
    public IObservable<DataChangeNotification> Changes => _changes.AsObservable();

    /// <summary>
    /// Creates an adapter with its own fresh, case-insensitive backing
    /// dictionaries for nodes and partition objects.
    /// </summary>
    /// <param name="logger">Optional logger for read/write/lookup diagnostics.</param>
    public InMemoryStorageAdapter(
        ILogger<InMemoryStorageAdapter>? logger = null)
        : this(
            nodes: new(StringComparer.OrdinalIgnoreCase),
            partitionObjects: new(StringComparer.OrdinalIgnoreCase),
            logger)
    {
    }

    /// <summary>
    /// Backing-store-injected constructor — share the same dictionaries
    /// across multiple <see cref="InMemoryStorageAdapter"/> instances so a
    /// multi-host test cluster (Orleans silo + client in one process) sees
    /// one logical store, mirroring production where multiple adapter
    /// instances all point at the same PG backend.
    /// </summary>
    public InMemoryStorageAdapter(
        ConcurrentDictionary<string, MeshNode> nodes,
        ConcurrentDictionary<string, List<object>> partitionObjects,
        ILogger<InMemoryStorageAdapter>? logger = null)
    {
        _nodes = nodes;
        _partitionObjects = partitionObjects;
        _logger = logger;
    }

    private static string Norm(string? path) => path?.Trim('/') ?? "";

    /// <inheritdoc />
    public override IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            _nodes.TryGetValue(Norm(path), out var node);
            _logger?.LogDebug("[InMemoryAdapter#{Id:X}] Read {Path} → {Found}",
                GetHashCode(), Norm(path), node != null ? "hit" : "miss");
            return Observable.Return<MeshNode?>(node);
        });

    /// <inheritdoc />
    public override IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            // 🚨 A durable store cannot hold an in-process delegate. FileSystem/Postgres strip
            // MeshNode.HubConfiguration naturally at the serialization boundary; this adapter
            // stores the INSTANCE, so without this strip a workspace node that carries the
            // routing-grafted enrichment (or a compilation-error overlay wrapper) would be
            // retained in the store-of-record, later served back by reads/path-resolution, and
            // latch EnrichWithNodeType's "already enriched" short-circuit onto a STALE config —
            // an instance then re-binds an obsolete overlay on every re-activation instead of
            // re-enriching (the OverlaySelfHealInstanceRecycleTest probe loop). Stripping here
            // makes the in-memory adapter behave exactly like every serializing backend.
            if (node.HubConfiguration is not null)
                node = node with { HubConfiguration = null };
            if (string.IsNullOrEmpty(node.Path))
                return Observable.Return<MeshNode?>(node);

            // 🚨 VERSION-CONDITIONAL upsert — the in-memory twin of the Postgres
            // `ON CONFLICT … WHERE target.version <= EXCLUDED.version` gate (#971). A single
            // AddOrUpdate keeps whichever row carries the higher MeshNode.Version, so the decision is
            // atomic against every concurrent writer of the same path — no read-then-write window for
            // one to slip through. The dictionary IS the store of record here, so this is where the
            // "a node's durable state never moves backward" invariant has to live; the in-process
            // high-water filter above it is empty on a fresh replica and cannot be the guarantee.
            var winner = _nodes.AddOrUpdate(
                Norm(node.Path), node,
                (_, existing) => existing.Version > node.Version ? existing : node);
            if (!ReferenceEquals(winner, node))
            {
                // Refused: the stored row is newer. Emit it (never the loser) so the write-integrity
                // chain can merge into durable truth, and publish NOTHING — nothing changed, and a
                // notification carrying the losing node would hand every subscriber the stale state
                // the store just rejected.
                _logger?.LogDebug(
                    "[InMemoryAdapter#{Id:X}] Write {Path} REFUSED — incoming v{Incoming} is below stored v{Stored}",
                    GetHashCode(), Norm(node.Path), node.Version, winner.Version);
                return Observable.Return<MeshNode?>(winner);
            }

            _logger?.LogDebug("[InMemoryAdapter#{Id:X}] Write {Path} (count={Count})",
                GetHashCode(), Norm(node.Path), _nodes.Count);
            try { _changes.OnNext(DataChangeNotification.Updated(Norm(node.Path), node)); } catch { /* never throw */ }
            return Observable.Return<MeshNode?>(node);
        });

    /// <inheritdoc />
    public override IObservable<string> Delete(string path)
        => Observable.Defer(() =>
        {
            _nodes.TryRemove(Norm(path), out var removed);
            try { _changes.OnNext(DataChangeNotification.Deleted(Norm(path), removed)); } catch { /* never throw */ }
            return Observable.Return(path);
        });

    /// <inheritdoc />
    /// <remarks>
    /// Strict semantics via <see cref="ConcurrentDictionary{TKey,TValue}.TryRemove(TKey, out TValue)"/>:
    /// concurrent consumers of the same path get exactly one <c>true</c> between
    /// them — the in-memory twin of the Postgres DELETE-rowcount gate.
    /// </remarks>
    public IObservable<bool> DeleteIfExists(string path)
        => Observable.Defer(() =>
        {
            var won = _nodes.TryRemove(Norm(path), out var removed);
            if (won)
            {
                try { _changes.OnNext(DataChangeNotification.Deleted(Norm(path), removed)); } catch { /* never throw */ }
            }
            return Observable.Return(won);
        });

    /// <inheritdoc />
    public override IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
        ListChildPaths(string? parentPath)
        => Observable.Defer(() =>
        {
            var normalized = Norm(parentPath);
            var prefix = string.IsNullOrEmpty(normalized) ? "" : normalized + "/";
            var expectedDepth = string.IsNullOrEmpty(normalized)
                ? 1
                : normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Length + 1;

            var nodePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 🚨 DirectoryPaths must include any intermediate prefix that has at
            // least one descendant node — a stored node at depth N≥expectedDepth+1
            // implies a "directory" at the expectedDepth level even if no node
            // lives there (e.g. SaveNode("org/acme/project/web") doesn't store
            // "org/acme/project" but WalkDescendants must recurse into it to find
            // "web"/"mobile"). Without this, GetDescendants returns empty for
            // any tree whose structure has "directory" levels.
            var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var k in _nodes.Keys)
            {
                if (!string.IsNullOrEmpty(prefix) && !k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrEmpty(prefix) && k.Contains('/'))
                {
                    // root level: path with '/' — top segment is a directory
                    directoryPaths.Add(k.Split('/', 2)[0]);
                    continue;
                }
                var segments = k.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == expectedDepth)
                    nodePaths.Add(k);
                else if (segments.Length > expectedDepth)
                {
                    // intermediate segment at expectedDepth becomes a directory entry
                    var dirPath = string.Join("/", segments.Take(expectedDepth));
                    if (!_nodes.ContainsKey(dirPath))
                        directoryPaths.Add(dirPath);
                }
            }

            return Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(
                (nodePaths, directoryPaths));
        });

    /// <summary>
    /// Native descendant enumeration: a direct prefix scan over the path-keyed
    /// store — exact and race-free against the dictionary that IS the storage of
    /// record. Declared on this class (not inherited from the base) for the same
    /// interface-slot reason as <see cref="ResolvePath"/> below.
    /// </summary>
    public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
        => Observable.Defer(() =>
        {
            var root = Norm(rootPath);
            if (string.IsNullOrEmpty(root))
                return Observable.Return<IReadOnlyCollection<string>>(
                    _nodes.Keys.ToImmutableList());
            var prefix = root + "/";
            return Observable.Return<IReadOnlyCollection<string>>(
                _nodes.Keys
                    .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToImmutableList());
        });

    /// <inheritdoc />
    public override IObservable<bool> Exists(string path)
        => Observable.Defer(() => Observable.Return(_nodes.ContainsKey(Norm(path))));

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            var normalized = Norm(fullPath);
            if (string.IsNullOrEmpty(normalized))
                return Observable.Return<(MeshNode?, int)>((null, 0));

            _logger?.LogDebug("[InMemoryAdapter#{Id:X}] FindBestPrefix '{Path}' (count={Count}, keys=[{Keys}])",
                GetHashCode(), normalized, _nodes.Count, string.Join(',', _nodes.Keys));

            var pathSegments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int depth = pathSegments.Length; depth > 0; depth--)
            {
                var testPath = string.Join("/", pathSegments.Take(depth));
                if (_nodes.TryGetValue(testPath, out var node))
                    return Observable.Return<(MeshNode?, int)>((node, depth));
            }
            return Observable.Return<(MeshNode?, int)>((null, 0));
        });

    /// <summary>
    /// In-memory has no satellite-UNION to preserve, so <c>ResolvePath</c> just
    /// reuses the <see cref="FindBestPrefixMatch"/> segment walk. Declared
    /// explicitly so the interface implementation lives on
    /// <c>InMemoryStorageAdapter</c> itself (alongside the <c>, IStorageAdapter</c>
    /// in the class header) — without that, the base
    /// <see cref="SimpleMeshNodeStorage"/> owns the interface slot and our
    /// <c>public</c> override is shadowed by the interface's default
    /// <c>(null, 0)</c> impl. Symptom of the bug: every
    /// <c>FileSystemObservableQueryTests</c> Delete failed NotFound while the
    /// node sat happily in <c>_nodes</c>.
    /// </summary>
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => FindBestPrefixMatch(fullPath, options);

    /// <inheritdoc />
    public override IObservable<object> GetPartitionObjects(
        string nodePath, string? subPath, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            var key = PartitionKey(nodePath, subPath);
            return _partitionObjects.TryGetValue(key, out var list)
                ? list.ToObservable()
                : Observable.Empty<object>();
        });

    /// <inheritdoc />
    public override IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath,
        IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            _partitionObjects[PartitionKey(nodePath, subPath)] = objects.ToList();
            return Observable.Return(Unit.Default);
        });

    /// <inheritdoc />
    public override IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => Observable.Defer(() =>
        {
            _partitionObjects.TryRemove(PartitionKey(nodePath, subPath), out _);
            return Observable.Return(Unit.Default);
        });

    /// <inheritdoc />
    public override IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => Observable.Defer(() => Observable.Return<DateTimeOffset?>(
            _partitionObjects.ContainsKey(PartitionKey(nodePath, subPath))
                ? DateTimeOffset.UtcNow : null));

    private static string PartitionKey(string nodePath, string? subPath)
    {
        var key = Norm(nodePath);
        return string.IsNullOrEmpty(subPath) ? key : $"{key}/{Norm(subPath)}";
    }
}
