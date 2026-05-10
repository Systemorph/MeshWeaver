using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// 🚨 No-cache passthrough to <see cref="IStorageAdapter"/>. The previous
/// implementation maintained `_nodes` / `_comments` / `_partitionData`
/// dictionaries hydrated by an eager recursive Postgres walk at hub
/// activation; profiling showed that walk dominated CPU (~30% inclusive
/// time per the snapshot in <c>C:\tmp\claude\traces\portal-snap-2.nettrace</c>).
///
/// <para>The replacement defers everything to the storage adapter — every
/// read fans out to a single targeted adapter call, no in-memory state is
/// kept across calls. Static node repos that need an in-memory snapshot run
/// as their own partition with a dedicated <c>StaticNodeQueryProvider</c>;
/// nothing else caches here.</para>
///
/// <para>Class name kept for the moment so consumers compile; rename + the
/// fan-out partition dispatcher refactor follows in a separate change.</para>
/// </summary>
public class AdapterPersistenceService : IStorageService, IDisposable
{
    private readonly IStorageAdapter? _storageAdapter;
    private readonly IDataChangeNotifier? _changeNotifier;
    private readonly ILogger? _logger;

    /// <summary>
    /// Static-only node cache. ONLY populated by <see cref="SeedIfAbsent"/>
    /// (called by <c>IStaticNodeProvider.GetStaticNodes()</c>). User-saved
    /// nodes go straight to the storage adapter and are NOT cached here. The
    /// previous full-tree eager load via <c>LoadNodesRecursivelyAsync</c> is
    /// gone (~30% CPU per profiling) — reads merge static seeds + adapter
    /// hits on demand.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MeshNode> _staticNodes = new(StringComparer.OrdinalIgnoreCase);

    public AdapterPersistenceService(
        IStorageAdapter? storageAdapter = null,
        IDataChangeNotifier? changeNotifier = null,
        ILogger<AdapterPersistenceService>? logger = null)
    {
        // Default to an internal InMemoryStorageAdapter when none is supplied
        // (non-partitioned in-memory mode used by tests). Without this fallback,
        // writes silently no-op against a null adapter — used to be covered by
        // the now-removed `_nodes` cache in this class.
        _storageAdapter = storageAdapter ?? new InMemoryStorageAdapter();
        _changeNotifier = changeNotifier;
        _logger = logger;
    }

    public void Dispose() { }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public IObservable<MeshNode?> GetNode(string path, JsonSerializerOptions options)
        => Observable.FromAsync(ct => GetNodeAsyncCore(path, options, ct));

    /// <summary>Test/back-compat shim. Production callers go through <see cref="GetNode"/>.</summary>
    public Task<MeshNode?> GetNodeAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
        => GetNodeAsyncCore(path, options, ct);

    private async Task<MeshNode?> GetNodeAsyncCore(string path, JsonSerializerOptions options, CancellationToken ct)
    {
        // 🚨 Adapter-first, static-seed fallback. Writes via WriteAsync go to
        // the adapter only — they MUST shadow any static seed that lives at
        // the same path, otherwise hubs that update a seeded thread / node
        // see the stale seed on every read after the write. Was the cause of
        // the Orleans chat regression: thread MeshThread.Messages list
        // appeared frozen at the seeded 4 messages because GetNode kept
        // returning the static seed instead of the adapter-written version.
        if (_storageAdapter != null)
        {
            var adapterNode = await _storageAdapter.ReadAsync(path, options, ct);
            if (adapterNode != null) return adapterNode;
        }
        var normalized = NormalizePath(path);
        return _staticNodes.TryGetValue(normalized, out var staticNode) ? staticNode : null;
    }

