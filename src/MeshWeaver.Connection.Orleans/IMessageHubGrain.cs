using MeshWeaver.Messaging;

namespace MeshWeaver.Connection.Orleans
{
    public interface IMessageHubGrain : IGrainWithStringKey
    {
        Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery);
    }
}
