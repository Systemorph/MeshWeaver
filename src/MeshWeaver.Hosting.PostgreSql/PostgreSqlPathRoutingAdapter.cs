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
/// <para>Routing is observable end-to-end:
/// <see cref="PostgreSqlPartitionStorageProvider.ResolveAdapterForSchema"/>
/// composes the per-namespace <see cref="System.Reactive.Subjects.ReplaySubject{T}"/>
/// (live partition state) with adapter construction. Once a schema is
/// resolved it's cached locally in <see cref="_adapters"/> so the
/// observable round-trip is paid once per (schema, silo).</para>
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

    private IObservable<IStorageAdapter?> Resolve(string? path)
    {
        if (string.IsNullOrEmpty(path)) return Observable.Return<IStorageAdapter?>(null);
        var seg = GetFirstSegment(path);
        if (seg == null) return Observable.Return<IStorageAdapter?>(null);

        // Cached per-schema adapter: avoids re-querying the partition subject
        // after the first resolution. The subject still feeds Matches/
        // ResolveDefinition for live partition-existence checks — this cache
        // is purely an "adapter instance for an already-known schema" map.
        if (_adapters.TryGetValue(seg, out var cached))
            return Observable.Return<IStorageAdapter?>(cached);

        return _provider.ResolveAdapterForSchema(seg)
            .Select(adapter =>
            {
                if (adapter is null) return (IStorageAdapter?)null;
                return _adapters.GetOrAdd(seg, _ => adapter);
            });
    }

    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => Resolve(path).SelectMany(a => a?.Read(path, options) ?? Observable.Return<MeshNode?>(null));

    public IObservable<MeshNode> Write(MeshNode node, JsonSerializerOptions options)
        => Resolve(node.Path).SelectMany(a => a?.Write(node, options)
            ?? Observable.Throw<MeshNode>(new InvalidOperationException(
                $"PostgreSql provider has no PartitionDefinition for '{node.Path}'.")));

    public IObservable<string> Delete(string path)
        => Resolve(path).SelectMany(a => a?.Delete(path) ?? Observable.Return(path));

    public IObservable<bool> Exists(string path)
        => Resolve(path).SelectMany(a => a?.Exists(path) ?? Observable.Return(false));

    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => Resolve(fullPath).SelectMany(a => a?.FindBestPrefixMatch(fullPath, options)
            ?? Observable.Return<(MeshNode?, int)>((null, 0)));

    /// <summary>
    /// Forwards to the per-schema adapter's <see cref="IStorageAdapter.ResolvePath"/>
    /// — PostgreSqlStorageAdapter overrides this with a single UNION query
    /// across mesh_nodes + every satellite table named in
    /// <see cref="PartitionDefinition.TableMappings"/>.
    /// </summary>
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => Resolve(fullPath).SelectMany(a => a?.ResolvePath(fullPath, options)
            ?? Observable.Return<(MeshNode?, int)>((null, 0)));

    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
        ListChildPaths(string? parentPath)
        => string.IsNullOrEmpty(parentPath)
            ? Observable.Throw<(IEnumerable<string>, IEnumerable<string>)>(
                new NotSupportedException(
                    "Root-level listing is a query concern; use IMeshQueryCore."))
            : Resolve(parentPath).SelectMany(a => a?.ListChildPaths(parentPath)
                ?? Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], [])));

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

    private static string? GetFirstSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Trim('/');
        if (normalized.Length == 0) return null;
        var slash = normalized.IndexOf('/');
        return slash < 0 ? normalized : normalized[..slash];
    }
}
