using OpenSmc.Messaging;

namespace OpenSmc.Application.Orleans;

public class ApplicationGrain : Grain, IApplicationGrain
{
    protected IMessageHub Hub { get; private set; }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);
        Hub = CreateHub();
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        await Hub.DisposeAsync();
        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    public Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery) => Task.FromResult(Hub.DeliverMessage(delivery));

    private IMessageHub CreateHub()
    {
        var address = ParseAddress(this.GetPrimaryKeyString());
        var hub = ServiceProvider.CreateMessageHub(address, conf =>
            conf
        );
        return hub;
    }

    private static ApplicationAddress ParseAddress(string serializedAddress)
    {
        // HACK V10: the real parsing should be here instead of this hardcoding (2024/04/15, Dmitry Kalabin)
        return new(TestApplication.Name, TestApplication.Environment);
    }
}
