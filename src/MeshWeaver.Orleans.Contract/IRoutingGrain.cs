using MeshWeaver.Messaging;
using Orleans;

namespace MeshWeaver.Orleans.Contract;

public interface IRoutingService
{
    Task<IMessageDelivery> DeliverMessage(IMessageDelivery request);
}
