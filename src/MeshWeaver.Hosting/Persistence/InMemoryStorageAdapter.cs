using System.Collections.Concurrent;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// In-memory <see cref="IStorageAdapter"/> for non-persistent partitions
/// (test fixtures, the catch-all wildcard partition in samples). Holds nodes
/// in a path-keyed <see cref="ConcurrentDictionary{TKey,TValue}"/> that IS
/// the storage of record — there is no separate persistence-service cache
/// on top. <see cref="AdapterPersistenceService"/> delegates straight here.
///
/// <para>This used to be a null-object that returned nothing; the actual
/// node table lived in <c>AdapterPersistenceService._nodes</c>. That cache
/// got removed when the eager <c>LoadNodesRecursivelyAsync</c> walk was
/// pulled (~30% CPU per profiling), which left the in-memory test
/// framework with no storage at all. Storage moved DOWN here so the
/// persistence service stays a thin passthrough.</para>
/// </summary>
internal sealed class InMemoryStorageAdapter : IStorageAdapter
{
    private readonly ConcurrentDictionary<string, MeshNode> _nodes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<object>> _partitionObjects =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger? _logger;

    public InMemoryStorageAdapter(ILogger<InMemoryStorageAdapter>? logger = null)
    {
        _logger = logger;
    }

    private static string Norm(string? path) => path?.Trim('/') ?? "";

    public Task<MeshNode?> ReadAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
    {
        _nodes.TryGetValue(Norm(path), out var node);
        _logger?.LogDebug("[InMemoryAdapter#{Id:X}] Read {Path} → {Found}",
            GetHashCode(), Norm(path), node != null ? "hit" : "miss");
        return Task.FromResult<MeshNode?>(node);
    }

    public Task WriteAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(node.Path))
        {
            _nodes[Norm(node.Path)] = node;
            _logger?.LogDebug("[InMemoryAdapter#{Id:X}] Write {Path} (count={Count})",
                GetHashCode(), Norm(node.Path), _nodes.Count);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        _nodes.TryRemove(Norm(path), out _);
        return Task.CompletedTask;
    }

    public Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(
        string? parentPath, CancellationToken ct = default)
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
        // lives there (e.g. SaveNode(\"org/acme/project/web\") doesn't store
        // \"org/acme/project\" but WalkDescendants must recurse into it to find
        // \"web\"/\"mobile\"). Without this, GetDescendants returns empty for
        // any tree whose structure has \"directory\" levels.
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

        return Task.FromResult<(IEnumerable<string>, IEnumerable<string>)>(
            (nodePaths, directoryPaths));
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => Task.FromResult(_nodes.ContainsKey(Norm(path)));

    public Task<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatchAsync(
        string fullPath, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var normalized = Norm(fullPath);
        if (string.IsNullOrEmpty(normalized))
            return Task.FromResult<(MeshNode?, int)>((null, 0));

        _logger?.LogDebug("[InMemoryAdapter#{Id:X}] FindBestPrefix '{Path}' (count={Count}, keys=[{Keys}])",
            GetHashCode(), normalized, _nodes.Count, string.Join(',', _nodes.Keys));

        var pathSegments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int depth = pathSegments.Length; depth > 0; depth--)
        {
            var testPath = string.Join("/", pathSegments.Take(depth));
            if (_nodes.TryGetValue(testPath, out var node))
                return Task.FromResult<(MeshNode?, int)>((node, depth));
        }
        return Task.FromResult<(MeshNode?, int)>((null, 0));
    }

    public async IAsyncEnumerable<object> GetPartitionObjectsAsync(
        string nodePath, string? subPath, JsonSerializerOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var key = PartitionKey(nodePath, subPath);
        if (_partitionObjects.TryGetValue(key, out var list))
        {
            foreach (var obj in list)
            {
                ct.ThrowIfCancellationRequested();
                yield return obj;
            }
        }
        await Task.CompletedTask;
    }

    public Task SavePartitionObjectsAsync(
        string nodePath, string? subPath,
        IReadOnlyCollection<object> objects, JsonSerializerOptions options,
        CancellationToken ct = default)
    {
        _partitionObjects[PartitionKey(nodePath, subPath)] = objects.ToList();
        return Task.CompletedTask;
    }

    public Task DeletePartitionObjectsAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
    {
        _partitionObjects.TryRemove(PartitionKey(nodePath, subPath), out _);
        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(
        string nodePath, string? subPath = null, CancellationToken ct = default)
        => Task.FromResult<DateTimeOffset?>(
            _partitionObjects.ContainsKey(PartitionKey(nodePath, subPath))
                ? DateTimeOffset.UtcNow : null);

    private static string PartitionKey(string nodePath, string? subPath)
    {
        var key = Norm(nodePath);
        return string.IsNullOrEmpty(subPath) ? key : $"{key}/{Norm(subPath)}";
    }
}
