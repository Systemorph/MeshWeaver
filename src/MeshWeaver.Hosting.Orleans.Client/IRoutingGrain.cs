using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Orleans.Client;

public interface IRoutingGrain : IGrainWithStringKey
{
    Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery);
}
