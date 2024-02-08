namespace OpenSmc.Messaging;

public class RoutePlugin : MessageHubPlugin<RouteConfiguration>
{
    private readonly IMessageHub parentHub;


    public RoutePlugin(IMessageHub hub, RouteConfiguration routeConfiguration, IMessageHub parentHub) : base(hub)
    {
        this.parentHub = parentHub;
        InitializeState(routeConfiguration);

        Register(RouteMessageAsync);
    }

    public override bool Filter(IMessageDelivery d) => d.State == MessageDeliveryState.Submitted;

    /// <summary>
    /// Loops through forward rules in a sequence. Each forward rule either applies and returns delivery.Forwarded() or doesn't apply and returns delivery.
    /// </summary>
    /// <param name="delivery"></param>
    /// <returns></returns>
    private async Task<IMessageDelivery> RouteMessageAsync(IMessageDelivery delivery)
    {
        // TODO V10: This should probably also react upon disconnect. (02.02.2024, Roland Bürgi)
        if (State.RoutedMessageAddresses.TryGetValue(delivery.Sender, out var originalSenders))
        {
            foreach (var originalSender in originalSenders)
            {
                var delivery1 = delivery;
                Hub.Post(delivery.Message, o => o.WithTarget(originalSender).WithProperties(delivery1.Properties));
            }

        }

        foreach (var handler in State.Handlers)
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