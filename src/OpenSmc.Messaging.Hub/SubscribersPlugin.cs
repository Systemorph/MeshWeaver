using Microsoft.Extensions.DependencyInjection;

namespace OpenSmc.Messaging;

public record MessageHubConnections
{
    public HashSet<object> Subscribers { get; } = new();
    public HashSet<object> Subscriptions { get; } = new();
}

public class SubscribersPlugin : MessageHubPlugin<SubscribersPlugin, MessageHubConnections>
{

    public SubscribersPlugin(IMessageHub hub) : base(hub)
    {
        Register(ForwardMessage);
    }

    public override async Task StartAsync()
    {
        await base.StartAsync();
        InitializeState(Hub.ServiceProvider.GetRequiredService<MessageHubConnections>());
    }

    public override bool Filter(IMessageDelivery d) => true;

    private IMessageDelivery ForwardMessage(IMessageDelivery delivery)
    {
        var usSending = delivery.Sender == null || Hub.Address.Equals(delivery.Sender);
        var sentToUs = delivery.Target == null || Hub.Address.Equals(delivery.Target);
        switch (delivery)
        {
            case { Target: MessageTargets.Subscribers }:
                if (!usSending)
                    throw new RoutingException("Trying to send to subscribers from outside hub.");
                return Forward(delivery, State.Subscribers.ToArray());

            case { Target: MessageTargets.Subscriptions }:
                if (!usSending)
                    throw new RoutingException("Trying to send to subscribers from outside hub.");

                return Forward(delivery, State.Subscriptions.ToArray());

            case { Message: DisconnectHubRequest }:
                return HandleDisconnect(delivery);


        }


        if (sentToUs)
        {
            if (!usSending && !State.Subscriptions.Contains(delivery.Sender))
                State.Subscribers.Add(delivery.Sender);

            return delivery;
        }

        if (!State.Subscribers.Contains(delivery.Target))
            State.Subscriptions.Add(delivery.Target);

        return delivery;
    }

    private IMessageDelivery HandleDisconnect(IMessageDelivery delivery)
    {
        State.Subscribers.Remove(delivery.Sender);
        State.Subscriptions.Remove(delivery.Sender);
        return delivery.Processed();
    }

    private IMessageDelivery Forward(IMessageDelivery delivery, object[] addresses)
    {
        foreach (var forwardAddress in addresses)
            Hub.DeliverMessage(delivery.WithRoutedTarget(forwardAddress)); 
        return delivery.Forwarded();
    }
}