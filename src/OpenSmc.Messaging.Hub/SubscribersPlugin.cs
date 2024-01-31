namespace OpenSmc.Messaging;

public class SubscribersPlugin : MessageHubPlugin<SubscribersPlugin>
{
    private readonly HashSet<object> subscribers = new();
    private readonly HashSet<object> subscriptions = new();

    public SubscribersPlugin(IMessageHub hub) : base(hub)
    {
        Register(ForwardMessage);
    }

    public override bool Filter(IMessageDelivery d) => true;

    private static readonly HashSet<object> ReservedAddresses = [MessageTargets.Subscribers, MessageTargets.Subscriptions];
    private IMessageDelivery ForwardMessage(IMessageDelivery delivery)
    {
        if (delivery.Target is MessageTargets.Subscribers)
            return Forward(delivery, subscribers.ToArray());
        if (delivery.Target is MessageTargets.Subscriptions)
            return Forward(delivery, subscriptions.ToArray());

        var usSending = delivery.Sender == null || Hub.Address.Equals(delivery.Sender);
        var sentToUs = delivery.Target == null || Hub.Address.Equals(delivery.Target);

        if (sentToUs)
        {
            if (!usSending && !subscriptions.Contains(delivery.Sender))
                 subscribers.Add(delivery.Sender);

            return delivery;
        }

        if (!subscribers.Contains(delivery.Target))
            subscriptions.Add(delivery.Target);

        return delivery;
    }

    private IMessageDelivery Forward(IMessageDelivery delivery, object[] addresses)
    {
        foreach (var forwardAddress in addresses)
            Hub.DeliverMessage(delivery.WithRoutedTarget(forwardAddress)); // TODO V10: order will be broken because handling will be scheduled (23.01.2024, Alexander Yolokhov)
        return delivery.Forwarded();
    }
}