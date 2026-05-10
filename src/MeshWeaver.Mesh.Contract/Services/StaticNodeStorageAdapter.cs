using System.Collections.Immutable;
using System.Text.Json;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Read-only <see cref="IStorageAdapter"/> backed by a fixed list of
/// <see cref="MeshNode"/>s — typically the output of a single
/// <see cref="IStaticNodeProvider.GetStaticNodes"/> call. Lets a static-node
/// repo be plugged into the partition routing as an
/// <see cref="IPartitionStorageProvider"/> with this adapter, instead of
/// going through the legacy <c>SeedIfAbsent</c> fan-in into a writable
/// partition's <c>AdapterPersistenceService</c>.
///
/// <para>The nodes are the partition's storage of record. There is no other
/// in-memory cache layered on top — the dispatcher routes reads here and
/// gets data out of this adapter directly.</para>
///
/// <para>Writes throw <see cref="NotSupportedException"/> — static partitions
/// are immutable. To allow runtime writes under the same partition prefix,
/// layer a writable provider higher in the registration order so it wins
/// first-match.</para>
/// </summary>
public sealed class StaticNodeStorageAdapter : IStorageAdapter
{
    private readonly ImmutableDictionary<string, MeshNode> _nodes;

    public StaticNodeStorageAdapter(IEnumerable<MeshNode> nodes)
    {
        _nodes = nodes
            .Where(n => !string.IsNullOrEmpty(n.Path))
            .GroupBy(n => Norm(n.Path), StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    }

    private static string Norm(string? path) => path?.Trim('/') ?? "";

    public Task<MeshNode?> ReadAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
    {
        _nodes.TryGetValue(Norm(path), out var node);
        return Task.FromResult<MeshNode?>(node);
    }

    public Task WriteAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"StaticNodeStorageAdapter is read-only; cannot write '{node.Path}'.");

    public Task DeleteAsync(string path, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"StaticNodeStorageAdapter is read-only; cannot delete '{path}'.");

    public Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(
        string? parentPath, CancellationToken ct = default)
    {
        var normalized = Norm(parentPath);
        var prefix = string.IsNullOrEmpty(normalized) ? "" : normalized + "/";
        var expectedDepth = string.IsNullOrEmpty(normalized)
            ? 1
            : normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Length + 1;

        var nodePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var k in _nodes.Keys)
        {
            if (!string.IsNullOrEmpty(prefix) && !k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrEmpty(prefix) && k.Contains('/'))
            {
                directoryPaths.Add(k.Split('/', 2)[0]);
                continue;
            }
            var segments = k.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == expectedDepth)
                nodePaths.Add(k);
            else if (segments.Length > expectedDepth)
            {
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

        var pathSegments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int depth = pathSegments.Length; depth > 0; depth--)
        {
            var testPath = string.Join("/", pathSegments.Take(depth));
            if (_nodes.TryGetValue(testPath, out var node))
                return Task.FromResult<(MeshNode?, int)>((node, depth));
        }
        return Task.FromResult<(MeshNode?, int)>((null, 0));
    }

#pragma warning disable CS1998
    public async IAsyncEnumerable<object> GetPartitionObjectsAsync(
        string nodePath, string? subPath, JsonSerializerOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Static partitions don't carry partition objects — the data is the nodes themselves.
        yield break;
    }
#pragma warning restore CS1998

    public Task SavePartitionObjectsAsync(string nodePath, string? subPath,
        IReadOnlyCollection<object> objects, JsonSerializerOptions options, CancellationToken ct = default)
        => throw new NotSupportedException("StaticNodeStorageAdapter is read-only.");

    public Task DeletePartitionObjectsAsync(string nodePath, string? subPath = null, CancellationToken ct = default)
        => throw new NotSupportedException("StaticNodeStorageAdapter is read-only.");

    public Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(
        string nodePath, string? subPath = null, CancellationToken ct = default)
        => Task.FromResult<DateTimeOffset?>(null);
}
