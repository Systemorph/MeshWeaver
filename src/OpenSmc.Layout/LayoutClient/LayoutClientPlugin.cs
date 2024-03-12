using OpenSmc.Messaging;

namespace OpenSmc.Layout.LayoutClient;

public class LayoutClientPlugin(LayoutClientConfiguration configuration, IMessageHub hub)
    : MessageHubPlugin<LayoutClientState>(hub)
{
    public override bool IsDeferred(IMessageDelivery delivery) 
        => delivery.Message is RefreshRequest
            || base.IsDeferred(delivery);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
        InitializeState(new(configuration));
        Hub.Post(configuration.RefreshRequest, o => o.WithTarget(State.Configuration.LayoutHostAddress));
    }


}

