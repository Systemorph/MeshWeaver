namespace OpenSmc.Messaging;

public class RoutePlugin : MessageHubPlugin<RoutePlugin>
{
    private readonly ForwardConfiguration forwardConfiguration;


    public RoutePlugin(IMessageHub hub, ForwardConfiguration forwardConfiguration, IMessageHub parentHub) : base(hub)
    {
        if (parentHub != null)
            forwardConfiguration = forwardConfiguration with { Handlers = forwardConfiguration.Handlers.Add(d => Task.FromResult(ForwardToParent(Hub, parentHub, d))) };
        this.forwardConfiguration = forwardConfiguration;

        Register(ForwardMessageAsync);
    }
    private static IMessageDelivery ForwardToParent(IMessageHub hub, IMessageHub parentHub, IMessageDelivery delivery)
    {
        parentHub.DeliverMessage(delivery);
        return delivery.Forwarded();
    }

    public override bool Filter(IMessageDelivery d) => d.State == MessageDeliveryState.Submitted && d.Target != null && !d.Target.Equals(Hub.Address);

    /// <summary>
    /// Loops through forward rules in a sequence. Each forward rule either applies and returns delivery.Forwarded() or doesn't apply and returns delivery.
    /// </summary>
    /// <param name="delivery"></param>
    /// <returns></returns>
    private async Task<IMessageDelivery> ForwardMessageAsync(IMessageDelivery delivery)
    {
        foreach (var handler in forwardConfiguration.Handlers)
            delivery = await handler(delivery);

        return delivery;
    }

}