using MeshWeaver.Messaging;

namespace MeshWeaver.Connection.Orleans;

public interface IRoutingGrain : IGrainWithStringKey
{
    Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery);
    Task RegisterStream(Address address, string streamProvider, string streamNamespace);
    Task UnregisterStream(Address address);

}
