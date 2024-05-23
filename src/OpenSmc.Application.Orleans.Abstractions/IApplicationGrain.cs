using OpenSmc.Messaging;

namespace OpenSmc.Application.Orleans;

public interface IApplicationGrain : IGrainWithStringKey
{
    Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery);
}
