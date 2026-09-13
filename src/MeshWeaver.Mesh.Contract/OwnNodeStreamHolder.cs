using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Holder for the own-MeshNode observable the routing layer (Orleans <c>MessageHubGrain</c> /
/// Monolith <c>MonolithRoutingService</c>) attaches at hub instantiation, so the per-node hub's
/// MeshDataSource can seed its workspace from the node the routing layer already resolved —
/// on Orleans the only source of the ENRICHED node, whose <c>HubConfiguration</c> delegate
/// storage cannot hold. Stored in <see cref="MessageHubConfiguration"/> via type-keyed
/// <c>config.Set(holder)</c>; consumed by MeshNodeTypeSource.
///
/// <para>🚨 <b>ONE-SHOT on both hosts: the resolved node, then completion.</b> It is not a
/// live-update channel and nothing may rely on it as one. Live changes to a hub's own node
/// reach the hub as the WRITES themselves (every write to a node routes to its owner), and
/// changes made in another process through <c>IMeshChangeFeed</c>. Orleans used to hand the
/// hub its activation source here — a <c>Merge</c> whose cache leg was the process-wide
/// mesh-node cache's live view of the hub's OWN path — and MeshNodeTypeSource kept a
/// lifetime subscription on it; the entry then never reached zero subscribers, its hydration
/// stream heart-beat the grain alive, and every node ever activated stayed resident (#3432,
/// <c>Doc/Architecture/AHubThatPinsItsOwnCacheEntry</c>). After activation that leg could only
/// echo the hub's own state, so nothing was lost by making it one-shot.</para>
/// </summary>
public sealed record OwnNodeStreamHolder(IObservable<MeshNode?> Stream);

/// <summary>
/// Attaches the routing-supplied own-MeshNode stream to a hub configuration. The per-node hub's
/// MeshDataSource reads it on init as the routing layer's already-resolved node (and, on
/// Orleans, the enriched one); the durable seed still comes from storage
/// (<c>Doc/Architecture/MeshNodeVersioning</c>). Items may be null when the routing layer
/// signals "no node at this path right now" — MeshNodeTypeSource treats null emissions as a
/// no-op seed. The stream completes after its one emission; see
/// <see cref="OwnNodeStreamHolder"/> for why it is not a live-update channel.
/// </summary>
public static class OwnNodeStreamExtensions
{
    /// <summary>
    /// Stashes the routing-supplied own-node stream on the hub configuration
    /// so <c>MeshNodeTypeSource</c> can seed the workspace from it instead of
    /// issuing a duplicate persistence read at init.
    /// </summary>
    public static MessageHubConfiguration WithOwnNodeStream(
        this MessageHubConfiguration config,
        IObservable<MeshNode?> ownNodeStream)
        => config.Set(new OwnNodeStreamHolder(ownNodeStream));
}
