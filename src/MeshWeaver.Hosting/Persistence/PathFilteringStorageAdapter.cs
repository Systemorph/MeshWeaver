using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Scopes an <see cref="IStorageAdapter"/> to the paths a predicate accepts:
/// writes outside the scope DECLINE (emit <c>null</c>) so the
/// <see cref="PersistenceService"/> try-then-claim chain moves on to the next
/// writable provider; reads outside the scope short-circuit to "not mine".
/// This is the Stage-2 wrapper the <see cref="InMemoryPartitionStorageProvider"/>
/// <c>matches</c> parameter always promised — without it a scoped in-memory
/// provider (e.g. the compile-watcher Release store) claimed EVERY write into
/// RAM when it iterated ahead of the durable backend.
/// </summary>
public sealed class PathFilteringStorageAdapter(IStorageAdapter inner, Func<string, bool> matches)
    : IStorageAdapter
{
    /// <inheritdoc />
    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => matches(node.Path)
            ? inner.Write(node, options)
            : Observable.Return<MeshNode?>(null);

    /// <inheritdoc />
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => matches(path)
            ? inner.Read(path, options)
            : Observable.Return<MeshNode?>(null);

    /// <inheritdoc />
    public IObservable<string> Delete(string path)
        => matches(path)
            ? inner.Delete(path)
            : Observable.Return(path);

    /// <inheritdoc />
    public IObservable<bool> Exists(string path)
        => matches(path)
            ? inner.Exists(path)
            : Observable.Return(false);

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => matches(fullPath)
            ? inner.FindBestPrefixMatch(fullPath, options)
            : Observable.Return<(MeshNode?, int)>((null, 0));

    /// <inheritdoc />
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)>
        ListChildPaths(string? parentPath)
        => inner.ListChildPaths(parentPath);

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => matches(fullPath)
            ? inner.ResolvePath(fullPath, options)
            : Observable.Return<(MeshNode?, int)>((null, 0));

    /// <inheritdoc />
    public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
        => matches(nodePath)
            ? inner.GetPartitionObjects(nodePath, subPath, options)
            : Observable.Empty<object>();

    /// <inheritdoc />
    public IObservable<System.Reactive.Unit> SavePartitionObjects(
        string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => matches(nodePath)
            ? inner.SavePartitionObjects(nodePath, subPath, objects, options)
            : Observable.Return(System.Reactive.Unit.Default);

    /// <inheritdoc />
    public IObservable<System.Reactive.Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => matches(nodePath)
            ? inner.DeletePartitionObjects(nodePath, subPath)
            : Observable.Return(System.Reactive.Unit.Default);

    /// <inheritdoc />
    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => matches(nodePath)
            ? inner.GetPartitionMaxTimestamp(nodePath, subPath)
            : Observable.Return<DateTimeOffset?>(null);

    /// <inheritdoc />
    public IObservable<DataChangeNotification> Changes => inner.Changes;
}
