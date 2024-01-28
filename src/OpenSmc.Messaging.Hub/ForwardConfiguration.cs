using System.Collections.Immutable;

namespace OpenSmc.Messaging;

public record ForwardConfiguration(IMessageHub Hub)
{

    internal ImmutableList<AsyncDelivery> Handlers { get; init; } = ImmutableList<AsyncDelivery>.Empty;



    public ForwardConfiguration RouteAddressToHub<TAddress>(Func<IMessageDelivery, IMessageHub> hubFactory) =>
        RouteAddress<TAddress>(d => hubFactory(d).DeliverMessage(d));

    public ForwardConfiguration RouteAddress<TAddress>(Action<IMessageDelivery> handler) =>
        RouteAddress<TAddress>(d =>
        {
            handler(d);
            return Task.CompletedTask;
        });
        
    public ForwardConfiguration RouteAddress<TAddress>(Func<IMessageDelivery, Task> handler)
        => this with
        {
            Handlers = Handlers.Add(async delivery =>
            {
                if (delivery.State != MessageDeliveryState.Submitted || delivery.Target is not TAddress)
                    return delivery;
                await handler(delivery);
                // TODO: should we take care of result from handler somehow?
                return delivery.Forwarded();
            }
            ),
        };

    public ForwardConfiguration RouteAddressToHostedHub<TAddress>(Func<MessageHubConfiguration, MessageHubConfiguration> configuration)
        => RouteAddress<TAddress>(d => Hub.GetHostedHub((TAddress)d.Target, configuration));
}



public interface IForwardConfigurationItem
{

    AsyncDelivery Route { get; }
    bool Filter(IMessageDelivery delivery);
}
public record ForwardConfigurationItem<TMessage> : IForwardConfigurationItem
{


    AsyncDelivery IForwardConfigurationItem.Route => d => Route((IMessageDelivery<TMessage>)d);


    bool IForwardConfigurationItem.Filter(IMessageDelivery delivery)
    {
        if (delivery.Message is not TMessage)
            return false;
        return Filter?.Invoke((IMessageDelivery<TMessage>)delivery) ?? true;
    }


    internal AsyncDelivery Route { get; init; }


    public ForwardConfigurationItem<TMessage> WithFilter(DeliveryFilter<TMessage> filter) => this with { Filter = filter };
    internal DeliveryFilter<TMessage> Filter { get; init; }

    public ForwardConfigurationItem<TMessage> WithInheritorsFromAssembliesOf(params Type[] types)
    {
        return this with { InheritorsFrom = types };
    }

    public ForwardConfigurationItem<TMessage> WithInheritance(bool inherit = true) => this with { ForwardInheritors = inherit };
    internal bool ForwardInheritors { get; init; } = true;
    public IReadOnlyCollection<Type> InheritorsFrom { get; init; } = Array.Empty<Type>();

}

