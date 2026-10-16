using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
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
/// <para><b>It is also the seam that SUPERSEDES a delete tombstone (#3008).</b> Every durable write
/// crosses this decorator, so its post-commit emission is the one place that knows, synchronously,
/// that the path exists again: <see cref="RecentlyDeletedRegistry.Supersede"/> runs in a <c>Do</c> on
/// the write's emission — strictly BEFORE the caller's <c>IMeshChangeFeed</c> publish, which
/// <c>StorageAdapterChangeFeedExtensions</c> composes downstream of it. That mirrors the delete, which
/// marks the tombstone synchronously before its response returns. Before this, the only clear ran in
/// a LIVE per-node hub's change handler — asynchronous, and absent when no hub was alive for the path
/// — so a reader whose delivery reached the still-disposing old hub after <c>Created</c> had already
/// been published was NACKed with the authoritative, non-retryable "will not reactivate".</para>
///
/// <para><b>A refusal at admission is not enough on its own (#6719).</b> A write admitted a moment
/// BEFORE the scope opened is already on its way to the store and commits whenever the backend gets
/// to it — possibly after the delete removed the row, recreating it (and its <c>Supersede</c> then
/// erased the tombstone, so the recreate looked legitimate). So every admitted write is also
/// REGISTERED with the registry until it terminates, and a deletion waits for the writes admitted
/// under its root (<see cref="RecentlyDeletedRegistry.AdmittedWritesSettled"/>) before it plans or
/// removes anything. In-process, every write under the root is therefore refused or committed
/// BEFORE the delete, never after it.</para>
///
/// <para>Pass-through when no <see cref="RecentlyDeletedRegistry"/> is registered (meshes
/// without Graph) and O(active deletions) — i.e. effectively free — on the write hot path.</para>
/// </summary>
internal sealed class SubtreeDeletionGuardStorageAdapter(
    IStorageAdapter inner,
    RecentlyDeletedRegistry? registry,
    ILogger<SubtreeDeletionGuardStorageAdapter>? logger = null) : IStorageAdapter, IDisposable
{
    /// <summary>
    /// Owns the connection of every admitted write still in flight (see <see cref="Admitted{T}"/>).
    /// A connection leaves it as its write terminates, so it holds LIVE writes only; disposing the
    /// adapter with its mesh releases whatever is still connected instead of leaving it rooted
    /// where no teardown can reach it.
    /// </summary>
    private readonly CompositeDisposable _admittedConnections = new();

    /// <inheritdoc />
    public void Dispose() => _admittedConnections.Dispose();

    /// <summary>
    /// 🚨 Decorators MUST forward Changes — the interface default is
    /// <c>Observable.Empty</c>, which silently starves every synced-query subscriber.
    /// </summary>
    public IObservable<DataChangeNotification> Changes => inner.Changes;

    /// <inheritdoc />
    public IObservable<ChangeFeedGap> ChangeFeedGaps => inner.ChangeFeedGaps;

    /// <inheritdoc />
    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions options)
        => registry is null
            ? inner.Write(node, options)
            : Admitted(
                [node.Path],
                (path, root) =>
                {
                    logger?.LogWarning(
                        "[SubtreeDeletionGuard] REFUSED write to {Path}: the subtree '{Root}' is being "
                        + "deleted. Nodes must not be created under a subtree while its recursive "
                        + "deletion is in flight.",
                        path, root);
                    return Observable.Throw<MeshNode?>(new SubtreeDeletionInFlightException(path, root));
                },
                // Post-commit, pre-publish: the null sentinel means "no adapter owns this path" —
                // nothing was written, so nothing is superseded.
                () => inner.Write(node, options)
                    .Do(saved =>
                    {
                        if (saved is not null)
                            registry.Supersede(saved.Path, saved.Version);
                    }));

    /// <inheritdoc />
    public IObservable<IReadOnlyList<MeshNode>> WriteMany(
        IReadOnlyCollection<MeshNode> nodes, JsonSerializerOptions options)
        => registry is null
            ? inner.WriteMany(nodes, options)
            : WriteManyAdmitted(nodes.ToArray(), options);

    /// <summary>
    /// <see cref="WriteMany"/> over ONE snapshot of the batch: the paths the guard tests and
    /// registers and the nodes handed to the store are the same array, so a caller mutating its
    /// collection between the call and the subscription cannot make the guard admit one path while
    /// the store writes another.
    /// </summary>
    private IObservable<IReadOnlyList<MeshNode>> WriteManyAdmitted(
        MeshNode[] nodes, JsonSerializerOptions options)
        => Admitted(
                nodes.Select(n => n.Path).ToArray(),
                (path, root) =>
                {
                    logger?.LogWarning(
                        "[SubtreeDeletionGuard] REFUSED batch write: {Path} lies under the subtree "
                        + "'{Root}' whose deletion is in flight.",
                        path, root);
                    return Observable.Throw<IReadOnlyList<MeshNode>>(new SubtreeDeletionInFlightException(path, root));
                },
                // Only the nodes the adapter reports as written were claimed — same rule as the
                // singular null sentinel.
                () => inner.WriteMany(nodes, options)
                    .Do(written =>
                    {
                        foreach (var saved in written)
                            registry!.Supersede(saved.Path, saved.Version);
                    }));

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
            : Admitted(
                [node.Path],
                (path, root) =>
                {
                    logger?.LogWarning(
                        "[SubtreeDeletionGuard] REFUSED compare-and-set write to {Path}: the subtree "
                        + "'{Root}' is being deleted.",
                        path, root);
                    return Observable.Throw<bool?>(new SubtreeDeletionInFlightException(path, root));
                },
                // `true` is the one outcome that committed; a version mismatch (false) or an
                // unowned path (null) wrote nothing.
                () => inner.WriteIfVersion(node, expectedVersion, options)
                    .Do(written =>
                    {
                        if (written == true)
                            registry.Supersede(node.Path, node.Version);
                    }));

    /// <summary>
    /// Admits one write, or refuses it (#6719). On Subscribe the write is REGISTERED with the
    /// registry FIRST and only then tested against the active deletions — the order that makes a
    /// deletion opening concurrently either refuse this write or wait for it
    /// (<see cref="RecentlyDeletedRegistry.AdmittedWritesSettled"/>), never miss it. An admitted
    /// write stays registered until the WRITE ITSELF terminates (completion or error), so a
    /// deletion whose scope opened after the admission runs only once this write has committed:
    /// its storage delete then removes what the write stored, instead of the write recreating what
    /// the delete removed. Cold: nothing is registered or written before Subscribe.
    ///
    /// <para>🚨 <b>Once admitted, the write runs to its own terminal, detached from the caller's
    /// subscription.</b> The store adapters below are EAGER (<c>IIoPool.Run</c> schedules the I/O
    /// on call and keeps it running whoever unsubscribes), so a caller's timeout or unsubscribe
    /// does NOT stop the write — it can still commit. Releasing the registration on that
    /// unsubscribe would let a delete proceed while the write is still on its way and recreate the
    /// node after it. So the admitted write is connected here, its registration is released only
    /// by its own completion or error, and the caller observes a replay of it; an unsubscribe only
    /// stops the caller listening.</para>
    ///
    /// <para>🚨 <b>The caller is attached BEFORE the write is connected.</b> The registration is
    /// released in the write's <c>Finally</c>, and a deletion waiting on it resumes right there. A
    /// write that completes synchronously inside the connect would otherwise settle — and let the
    /// delete remove the row and publish <c>Deleted</c> — while its result still sat in the replay
    /// buffer, so the caller's own post-commit publish (<c>Created</c>/<c>Updated</c>) would follow
    /// the delete's. Attached first, the caller receives the result and the completion before the
    /// <c>Finally</c> runs, whichever thread completes the write. The connection is still owned
    /// here, not by the caller's subscription.</para>
    /// </summary>
    /// <param name="paths">Every node path the write commits.</param>
    /// <param name="refuse">Builds the refusal for the first path found under an active deletion.</param>
    /// <param name="write">The write itself, subscribed only when admitted.</param>
    private IObservable<T> Admitted<T>(
        IReadOnlyCollection<string> paths,
        Func<string, string?, IObservable<T>> refuse,
        Func<IObservable<T>> write)
        => Observable.Create<T>(observer =>
        {
            var admission = registry!.AdmitWrite(paths);
            foreach (var path in paths)
                if (registry.IsUnderActiveDeletion(path, out var root))
                {
                    admission.Dispose();
                    return refuse(path, root).Subscribe(observer);
                }

            IObservable<T> inner;
            try
            {
                inner = write();
            }
            catch (Exception ex)
            {
                // Not swallowed: the registration is released and the fault reaches the caller.
                admission.Dispose();
                observer.OnError(ex);
                return Disposable.Empty;
            }

            var outcome = inner.Finally(admission.Dispose).Replay();
            var callerSubscription = outcome.Subscribe(observer);
            outcome.ConnectOwnedBy(_admittedConnections);
            return callerSubscription;
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
    /// <remarks>Forwarded: the interface default is a non-atomic read-compare-delete (#6856).</remarks>
    public IObservable<bool> DeleteIfVersionAtMost(string path, long maxVersion, JsonSerializerOptions options)
        => inner.DeleteIfVersionAtMost(path, maxVersion, options);

    /// <inheritdoc />
    public IObservable<IReadOnlyList<string>> DeleteMany(IReadOnlyCollection<string> paths)
        => inner.DeleteMany(paths);

    /// <inheritdoc />
    public IObservable<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPaths(string? parentPath)
        => inner.ListChildPaths(parentPath);

    /// <inheritdoc />
    public IObservable<IReadOnlyCollection<string>> ListDescendantPaths(string rootPath)
        => inner.ListDescendantPaths(rootPath);

    /// <inheritdoc />
    /// <remarks>Pure delegation — only the composite below knows its providers, and the
    /// interface default (<c>null</c>) would silently drop the delete pre-flight (#1433).</remarks>
    public IObservable<string?> FindDeleteBlockingProvider(string path)
        => inner.FindDeleteBlockingProvider(path);

    /// <inheritdoc />
    public IObservable<bool> Exists(string path) => inner.Exists(path);

    /// <inheritdoc />
    /// <remarks>Pure delegation — the writable-only filter lives in the composite below, and
    /// the interface default would collapse it back onto <see cref="Exists"/> here.</remarks>
    public IObservable<bool> ExistsInWritableStorage(string path)
        => inner.ExistsInWritableStorage(path);

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
