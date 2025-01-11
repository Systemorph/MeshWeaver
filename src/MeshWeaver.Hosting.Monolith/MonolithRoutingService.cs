using System.Collections.Concurrent;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Monolith;

public class MonolithRoutingService(IMessageHub hub) : RoutingServiceBase(hub)
{
    private readonly ConcurrentDictionary<Address, AsyncDelivery> streams = new();


    public override Task Unregister(Address address)
    {
        streams.TryRemove(address, out _);
        return Task.FromResult<Address>(null);
    }


    public override Task RegisterStream(Address address, AsyncDelivery callback)
    {
        streams[address] = callback;
        return Task.CompletedTask;
    }


    private readonly ConcurrentDictionary<object, IMessageHub> hubs = new();
    protected override async Task<IMessageDelivery> RouteImpl(
        IMessageDelivery delivery, 
        MeshNode node, 
        Address address,
        CancellationToken cancellationToken)
    {
        if (streams.TryGetValue(address, out var stream))
            return await stream.Invoke(delivery, cancellationToken);

        if ((hubs.TryGetValue(delivery.Target, out var hub) 
                ? hub 
                : (hub = hubs[delivery.Target] = CreateHub(node, address.Type, address.Id))) is not null)
        {
            await hub.DeliverMessageAsync(delivery, cancellationToken);
            return delivery.Forwarded(hub.Address);
        }

        return delivery;
    }

    private IMessageHub CreateHub(MeshNode node, string addressType, string id)
    {
        if (node.HubFactory is not null)
        {
            var hub = node.HubFactory.Invoke(Mesh.ServiceProvider, addressType, id);
            hub.RegisterForDisposal((_, _) => Unregister(hub.Address));

        }
        return null;
    }
}
