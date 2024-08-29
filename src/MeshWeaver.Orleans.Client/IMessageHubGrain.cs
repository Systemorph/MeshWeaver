using MeshWeaver.Messaging;
using Orleans;

namespace MeshWeaver.Orleans.Client
{
    public interface IMessageHubGrain : IGrainWithStringKey
    {
        public Task<IMessageDelivery> DeliverMessage(IMessageDelivery request);
    }
}
