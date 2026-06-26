using MeshWeaver.Messaging;

namespace MeshWeaver.Connection.Orleans;

/// <summary>
/// Single silo-side routing grain that resolves a delivery's target path within the
/// mesh and dispatches it to the appropriate per-node hub grain (or memory stream for
/// portal/client targets).
/// </summary>
public interface IRoutingGrain : IGrainWithStringKey
{
    /// <summary>
    /// Routes the delivery in the mesh: resolves the path on the silo side,
    /// then dispatches to the per-node hub grain (or memory stream for portal/
    /// client targets).
    /// </summary>
    Task<IMessageDelivery> RouteMessage(IMessageDelivery delivery);
}
