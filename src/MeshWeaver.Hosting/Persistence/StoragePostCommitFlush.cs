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
        // 🚨 THIS is the ONE durable write for a patch-driven own-node change (#1249). Record the
        // version it made durable so the per-node persistence sampler — which sees the very same
        // commit and would otherwise post a SECOND, unordered write of it — drops the duplicate in
        // MeshDataSource's SaveMeshNodeRequest handler. Recorded only on the write's EMISSION, so a
        // flush that failed leaves the sampler as the writer of record; recorded at the version the
        // store reports durable, which is HIGHER than ours when the backend's version-conditional
        // upsert kept a newer row. See PostCommitFlushRegistry for the whole mechanism.
        // 🚨 A null emission is the try-then-claim sentinel — "this adapter does not own this path",
        // NOT a successful write (the same reason WriteAndPublishUpdated skips its feed publish on
        // null). Recording it would suppress the sampler's write for a row nobody persisted, which
        // would turn a duplicate-write bug into a lost-write one.
        var flushed = hub.ServiceProvider.GetService<PostCommitFlushRegistry>();

        // 🚨 CLAIM BEFORE THE WRITE, confirm after (#1557). Recording only on the emission left the
        // suppression TIMED rather than ordered: the sampler's queued SaveMeshNodeRequest can reach
        // its handler while this round-trip is still in flight, read a mark that has not been raised
        // yet, and write — landing as a version regression whose base-less merge keeps the string
        // SUPERSET and re-adds deleted text. Measured at ~4% on the 2-vCPU runner, and each
        // occurrence is a real resurrection, not just a red test.
        //
        // The claim is provisional and is RELEASED on every path that does not persist, so a route
        // that does not write can never suppress the one that would.
        flushed?.Claim(node.Path, node.Version);
        var resolved = false;

        return storage.WriteAndPublishUpdated(node, hub.JsonSerializerOptions, changeFeed)
            .Do(
                saved =>
                {
                    if (saved is not null)
                    {
                        // Persisted — Record confirms the claim and raises the mark to whatever the
                        // store reports durable (higher than ours when its version-conditional
                        // upsert kept a newer row).
                        resolved = true;
                        flushed?.Record(node.Path, Math.Max(node.Version, saved.Version));
                    }
                    else
                    {
                        // The null try-then-claim sentinel — "this adapter does not own this path".
                        // NOT a successful write, so the sampler must stay the writer of record.
                        resolved = true;
                        flushed?.Release(node.Path, node.Version);
                    }
                })
            // 🚨 THE CLAIM MUST ALWAYS BE ANSWERED. Finally runs on completion, on error AND on
            // UNSUBSCRIPTION — the last of which is the one an OnError/OnCompleted pair misses: if
            // the patch pipeline's subscription is torn down mid-write (hub teardown, a cancelled
            // round), no terminal notification ever fires, the claim is never resolved, and the
            // sampler that deferred against it waits forever on a signal nobody will send. A
            // deferred write must never outlive the thing it is deferring to.
            //
            // Releasing an unresolved claim is the FAIL-SAFE direction: the sampler goes ahead and
            // writes. The other direction would drop a write nobody else is making.
            .Finally(() =>
            {
                if (resolved) return;
                resolved = true;
                flushed?.Release(node.Path, node.Version);
            })
            .Select(_ => true)
            .DefaultIfEmpty(true);
    }

    // Feed-only publish for a write path that persists by SOME OTHER route and so must not call
    // Flush (that would double-write) while still needing the Updated event that evicts the
    // Workspace's _remoteStreamCache — without it a fresh subscriber after a cross-hub MeshNode
    // update reads a stale cached snapshot (WorkspaceCacheEviction.NewSubscriber_AfterUpdate).
    // A plain Subject.OnNext — no IO, no re-entrancy — so it never reintroduces the prod wedge.
    // 🚨 No callers today: the cross-hub atomic apply chains Flush and acks off the durable write.
    // See IPostCommitFlush.PublishUpdated for why that must stay the arrangement (#1249).
    public void PublishUpdated(object committed)
    {
        if (committed is not MeshNode node)
            return;
        hub.ServiceProvider.GetService<IMeshChangeFeed>()?.Publish(MeshChangeEvent.Updated(node));
    }
}
