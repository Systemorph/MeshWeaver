using MeshWeaver.Messaging;

namespace MeshWeaver.Connection.Orleans;

public interface IRoutingGrain : IGrainWithStringKey
{
    /// <summary>
    /// Routes the delivery in the mesh: resolves the path on the silo side,
    /// then dispatches to the per-node hub grain (or memory stream for portal/
    /// client targets).
    /// </summary>
    Task<IMessageDelivery> RouteMessage(IMessageDelivery delivery);
}
