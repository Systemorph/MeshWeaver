using System.Collections.Concurrent;
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
            if (!string.IsNullOrEmpty(node.Path))
            {
                _nodes[Norm(node.Path)] = node;
                _logger?.LogDebug("[InMemoryAdapter#{Id:X}] Write {Path} (count={Count})",
                    GetHashCode(), Norm(node.Path), _nodes.Count);
                try { _changes.OnNext(DataChangeNotification.Updated(Norm(node.Path), node)); } catch { /* never throw */ }
            }
            return Observable.Return(node);
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
