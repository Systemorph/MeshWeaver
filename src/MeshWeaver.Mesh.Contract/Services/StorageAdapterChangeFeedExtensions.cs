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
/// <see cref="IDataChangeNotifier"/> notifications, etc., knowing the
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
