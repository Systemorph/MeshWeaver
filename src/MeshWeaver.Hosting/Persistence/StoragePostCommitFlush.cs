using System;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

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
}
