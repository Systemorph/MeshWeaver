using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Holder for an own-MeshNode source observable supplied by the routing layer
/// (Orleans <c>MessageHubGrain</c> / Monolith <c>MonolithRoutingService</c>) so
/// the per-node hub's MeshDataSource can seed its workspace from the already-
/// loaded node — and follow subsequent emissions — instead of issuing a
/// duplicate persistence read on init. Stored in <see cref="MessageHubConfiguration"/>
/// via type-keyed <c>config.Set(holder)</c>; consumed by MeshNodeTypeSource.
/// </summary>
public sealed record OwnNodeStreamHolder(IObservable<MeshNode?> Stream);

/// <summary>
/// Extension to attach the routing-supplied own-MeshNode stream to a hub
/// configuration. The per-node hub's MeshDataSource reads this on init and
/// uses it as the source of truth — skipping the duplicate persistence read
/// it would otherwise do, and propagating subsequent emissions from the
/// routing layer (catalog stream / cluster sync) directly into the workspace.
/// Items may be null when the routing layer signals "no node at this path
/// right now" — MeshNodeTypeSource treats null emissions as a no-op seed.
/// </summary>
public static class OwnNodeStreamExtensions
{
    public static MessageHubConfiguration WithOwnNodeStream(
        this MessageHubConfiguration config,
        IObservable<MeshNode?> ownNodeStream)
        => config.Set(new OwnNodeStreamHolder(ownNodeStream));
}