    /// <summary>Direct children — adapter first (live state), then static seeds for paths the adapter doesn't yet cover.</summary>
    public async IAsyncEnumerable<MeshNode> GetChildrenAsync(string? parentPath, JsonSerializerOptions options)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) Adapter walk first so runtime writes shadow static seeds.
        if (_storageAdapter != null)
        {
            var (nodePaths, _) = await _storageAdapter.ListChildPathsAsync(parentPath ?? "", default);
            foreach (var path in nodePaths)
            {
                var node = await _storageAdapter.ReadAsync(path, options, default);
                if (node == null) continue;
                if (node.MainNode != null && node.MainNode != node.Path) continue;
                seenPaths.Add(NormalizePath(node.Path));
                yield return node;
            }
        }

        // 2) Static seeds for paths the adapter doesn't have.
        foreach (var n in EnumerateStaticChildren(parentPath, includeSatellites: false))
        {
            if (seenPaths.Add(NormalizePath(n.Path)))
                yield return n;
        }
    }

    /// <summary>Same as <see cref="GetChildrenAsync"/> but includes satellite nodes.</summary>
    public async IAsyncEnumerable<MeshNode> GetAllChildrenAsync(string? parentPath, JsonSerializerOptions options)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_storageAdapter != null)
        {
            var (nodePaths, _) = await _storageAdapter.ListChildPathsAsync(parentPath ?? "", default);
            foreach (var path in nodePaths)
            {
                var node = await _storageAdapter.ReadAsync(path, options, default);
                if (node == null) continue;
                seenPaths.Add(NormalizePath(node.Path));
                yield return node;
            }
        }

        foreach (var n in EnumerateStaticChildren(parentPath, includeSatellites: true))
        {
            if (seenPaths.Add(NormalizePath(n.Path)))
                yield return n;
        }
    }

    private IEnumerable<MeshNode> EnumerateStaticChildren(string? parentPath, bool includeSatellites)
    {
        var normalized = NormalizePath(parentPath);
        var parentSegments = string.IsNullOrEmpty(normalized)
            ? Array.Empty<string>()
            : normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var expectedDepth = parentSegments.Length + 1;

        foreach (var node in _staticNodes.Values)
        {
            if (!includeSatellites && node.MainNode != null && node.MainNode != node.Path)
                continue;
            var nodeSegments = node.Segments;
            if (nodeSegments.Count != expectedDepth) continue;
            var match = true;
            for (int i = 0; i < parentSegments.Length; i++)
                if (!nodeSegments[i].Equals(parentSegments[i], StringComparison.OrdinalIgnoreCase))
                { match = false; break; }
            if (match) yield return node;
        }
    }

    /// <summary>Recursive descendants — static seeds + adapter walk level-by-level.
    /// Storage adapters that can return the full subtree in one round-trip
    /// (e.g. PostgreSQL with one SQL <c>WHERE namespace LIKE ...</c>) should
    /// override this with a single bulk method.</summary>
    public async IAsyncEnumerable<MeshNode> GetDescendantsAsync(string? parentPath, JsonSerializerOptions options)
    {
        await foreach (var node in WalkDescendants(parentPath ?? "", options, includeSatellites: false))
            yield return node;
    }

    public async IAsyncEnumerable<MeshNode> GetAllDescendantsAsync(string? parentPath, JsonSerializerOptions options)
    {
        await foreach (var node in WalkDescendants(parentPath ?? "", options, includeSatellites: true))
            yield return node;
    }

    private async IAsyncEnumerable<MeshNode> WalkDescendants(string parentPath, JsonSerializerOptions options, bool includeSatellites)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedParent = NormalizePath(parentPath);

        // 1) Adapter walk first so runtime writes shadow static seeds.
        if (_storageAdapter != null)
        {
            var (nodePaths, dirPaths) = await _storageAdapter.ListChildPathsAsync(parentPath, default);
            foreach (var path in nodePaths)
            {
                var node = await _storageAdapter.ReadAsync(path, options, default);
                if (node != null && (includeSatellites || node.MainNode == null || node.MainNode == node.Path))
                {
                    seenPaths.Add(NormalizePath(node.Path));
                    yield return node;
                }
                await foreach (var descendant in WalkDescendants(path, options, includeSatellites))
                {
                    seenPaths.Add(NormalizePath(descendant.Path));
                    yield return descendant;
                }
            }
            foreach (var dir in dirPaths)
                await foreach (var descendant in WalkDescendants(dir, options, includeSatellites))
                {
                    seenPaths.Add(NormalizePath(descendant.Path));
                    yield return descendant;
                }
        }

        // 2) Static-seed descendants for paths the adapter walk didn't cover.
        foreach (var node in _staticNodes.Values)
        {
            if (!includeSatellites && node.MainNode != null && node.MainNode != node.Path) continue;
            var p = NormalizePath(node.Path);
            if (string.IsNullOrEmpty(normalizedParent)
                || p.StartsWith(normalizedParent + "/", StringComparison.OrdinalIgnoreCase))
            {
                if (seenPaths.Add(p))
                    yield return node;
            }
        }
    }

    /// <summary>Seeds a static node into the in-memory dict — called by
    /// IStaticNodeProvider implementations at hub init. Idempotent: existing
    /// keys are preserved (storage-adapter writes for the same path take
    /// precedence on read fallback). The static dict is the ONLY in-memory
    /// state this service holds.</summary>
    public void SeedIfAbsent(MeshNode node)
    {
        if (string.IsNullOrEmpty(node.Path)) return;
        _staticNodes.TryAdd(NormalizePath(node.Path), node);
    }

    public IObservable<MeshNode> SaveNode(MeshNode node, JsonSerializerOptions options) =>
        Observable.Defer(() =>
        {
            var savedNode = node with
            {
                LastModified = node.LastModified == default ? DateTimeOffset.UtcNow : node.LastModified
            };
            var write = _storageAdapter is null
                ? Observable.Return(savedNode)
                : Observable.FromAsync(ct => _storageAdapter.WriteAsync(savedNode, options, ct), Scheduler.Default)
                    .Select(_ => savedNode);
            return write.Do(saved =>
                _changeNotifier?.NotifyChange(DataChangeNotification.Updated(NormalizePath(saved.Path), saved)));
        });

    public IObservable<string> DeleteNode(string path, bool recursive = false) =>
        Observable.Defer(() =>
        {
            var normalizedPath = NormalizePath(path);
            if (_storageAdapter is null)
                return Observable.Return(path);

            var deleteOps = recursive
                ? Observable.FromAsync<IReadOnlyList<string>>(async ct =>
                    {
                        var paths = new List<string>();
                        await foreach (var n in WalkDescendants(normalizedPath, JsonSerializerOptions.Default, includeSatellites: true))
                            paths.Add(NormalizePath(n.Path));
                        paths.Add(normalizedPath);
                        return paths;
                    }, Scheduler.Default)
                : Observable.Return<IReadOnlyList<string>>(new[] { normalizedPath });

            return deleteOps.SelectMany(paths =>
            {
                var ops = paths.Select(p =>
                    Observable.FromAsync(ct => _storageAdapter.DeleteAsync(p, ct), Scheduler.Default)
                        .Do(_ => _changeNotifier?.NotifyChange(DataChangeNotification.Deleted(p, null))));
                return ops.Aggregate(
                    (IObservable<Unit>)Observable.Return(Unit.Default),
                    (acc, next) => acc.IgnoreElements().Concat(next));
            }).Select(_ => path);
        });

    public IObservable<MeshNode> MoveNode(string sourcePath, string targetPath, JsonSerializerOptions options) =>
        Observable.Defer(() =>
        {
            if (_storageAdapter is null)
                return Observable.Throw<MeshNode>(new InvalidOperationException("No storage adapter configured"));

            return Observable.FromAsync(async ct =>
            {
                var sourceNode = await _storageAdapter.ReadAsync(sourcePath, options, ct)
                    ?? throw new InvalidOperationException($"Source node not found: {sourcePath}");
                if (await _storageAdapter.ExistsAsync(targetPath, ct))
                    throw new InvalidOperationException($"Target path already exists: {targetPath}");

                var movedRoot = MeshNode.FromPath(targetPath) with
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
                await _storageAdapter.WriteAsync(movedRoot, options, ct);

                var normalizedSource = NormalizePath(sourcePath);
                var normalizedTarget = NormalizePath(targetPath);
                await foreach (var desc in WalkDescendants(normalizedSource, options, includeSatellites: true)
                    .WithCancellation(ct))
                {
                    var descPath = NormalizePath(desc.Path);
                    var newPath = normalizedTarget + descPath[normalizedSource.Length..];
                    var moved = MeshNode.FromPath(newPath) with
                    {
                        Name = desc.Name,
                        NodeType = desc.NodeType,
                        Icon = desc.Icon,
                        Order = desc.Order,
                        Content = desc.Content,
                        AssemblyLocation = desc.AssemblyLocation,
                        HubConfiguration = desc.HubConfiguration,
                        GlobalServiceConfigurations = desc.GlobalServiceConfigurations
                    };
                    await _storageAdapter.WriteAsync(moved, options, ct);
                }

                // Delete originals (descendants first, then root)
                var pathsToDelete = new List<string>();
                await foreach (var desc in WalkDescendants(normalizedSource, options, includeSatellites: true)
                    .WithCancellation(ct))
                    pathsToDelete.Add(NormalizePath(desc.Path));
                pathsToDelete.Add(normalizedSource);
                foreach (var p in pathsToDelete)
                    await _storageAdapter.DeleteAsync(p, ct);

                return movedRoot;
            }, Scheduler.Default);
        });

    /// <summary>Substring match against name and content under
    /// <paramref name="parentPath"/>. Walks the same descendant tree as
    /// <see cref="GetDescendantsAsync"/> and filters in memory — fine for the
    /// in-memory + file-system adapters tests use; PostgreSQL routes through
    /// <c>PostgreSqlMeshQuery</c>'s SQL search path before reaching this.</summary>
    public async IAsyncEnumerable<MeshNode> SearchAsync(string? parentPath, string query, JsonSerializerOptions options)
    {
        await foreach (var node in GetDescendantsAsync(parentPath, options))
        {
            if (node.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                || node.Content?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            {
                yield return node;
            }
        }
    }

    public IObservable<bool> Exists(string path) =>
        Observable.Defer(() =>
        {
            if (_staticNodes.ContainsKey(NormalizePath(path)))
                return Observable.Return(true);
            return _storageAdapter is null
                ? Observable.Return(false)
                : Observable.FromAsync(ct => _storageAdapter.ExistsAsync(path, ct), Scheduler.Default);
        });

    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options) =>
        Observable.FromAsync(ct => FindBestPrefixMatchAsyncImpl(fullPath, options, ct), Scheduler.Default);

    /// <summary>
    /// Try the storage adapter's optimized lookup first (PostgreSQL has a
    /// dedicated SQL path); if it returns nothing, walk path segments full →
    /// shallowest via per-segment ReadAsync — first hit is the deepest match.
    /// In-memory adapters that don't override <see cref="IStorageAdapter.FindBestPrefixMatchAsync"/>
    /// fall through to the segment walk so chat / routing-layer prefix lookups
    /// keep working.
    /// </summary>
    private async Task<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatchAsyncImpl(
        string fullPath, JsonSerializerOptions options, CancellationToken ct)
    {
        var normalized = NormalizePath(fullPath);
        if (string.IsNullOrEmpty(normalized)) return (null, 0);

        var pathSegments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // 1) Best static-seed match (deepest prefix in _staticNodes).
        var bestDepth = 0;
        MeshNode? best = null;
        foreach (var (key, node) in _staticNodes)
        {
            var depth = node.Segments.Count;
            if (depth <= bestDepth) continue;
            if (depth > pathSegments.Length) continue;
            var match = true;
            for (int i = 0; i < depth; i++)
                if (!node.Segments[i].Equals(pathSegments[i], StringComparison.OrdinalIgnoreCase))
                { match = false; break; }
            if (match)
            {
                bestDepth = depth;
                best = node;
            }
        }

        // 2) Adapter-optimized path (Postgres etc.) — only if it beats the static match.
        if (_storageAdapter != null)
        {
            var (adapterNode, adapterDepth) = await _storageAdapter.FindBestPrefixMatchAsync(fullPath, options, ct);
            if (adapterDepth > bestDepth)
            {
                bestDepth = adapterDepth;
                best = adapterNode;
            }

            // 3) Walk full → shallowest via Read; first hit at depth > bestDepth wins.
            for (int depth = pathSegments.Length; depth > bestDepth; depth--)
            {
                var testPath = string.Join("/", pathSegments.Take(depth));
                var node = await _storageAdapter.ReadAsync(testPath, options, ct);
                if (node != null)
                    return (node, depth);
            }
        }

        return best != null ? (best, bestDepth) : (null, 0);
    }

    private static string NormalizePath(string? path) => path?.Trim('/') ?? "";

    #region Comments — stub. Comments are stored as MeshNodes (NodeType:Comment)
    // so this in-memory dict was redundant; consumers query for Comment nodes
    // through the standard query path. Returning empty here while the legacy
    // surface is still on the interface.

