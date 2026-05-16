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
    private readonly IReadOnlyList<IPartitionStorageProvider> _providers;

    public PersistenceService(IEnumerable<IPartitionStorageProvider> providers)
    {
        _providers = providers.ToList();
    }

    private IStorageAdapter? Resolve(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var p in _providers)
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

    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
        ListChildPaths(string? parentPath)
        => string.IsNullOrEmpty(parentPath)
            ? Observable.Throw<(IEnumerable<string>, IEnumerable<string>)>(
                new NotSupportedException(
                    "Root-level listing is a query concern; use IMeshQueryCore, not IStorageAdapter."))
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
}
