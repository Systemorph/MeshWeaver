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

    private static ApplicationAddress ParseAddress(string serializedAddress)
    {
        // HACK V10: the real parsing should be here instead of this hardcoding (2024/04/15, Dmitry Kalabin)
        return new(TestApplication.Name, TestApplication.Environment);
    }
}
