using System.Reactive.Linq;
using System.Text.Json;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Commit-then-publish helpers around <see cref="IStorageAdapter"/>. Each method
/// chains the adapter's write/delete with the corresponding
/// <see cref="IMeshChangeFeed"/> publish so that — by construction — the
/// <see cref="MeshChangeEvent"/> is only emitted AFTER the adapter's observable
/// has signalled completion (which the FileSystem, InMemory, and Postgres
/// adapters all gate on the durable commit via
/// <see cref="System.Reactive.Linq.Observable.FromAsync{T}(System.Func{System.Threading.CancellationToken, System.Threading.Tasks.Task{T}})"/>).
///
/// <para><b>Why a helper, not just convention.</b> The old shape required every
/// call site to subscribe to the storage write, capture the saved node in a
/// lambda, and then call <c>changeFeed.Publish(MeshChangeEvent.Created(saved))</c>
/// from inside that lambda. That worked when wired carefully, but it gave the
/// compiler no way to enforce the ordering — and at least one fallback branch
/// (UpdateNode's no-persistence path) published the change event without a
/// preceding storage write at all, so cross-replica subscribers saw a phantom
/// Updated for a row the database never received. Routing every mutation
/// through these helpers makes the invariant compile-time-visible: there is
/// no longer a way to call <c>Publish(Created/Updated/Deleted)</c> without
/// first chaining off the adapter's write/delete observable.</para>
///
/// <para><b>Ordering within a single chain.</b> The publish runs inside a
/// <c>.Do(...)</c> operator, which fires synchronously on each upstream
/// emission — strictly before any downstream <c>Subscribe</c> handler the
/// caller adds. So callers can safely use the returned observable to drive
/// the operation's response message, version-history writes,
/// in-process change-feed notifications, etc., knowing the
/// mesh-change-feed publish already happened.</para>
/// </summary>
public static class StorageAdapterChangeFeedExtensions
{
    /// <summary>
    /// Writes <paramref name="node"/> via <paramref name="adapter"/> and publishes a
    /// <see cref="MeshChangeEvent.Created"/> on <paramref name="changeFeed"/> after the
    /// write emits (post-commit). The saved node flows through unchanged for further
    /// composition. <paramref name="changeFeed"/> may be <c>null</c> — the publish is
    /// then a no-op, but the storage write still runs and emits as usual.
    ///
    /// <para>Skips the publish if the adapter emits <c>null</c> — that's the
    /// try-then-claim sentinel meaning "this adapter does not own this path,"
    /// not "the write succeeded."</para>
    /// </summary>
    public static IObservable<MeshNode?> WriteAndPublishCreated(
        this IStorageAdapter adapter,
        MeshNode node,
        JsonSerializerOptions options,
        IMeshChangeFeed? changeFeed)
        => adapter.Write(node, options)
            .Do(saved =>
            {
                if (saved is not null)
                    changeFeed?.Publish(MeshChangeEvent.Created(saved));
            });

    /// <summary>
    /// Writes <paramref name="node"/> via <paramref name="adapter"/> and publishes a
    /// <see cref="MeshChangeEvent.Updated"/> on <paramref name="changeFeed"/> after the
    /// write emits (post-commit). Same null-handling as
    /// <see cref="WriteAndPublishCreated"/>.
    /// </summary>
    public static IObservable<MeshNode?> WriteAndPublishUpdated(
        this IStorageAdapter adapter,
        MeshNode node,
        JsonSerializerOptions options,
        IMeshChangeFeed? changeFeed)
        => adapter.Write(node, options)
            .Do(saved =>
            {
                if (saved is not null)
                    changeFeed?.Publish(MeshChangeEvent.Updated(saved));
            });

    /// <summary>
    /// Writes <paramref name="nodes"/> in ONE <see cref="IStorageAdapter.WriteMany"/> and
    /// publishes a <see cref="MeshChangeEvent.Created"/> per node the adapter reports as
    /// written — the bulk twin of <see cref="WriteAndPublishCreated"/>, with the same
    /// commit-then-publish ordering (the <c>Do</c> fires on the post-commit emission).
    ///
    /// <para><b>Why this exists.</b> A bulk write is a mutation like any other, and the
    /// mesh-change feed is what invalidates the caches that decide whether a node is
    /// REACHABLE: <c>PathResolutionService</c>'s resolution cache, <c>MeshNodeStreamCache</c>,
    /// the Orleans path-cache invalidator. Writing rows without publishing leaves a node that
    /// EXISTS in storage and does not exist to the running mesh — a path whose resolution was
    /// cached as a miss (prefix + non-empty remainder is a perfectly cacheable value) stays a
    /// miss, so routing answers <c>No node found at '…'</c> until the process restarts. That is
    /// exactly what the installer's System-side bulk path did to <c>Store/Plugin</c> on a fresh
    /// mesh (2026-08-05): the row was in Postgres, every plugin root typed on it wore the
    /// missing-type overlay, and no compile ever started because no hub was ever woken.
    /// The adapter's own in-process <c>Changes</c> subject is NOT a substitute — it feeds
    /// synced queries, not the resolution caches.</para>
    ///
    /// <para>Nodes absent from the adapter's result were not claimed/written (see
    /// <see cref="IStorageAdapter.WriteMany"/>), so they are not announced — same rule as the
    /// singular helper's null sentinel.</para>
    /// </summary>
    public static IObservable<IReadOnlyList<MeshNode>> WriteManyAndPublishCreated(
        this IStorageAdapter adapter,
        IReadOnlyCollection<MeshNode> nodes,
        JsonSerializerOptions options,
        IMeshChangeFeed? changeFeed)
        => adapter.WriteMany(nodes, options)
            .Do(written =>
            {
                foreach (var saved in written)
                    changeFeed?.Publish(MeshChangeEvent.Created(saved));
            });

    /// <summary>
    /// Deletes <paramref name="path"/> via <paramref name="adapter"/> and publishes a
    /// <see cref="MeshChangeEvent.Deleted"/> on <paramref name="changeFeed"/> after the
    /// delete emits (post-commit). The deleted path flows through unchanged for further
    /// composition. <paramref name="nodeType"/> is the type-name to stamp on the event
    /// (helpful for subscribers that filter by type); if unknown, pass <c>null</c>.
    /// </summary>
    public static IObservable<string> DeleteAndPublish(
        this IStorageAdapter adapter,
        string path,
        IMeshChangeFeed? changeFeed,
        string? nodeType = null)
        => adapter.Delete(path)
            .Do(p => changeFeed?.Publish(MeshChangeEvent.Deleted(p, nodeType)));
}
