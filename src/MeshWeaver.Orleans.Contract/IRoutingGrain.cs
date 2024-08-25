using MeshWeaver.Messaging;
using Orleans;

namespace MeshWeaver.Orleans.Contract
{
    public interface IRoutingGrain : IGrainWithStringKey
    {
        Task<IMessageDelivery> DeliverMessage(object routeAddress, IMessageDelivery request);
    }
}
