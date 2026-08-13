using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Outermost <see cref="IStorageAdapter"/> decorator that REFUSES a write whose path lies at
/// or under a subtree whose recursive deletion is currently in flight.
///
/// <para><b>Why.</b> <c>HandleDeleteNodeRequest</c> builds its plan, deletes bottom-up, and
/// verifies against storage — but a writer racing the delete (the NodeType compile watcher
/// creating <c>Release</c> satellites seconds into the tear-down, issue #839) used to land
/// rows under the half-deleted subtree between plan and commit. The
/// <see cref="RecentlyDeletedRegistry"/> TTL tombstone only drops per-node-hub RESURRECTION
/// saves; it never covered brand-new creations. This guard closes that hole as a hard
/// invariant, not a timer: the delete handler opens a
/// <see cref="RecentlyDeletedRegistry.BeginSubtreeDeletion"/> scope BEFORE enumerating its
/// plan and releases it when the operation completes (success or failure), and while the
/// scope is open every in-process write under the root is refused with an error the writer's
/// error sink surfaces. Cross-process writers are caught by the delete handler's
/// storage-verified drain loop instead.</para>
///
/// <para><b>Refusal is an OnError, deliberately.</b> Unlike
/// <see cref="MonotonicWriteGuardStorageAdapter"/> (which returns the stored node because a
/// durable "current truth" exists for the caller's contract), there is NO legitimate outcome
/// for a write into a subtree being deleted — emitting the node as if saved would tell the
/// creator "created" about a row that must not and will not exist. Callers' standard
/// <c>.Subscribe(_ => …, ex => log)</c> shape surfaces the refusal; a create request fails
/// with a clear message instead of silently minting a doomed node.</para>
///
/// <para>Pass-through when no <see cref="RecentlyDeletedRegistry"/> is registered (meshes
/// without Graph) and O(active deletions) — i.e. effectively free — on the write hot path.</para>
/// </summary>
internal sealed class SubtreeDeletionGuardStorageAdapter(
    IStorageAdapter inner,
    RecentlyDeletedRegistry? registry,
    ILogger<SubtreeDeletionGuardStorageAdapter>? logger = null) : IStorageAdapter
{
    /// <summary>
    /// 🚨 Decorators MUST forward Changes — the interface default is
    /// <c>Observable.Empty</c>, which silently starves every synced-query subscriber.
    /// </summary>
    public IObservable<DataChangeNotification> Changes => inner.Changes;

    /// <inheritdoc />
    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => registry is null
            ? inner.Write(node, options)
            : Observable.Defer(() =>
            {
                if (registry.IsUnderActiveDeletion(node.Path, out var root))
                {
                    logger?.LogWarning(
                        "[SubtreeDeletionGuard] REFUSED write to {Path}: the subtree '{Root}' is being "
                        + "deleted. Nodes must not be created under a subtree while its recursive "
                        + "deletion is in flight.",
                        node.Path, root);
                    return Observable.Throw<MeshNode?>(new InvalidOperationException(
                        $"Cannot write '{node.Path}': the subtree '{root}' is currently being deleted."));
                }
                return inner.Write(node, options);
            });

    /// <inheritdoc />
    public IObservable<IReadOnlyList<MeshNode>> WriteMany(
        IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
        => registry is null
            ? inner.WriteMany(nodes, options)
            : Observable.Defer(() =>
            {
                foreach (var node in nodes)
                {
                    if (registry.IsUnderActiveDeletion(node.Path, out var root))
                    {
                        logger?.LogWarning(
                            "[SubtreeDeletionGuard] REFUSED batch write: {Path} lies under the subtree "
                            + "'{Root}' whose deletion is in flight.",
                            node.Path, root);
                        return Observable.Throw<IReadOnlyList<MeshNode>>(new InvalidOperationException(
                            $"Cannot write '{node.Path}': the subtree '{root}' is currently being deleted."));
                    }
                }
                return inner.WriteMany(nodes, options);
            });

    /// <inheritdoc />
    /// <remarks>
    /// Guarded exactly like <see cref="Write"/> — a compare-and-set into a subtree being deleted is
    /// still a write — and otherwise forwarded, because the interface default would fall back to a
    /// non-atomic read-compare-write and silently strip the backend's atomicity at the OUTERMOST
    /// decorator, which is this one.
    /// </remarks>
    public IObservable<bool?> WriteIfVersion(
        MeshNode node, long expectedVersion, JsonSerializerOptions options)
        => registry is null
            ? inner.WriteIfVersion(node, expectedVersion, options)
            : Observable.Defer(() =>
            {
                if (registry.IsUnderActiveDeletion(node.Path, out var root))
                {
                    logger?.LogWarning(
                        "[SubtreeDeletionGuard] REFUSED compare-and-set write to {Path}: the subtree "
                        + "'{Root}' is being deleted.",
                        node.Path, root);
                    return Observable.Throw<bool?>(new InvalidOperationException(
                        $"Cannot write '{node.Path}': the subtree '{root}' is currently being deleted."));
                }
                return inner.WriteIfVersion(node, expectedVersion, options);
            });

    /// <inheritdoc />
    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
        => inner.Read(path, options);

    /// <inheritdoc />
    public IObservable<MeshNode> ReadMany(IReadOnlyCollection<string> paths, JsonSerializerOptions options)
        => inner.ReadMany(paths, options);

    /// <inheritdoc />
    public IObservable<string> Delete(string path) => inner.Delete(path);

    /// <inheritdoc />
    public IObservable<bool> DeleteIfExists(string path) => inner.DeleteIfExists(path);

    /// <inheritdoc />
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
        => inner.ListChildPaths(parentPath);

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
        => inner.ListDescendantPaths(rootPath);

    /// <inheritdoc />
    public IObservable<bool> Exists(string path) => inner.Exists(path);

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatch(
        string fullPath, JsonSerializerOptions options)
        => inner.FindBestPrefixMatch(fullPath, options);

    /// <inheritdoc />
    public IObservable<(MeshNode? Node, int MatchedSegments)> ResolvePath(
        string fullPath, JsonSerializerOptions options)
        => inner.ResolvePath(fullPath, options);

    /// <inheritdoc />
    public IObservable<IEnumerable<string>> ListPartitionSubPaths(string nodePath)
        => inner.ListPartitionSubPaths(nodePath);

    /// <inheritdoc />
    public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
        => inner.GetPartitionObjects(nodePath, subPath, options);

    /// <inheritdoc />
    public IObservable<Unit> SavePartitionObjects(
        string nodePath, string? subPath,
        IReadOnlyCollection<object> objects, JsonSerializerOptions options)
        => inner.SavePartitionObjects(nodePath, subPath, objects, options);

    /// <inheritdoc />
    public IObservable<Unit> DeletePartitionObjects(string nodePath, string? subPath = null)
        => inner.DeletePartitionObjects(nodePath, subPath);

    /// <inheritdoc />
    public IObservable<DateTimeOffset?> GetPartitionMaxTimestamp(string nodePath, string? subPath = null)
        => inner.GetPartitionMaxTimestamp(nodePath, subPath);
}
