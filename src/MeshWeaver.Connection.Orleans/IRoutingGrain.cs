using MeshWeaver.Messaging;

namespace MeshWeaver.Connection.Orleans;

public interface IRoutingGrain : IGrainWithStringKey
{
    Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery);

}
