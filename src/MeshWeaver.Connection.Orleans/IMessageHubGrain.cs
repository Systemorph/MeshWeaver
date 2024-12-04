using MeshWeaver.Messaging;

namespace MeshWeaver.Connection.Orleans
{
    public interface IMessageHubGrain : IGrainWithStringKey
    {
        public Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery);
    }
}
