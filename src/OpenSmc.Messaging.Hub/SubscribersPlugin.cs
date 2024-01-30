namespace OpenSmc.Messaging;

public class SubscribersPlugin : MessageHubPlugin<SubscribersPlugin>
{
    private readonly HashSet<object> subscribers = new();
    private readonly HashSet<object> subscribedTo = new();

    public SubscribersPlugin(IServiceProvider serviceProvider, IMessageHub hub) : base(hub)
    {
        Register(ForwardMessageAsync);
    }

    public override bool Filter(IMessageDelivery d) => true;

    private Task<IMessageDelivery> ForwardMessageAsync(IMessageDelivery delivery)
    {
        var weSending = delivery.Sender == null || Hub.Address.Equals(delivery.Sender);
        var sentToUs = delivery.Target == null || Hub.Address.Equals(delivery.Target);

        if (weSending && sentToUs)
            return Task.FromResult(delivery);

        if (weSending && !MessageTargets.Subscribers.Equals(delivery.Target))
        {
            subscribedTo.Add(delivery.Target);
        }

        if (sentToUs && delivery.Sender != null && !subscribedTo.Contains(delivery.Sender))
        {
            subscribers.Add(delivery.Sender);
        }

        if (MessageTargets.Subscribers.Equals(delivery.Target))
        {
            foreach (var forwardAddress in subscribers.ToArray())
            {
                Hub.DeliverMessage(delivery.WithRoutedTarget(forwardAddress)); // TODO V10: order will be broken because handling will be scheduled (23.01.2024, Alexander Yolokhov)
            }
        }

        return Task.FromResult(delivery);
    }

}