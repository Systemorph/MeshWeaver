using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
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

    /// <summary>
    /// Wraps <paramref name="nodes"/> in an immutable, path-keyed lookup. Duplicate
    /// paths are deduped to the last entry (caller wins).
    /// </summary>
    public StaticNodeStorageAdapter(IEnumerable<MeshNode> nodes)
    {
        _nodes = nodes
            .Where(n => !string.IsNullOrEmpty(n.Path))
            .GroupBy(n => Norm(n.Path), StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    }

    private static string Norm(string? path) => path?.Trim('/') ?? "";

    /// <inheritdoc/>
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            _nodes.TryGetValue(Norm(path), out var node);
            return Observable.Return<MeshNode?>(node);
        });

    /// <inheritdoc/>
    public IObservable<MeshNode> Write(MeshNode node, JsonSerializerOptions options)
        => Observable.Throw<MeshNode>(new NotSupportedException(
            $"StaticNodeStorageAdapter is read-only; cannot write '{node.Path}'."));

    /// <inheritdoc/>
    public IObservable<string> Delete(string path)
        => Observable.Throw<string>(new NotSupportedException(
            $"StaticNodeStorageAdapter is read-only; cannot delete '{path}'."));

    /// <inheritdoc/>
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
        ListChildPaths(string? parentPath)
        => Observable.Defer(() =>
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

            return Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(
                (nodePaths, directoryPaths));
        });

    /// <inheritdoc/>
    public IObservable<bool> Exists(string path)
        => Observable.Defer(() => Observable.Return(_nodes.ContainsKey(Norm(path))));

    /// <inheritdoc/>
    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => Observable.Defer(() =>
        {
            var normalized = Norm(fullPath);
            if (string.IsNullOrEmpty(normalized))
                return Observable.Return<(MeshNode?, int)>((null, 0));

            var pathSegments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int depth = pathSegments.Length; depth > 0; depth--)
            {
                var testPath = string.Join("/", pathSegments.Take(depth));
                if (_nodes.TryGetValue(testPath, out var node))
                    return Observable.Return<(MeshNode?, int)>((node, depth));
            }
            return Observable.Return<(MeshNode?, int)>((null, 0));
        });

    /// <inheritdoc/>
    public IObservable<object> GetPartitionObjects(
        string nodePath, string? subPath, JsonSerializerOptions options)
        // Static partitions don't carry partition objects — the data is the nodes themselves.
        => Observable.Empty<object>();

    /// <inheritdoc/>
    public IObservable<Unit> SavePartitionObjects(string nodePath, string? subPath,
        IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => Observable.Throw<Unit>(new NotSupportedException("StaticNodeStorageAdapter is read-only."));

    /// <inheritdoc/>
    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => Observable.Throw<Unit>(new NotSupportedException("StaticNodeStorageAdapter is read-only."));

    /// <inheritdoc/>
    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(
        string nodePath, string? subPath = null)
        => Observable.Return<DateTimeOffset?>(null);
}
