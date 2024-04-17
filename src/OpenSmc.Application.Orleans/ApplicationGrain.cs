using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Messaging;

namespace OpenSmc.Application.Orleans;

public class ApplicationGrain : Grain, IApplicationGrain
{
    protected IMessageHub Hub { get; private set; }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await base.OnActivateAsync(cancellationToken);
        Hub = ServiceProvider.GetRequiredService<IMessageHub>();
        //Hub = CreateHub();
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        //await Hub.DisposeAsync();
        await base.OnDeactivateAsync(reason, cancellationToken);
    }

    public Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery) => Task.FromResult(Hub.DeliverMessage(FixAddresses(delivery)));

    // HACK V10: this is here as a temporal workaround for deserialization issues and should be removed as soon as we fix deserialization (2024/04/17, Dmitry Kalabin)
    private static IMessageDelivery FixAddresses(IMessageDelivery delivery) 
        => (MessageDelivery)delivery with { Sender = new UiAddress(TestUiIds.HardcodedUiId), Target = new ApplicationAddress(TestApplication.Name, TestApplication.Environment), };

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
