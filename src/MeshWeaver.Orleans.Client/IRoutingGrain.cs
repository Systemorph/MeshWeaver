using MeshWeaver.Messaging;
using Orleans;

namespace MeshWeaver.Orleans.Client
{
    public interface IRoutingGrain : IGrainWithStringKey
    {
        Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery);
    }
}
