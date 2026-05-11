using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Abstract base for "pedestrian" storage backends — in-memory, file-system,
/// embedded-resource — whose only way to enumerate descendants is to walk
/// the parent→children tree recursively. Subclasses provide the
/// <see cref="IStorageAdapter"/> primitives against their backing store;
/// the base layers the public <see cref="ListDescendantPaths"/> walk on top
/// for the pedestrian query provider's use.
///
/// <para>
/// 🚨 The descendant walk is the ONE concession to the "no naive load" rule
/// (per <c>Doc/Architecture/CqrsAndContentAccess.md</c>). It is contained
/// here. The routing layer (<c>RoutingPersistenceServiceCore</c>) and the
/// in-memory query engine (<c>MeshQueryEngine</c>) know nothing about it.
/// Only a dedicated <see cref="IMeshQueryProvider"/> registered for
/// pedestrian-backed partitions consumes <see cref="ListDescendantPaths"/>.
/// Postgres / Cosmos / blob backends don't extend this — they route queries
/// through their own native push-down provider.
/// </para>
///
/// <para>
/// 🚨 API is <see cref="IObservable{T}"/> end-to-end per the "Nothing async
/// ever" rule (<c>Doc/Architecture/AsynchronousCalls.md</c>). Subclasses
/// implement the IObservable <see cref="IStorageAdapter"/> primitives
/// directly — no Task surface anywhere.
/// </para>
/// </summary>
public abstract class SimpleMeshNodeStorage : IStorageAdapter
{
    // Subclasses implement these directly — IObservable end-to-end.

    /// <inheritdoc />
    public abstract IObservable<MeshNode?> Read(string path, JsonSerializerOptions options);

    /// <inheritdoc />
    public abstract IObservable<Unit> Write(MeshNode node, JsonSerializerOptions options);

    /// <inheritdoc />
    public abstract IObservable<Unit> Delete(string path);

    /// <inheritdoc />
    public abstract IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath);

    /// <inheritdoc />
    public abstract IObservable<bool> Exists(string path);

    /// <inheritdoc />
    public abstract IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options);

    /// <inheritdoc />
    public abstract IObservable<Unit> SavePartitionObjects(string nodePath, string? subPath, IReadOnlyCollection<object> objects, JsonSerializerOptions options);

    /// <inheritdoc />
    public abstract IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null);

    /// <inheritdoc />
    public abstract IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null);

    /// <summary>
    /// Emits every descendant <b>path</b> under <paramref name="parentPath"/> by recursing
    /// through <see cref="IStorageAdapter.ListChildPaths"/>. Path-only — no MeshNode load.
    /// Subscribers do <see cref="IStorageAdapter.Read"/> per path when they actually need
    /// content. Cold observable; the walk fires on Subscribe.
    /// </summary>
    public IObservable<string> ListDescendantPaths(string? parentPath)
        => Observable.Defer(() => WalkPaths(parentPath));

    private IObservable<string> WalkPaths(string? parent)
        => ListChildPaths(parent)
            .SelectMany(level =>
                level.NodePaths.ToObservable()
                    .SelectMany(p => Observable.Return(p).Concat(WalkPaths(p)))
                    .Concat(level.DirectoryPaths.ToObservable().SelectMany(WalkPaths)));
}