#pragma warning disable CS1998
    public async IAsyncEnumerable<Comment> GetCommentsAsync(string nodePath, JsonSerializerOptions options)
    {
        yield break;
    }
#pragma warning restore CS1998

    public IObservable<Comment> AddComment(Comment comment, JsonSerializerOptions options) =>
        Observable.Return(comment with
        {
            Id = string.IsNullOrEmpty(comment.Id) ? Guid.NewGuid().ToString() : comment.Id,
            CreatedAt = comment.CreatedAt == default ? DateTimeOffset.UtcNow : comment.CreatedAt
        });

    public IObservable<string> DeleteComment(string commentId) => Observable.Return(commentId);
    public IObservable<Comment?> GetComment(string commentId) => Observable.Return<Comment?>(null);

    #endregion

    #region Partition Storage — passthrough to adapter, no _partitionData cache

    public IAsyncEnumerable<object> GetPartitionObjectsAsync(
        string nodePath, string? subPath, JsonSerializerOptions options) =>
        _storageAdapter is null
            ? AsyncEnumerable.Empty<object>()
            : _storageAdapter.GetPartitionObjectsAsync(nodePath, subPath, options);

    public IObservable<IReadOnlyCollection<object>> SavePartitionObjects(
        string nodePath, string? subPath,
        IReadOnlyCollection<object> objects, JsonSerializerOptions options) =>
        Observable.Defer(() =>
        {
            var write = _storageAdapter is null
                ? Observable.Return(Unit.Default)
                : Observable.FromAsync(
                    ct => _storageAdapter.SavePartitionObjectsAsync(nodePath, subPath, objects, options, ct),
                    Scheduler.Default);
            return write.Select(_ => objects);
        });

    public IObservable<string> DeletePartitionObjects(string nodePath, string? subPath = null) =>
        Observable.Defer(() =>
        {
            var del = _storageAdapter is null
                ? Observable.Return(Unit.Default)
                : Observable.FromAsync(
                    ct => _storageAdapter.DeletePartitionObjectsAsync(nodePath, subPath, ct),
                    Scheduler.Default);
            return del.Select(_ => subPath ?? nodePath);
        });

    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null) =>
        _storageAdapter is null
            ? Observable.Return<DateTimeOffset?>(null)
            : Observable.FromAsync(
                ct => _storageAdapter.GetPartitionMaxTimestampAsync(nodePath, subPath, ct),
                Scheduler.Default);

    #endregion
}

internal static class AsyncEnumerable
{
    public static IAsyncEnumerable<T> Empty<T>() => EmptyAsyncEnumerable<T>.Instance;

    private sealed class EmptyAsyncEnumerable<T> : IAsyncEnumerable<T>, IAsyncEnumerator<T>
    {
        public static readonly EmptyAsyncEnumerable<T> Instance = new();
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public T Current => default!;
        public ValueTask<bool> MoveNextAsync() => new(false);
        public ValueTask DisposeAsync() => default;
    }
}
