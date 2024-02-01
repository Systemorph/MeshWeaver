namespace OpenSmc.Messaging;

public class RoutePlugin : MessageHubPlugin<RoutePlugin>
{
    private readonly ForwardConfiguration forwardConfiguration;
    private readonly IMessageHub parentHub;


    public RoutePlugin(IMessageHub hub, ForwardConfiguration forwardConfiguration, IMessageHub parentHub) : base(hub)
    {
        this.forwardConfiguration = forwardConfiguration;
        this.parentHub = parentHub;

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
        foreach (var handler in forwardConfiguration.Handlers)
            delivery = await handler(delivery);

        if (delivery.State != MessageDeliveryState.Submitted || delivery.Target == null || Hub.Address.Equals(delivery.Target))
            return delivery;


        return RouteAlongHostingHierarchy(delivery, delivery.Target, null);
    }
    private IMessageDelivery RouteAlongHostingHierarchy(IMessageDelivery delivery, object address, object hostedSegment)
    {
        if (Hub.Address.Equals(address))
            // TODO V10: This works only if the hub has been instantiated before. Consider re-implementing setting hosted hub configs at config time. (31.01.2024, Roland Bürgi)
            return Hub.GetHostedHub(hostedSegment, null).DeliverMessage(delivery);

        if (address is IHostedAddress hosted)
            return RouteAlongHostingHierarchy(delivery, hosted.Host, address);


        if (parentHub == null)
            return delivery.NotFound();

        parentHub.DeliverMessage(delivery);
        return delivery.Forwarded();
    }

}