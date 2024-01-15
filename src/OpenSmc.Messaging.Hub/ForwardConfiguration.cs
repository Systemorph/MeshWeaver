using System.Collections.Immutable;

namespace OpenSmc.Messaging.Hub;

public record ForwardConfiguration(AsyncDelivery Route, IMessageHub Hub, object Address)
{


    internal ImmutableList<IForwardConfigurationItem> Items { get; init; } = ImmutableList<IForwardConfigurationItem>.Empty;
    public ForwardConfiguration WithForwardToTarget<TMessage>(object address, Func<ForwardConfigurationItem<TMessage>, ForwardConfigurationItem<TMessage>> config = null)
        => WithForward(d => Route(d.ForwardTo(address)), config);
    public ForwardConfiguration WithForwardToTarget<TMessage>(Func<IMessageDelivery<TMessage>, object> addressMap, Func<ForwardConfigurationItem<TMessage>, ForwardConfigurationItem<TMessage>> config = null)
        => WithForward(d => Route(d.ForwardTo(addressMap(d))), config);

    public ForwardConfiguration WithForward<TMessage>(SyncDelivery<TMessage> route, Func<ForwardConfigurationItem<TMessage>, ForwardConfigurationItem<TMessage>> setup = null)
    {
        var item = (setup ?? (y => y))(new()
        {
            Route = d => Task.FromResult(route((IMessageDelivery<TMessage>)d)),
        });
        return this with
        {
            Items = Items.Add(item)
        };

    }
    public ForwardConfiguration WithForward<TMessage>(AsyncDelivery<TMessage> route, Func<ForwardConfigurationItem<TMessage>, ForwardConfigurationItem<TMessage>> setup = null)
        => this with
        {
            Items = Items.Add((setup ?? (y => y))(new()
            {
                Route = d => route((IMessageDelivery<TMessage>)d)
            }))
        };


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

