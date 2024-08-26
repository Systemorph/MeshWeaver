using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Contract;

public interface IRoutingService
{
    Task<IMessageDelivery> DeliverMessage(IMessageDelivery request);
    Task RegisterHubAsync(IMessageHub hub);
}
