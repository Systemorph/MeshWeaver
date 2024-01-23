namespace OpenSmc.Messaging.Hub;

public class RoutePlugin : MessageHubPlugin<RoutePlugin>
{
    private readonly ForwardConfiguration forwardConfiguration;


    public RoutePlugin(IServiceProvider serviceProvider, ForwardConfiguration forwardConfiguration) : base(serviceProvider)
    {
        this.forwardConfiguration = forwardConfiguration;
        Register(ForwardMessageAsync);
    }

    public override bool Filter(IMessageDelivery d) => d.State == MessageDeliveryState.Submitted;

    /// <summary>
    /// Loops through forward rules in a sequence. Each forward rule either applies and returns delivery.Forwarded() or doesn't apply and returns delivery.
    /// </summary>
    /// <param name="delivery"></param>
    /// <returns></returns>
    private async Task<IMessageDelivery> ForwardMessageAsync(IMessageDelivery delivery)
    {
        foreach (var item in GetForwards(delivery))
            delivery = await item.Route(delivery);

        foreach (var handler in forwardConfiguration.Handlers)
        {
            delivery = await handler(delivery);
        }

        return delivery;
    }

    private IEnumerable<IForwardConfigurationItem> GetForwards(IMessageDelivery delivery)
    {
        return forwardConfiguration.Items.Where(f => f.Filter(delivery));
    }
}