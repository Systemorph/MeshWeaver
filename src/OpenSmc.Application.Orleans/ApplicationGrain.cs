using OpenSmc.Messaging;

namespace OpenSmc.Application.Orleans;

public class ApplicationGrain : Grain, IApplicationGrain
{
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    public Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
    {
        throw new NotImplementedException();
    }
}
