using MeshWeaver.Messaging;

namespace MeshWeaver.Application.Orleans;

public interface IApplicationGrain : IGrainWithStringKey
{
    Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery);
}
