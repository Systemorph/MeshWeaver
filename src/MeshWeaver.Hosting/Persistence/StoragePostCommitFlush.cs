using System;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// <see cref="IPostCommitFlush"/> for MeshNode per-node hubs: flushes the committed
/// MeshNode to the hub's <see cref="IStorageAdapter"/> so the patch handler's
/// <c>PatchDataResponse</c> ack guarantees durability (read-after-write). Mirrors the
/// commit → persist → respond shape the deleted <c>UpdateNodeRequest</c> handler used
/// (its <c>WriteAndPublishUpdated</c> chained the Ok response off the storage write).
/// Resolves <see cref="IStorageAdapter"/> lazily from the hub so partitioned routing
/// (<c>PersistenceService</c>) sends the write to the node's own partition.
/// No-ops for non-MeshNode entities (other data hubs reuse the generic patch path).
/// </summary>
internal sealed class StoragePostCommitFlush(IMessageHub hub) : IPostCommitFlush
{
    public IObservable<bool> Flush(object committed)
    {
        if (committed is not MeshNode node)
            return Observable.Return(true);

        // 🚨 The root mesh hub's own node is transient infrastructure, NOT an addressable mesh node
        // — its `mesh/{id}` id is a fresh guid every process, so persisting it just orphans a row on
        // each restart ("puke standing"). The mesh hub is routable (registered with the routing
        // service like portal/), but must never hit storage — same as portals, which route yet
        // persist nothing. Log an error (so a future writer that commits it is visible in the logs)
        // and skip: the ack still resolves true, so the read-after-write contract is unaffected.
        if (hub.Address.Type == AddressExtensions.MeshType)
        {
            hub.ServiceProvider.GetService<ILogger<StoragePostCommitFlush>>()?.LogError(
                "Refused to persist the root mesh hub's own node {Path}: a mesh/{{id}} address is transient " +
                "infrastructure, not an addressable node, and must never be committed. Investigate the writer.",
                node.Path);
            return Observable.Return(true);
        }

        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null)
            return Observable.Return(true);

        // Persist AND publish a MeshChangeKind.Updated event to IMeshChangeFeed —
        // exactly what the deleted handler's WriteAndPublishUpdated did. The publish
        // drives the Workspace's _remoteStreamCache eviction (so a fresh GetRemoteStream
        // after the update sees the new snapshot, not a cached pre-update one) and
        // refreshes synced-query providers.
        var changeFeed = hub.ServiceProvider.GetService<IMeshChangeFeed>();
        return storage.WriteAndPublishUpdated(node, hub.JsonSerializerOptions, changeFeed)
            .Select(_ => true)
            .DefaultIfEmpty(true);
    }

    // Feed-only publish for the MeshNode cross-hub ATOMIC apply (ApplyMeshNodePatchAtomic),
    // which persists off-turn via DataSourceWithStorage.Synchronize and so must NOT call Flush
    // (that would double-write). The atomic path dropped the post-commit Flush to keep the ack
    // emit-onstart — but Flush was ALSO what published the Updated event that evicts the
    // Workspace's _remoteStreamCache. Without this, a fresh subscriber after a cross-hub MeshNode
    // update reads a stale cached snapshot (WorkspaceCacheEviction.NewSubscriber_AfterUpdate).
    // A plain Subject.OnNext — no IO, no re-entrancy — so it never reintroduces the atioz wedge.
    public void PublishUpdated(object committed)
    {
        if (committed is not MeshNode node)
            return;
        hub.ServiceProvider.GetService<IMeshChangeFeed>()?.Publish(MeshChangeEvent.Updated(node));
    }
}
