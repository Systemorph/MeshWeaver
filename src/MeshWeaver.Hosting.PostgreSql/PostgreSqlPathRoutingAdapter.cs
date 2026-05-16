using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Path-routing <see cref="IStorageAdapter"/> facade exposed by
/// <see cref="PostgreSqlPartitionStorageProvider.Adapter"/>. Resolves the
/// path's first segment to a per-schema <see cref="PostgreSqlStorageAdapter"/>
/// (cached) and delegates every operation. Per-table routing within a schema
/// happens inside <see cref="PostgreSqlStorageAdapter"/> itself via
/// <see cref="PartitionDefinition.ResolveTable"/>.
///
/// <para>Replaces the legacy <c>PostgreSqlPartitionedStoreFactory</c>'s
/// per-segment <c>CreateStoreAsync</c> output: same per-schema isolation,
/// but driven by a synchronous lookup against the registered partition
/// dictionary rather than an async factory call.</para>
/// </summary>
internal sealed class PostgreSqlPathRoutingAdapter : IStorageAdapter
{
    private readonly PostgreSqlPartitionStorageProvider _provider;
    private readonly ConcurrentDictionary<string, IStorageAdapter> _adapters =
        new(StringComparer.OrdinalIgnoreCase);

    public PostgreSqlPathRoutingAdapter(PostgreSqlPartitionStorageProvider provider)
    {
        _provider = provider;
    }

    private IStorageAdapter? Resolve(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var seg = GetFirstSegment(path);
        if (seg == null) return null;
        if (!_provider.Matches(path)) return null;
        return _adapters.GetOrAdd(seg, _provider.ResolveAdapterForSchema);
    }

    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => Resolve(path)?.Read(path, options) ?? Observable.Return<MeshNode?>(null);

    public IObservable<MeshNode> Write(MeshNode node, JsonSerializerOptions options)
        => Resolve(node.Path)?.Write(node, options)
            ?? Observable.Throw<MeshNode>(new InvalidOperationException(
                $"PostgreSql provider has no PartitionDefinition for '{node.Path}'."));

    public IObservable<string> Delete(string path)
        => Resolve(path)?.Delete(path) ?? Observable.Return(path);

    public IObservable<bool> Exists(string path)
        => Resolve(path)?.Exists(path) ?? Observable.Return(false);

    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => Resolve(fullPath)?.FindBestPrefixMatch(fullPath, options)
            ?? Observable.Return<(MeshNode?, int)>((null, 0));

    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
        ListChildPaths(string? parentPath)
        => string.IsNullOrEmpty(parentPath)
            ? Observable.Throw<(IEnumerable<string>, IEnumerable<string>)>(
                new NotSupportedException(
                    "Root-level listing is a query concern; use IMeshQueryCore."))
            : Resolve(parentPath)?.ListChildPaths(parentPath)
                ?? Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], []));

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

    private static string? GetFirstSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Trim('/');
        if (normalized.Length == 0) return null;
        var slash = normalized.IndexOf('/');
        return slash < 0 ? normalized : normalized[..slash];
    }
}
