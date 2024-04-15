using OpenSmc.Messaging;

namespace OpenSmc.Application.Orleans;

public class ApplicationGrain : Grain, IApplicationGrain
{
    public Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
    {
        throw new NotImplementedException();
    }
}
