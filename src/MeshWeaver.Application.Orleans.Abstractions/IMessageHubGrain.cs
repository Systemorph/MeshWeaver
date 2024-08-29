using MeshWeaver.Messaging;

namespace MeshWeaver.Application.Orleans;

public interface IMessageHubGrain : IGrainWithGuidKey
{
    public Task<IMessageDelivery> DeliverMessage(IMessageDelivery request);
}
