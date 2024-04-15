using OpenSmc.Messaging;

namespace OpenSmc.Application.Orleans;

public interface IApplicationGrain
{
    Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery);
}
