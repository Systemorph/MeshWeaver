using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Orleans.Client
{
    public interface IMessageHubGrain : IGrainWithStringKey
    {
        public Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery);
    }
}
