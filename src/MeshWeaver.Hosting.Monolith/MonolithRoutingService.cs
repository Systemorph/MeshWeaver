using System.Collections.Concurrent;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Monolith;

public class MonolithRoutingService(IMessageHub hub) : RoutingServiceBase(hub)
{
    private readonly ConcurrentDictionary<Address, AsyncDelivery> streams = new();


    public override Task Async(Address address)
    {
        streams.TryRemove(address, out _);
        return Task.FromResult<Address>(null);
    }


    public override Task RegisterStreamAsync(Address address, AsyncDelivery callback)
    {
        streams[address] = callback;
        return Task.CompletedTask;
    }


    protected override async Task<IMessageDelivery> RouteImpl(
        IMessageDelivery delivery, 
        MeshNode node, 
        Address address,
        CancellationToken cancellationToken)
    {
        if (streams.TryGetValue(address, out var stream))
            return await stream.Invoke(delivery, cancellationToken);

        var hub = CreateHub(node, address);
        if (hub is not null)
        {
            hub.DeliverMessage(delivery);
            return delivery.Forwarded(hub.Address);
        }

        return delivery;
    }

    private IMessageHub CreateHub(MeshNode node, Address address)
    {
        if (node.HubConfiguration is not null)
        {
            var hub = Mesh.GetHostedHub(address, node.HubConfiguration);
            hub.RegisterForDisposal((_, _) => Async(hub.Address));
            return hub;
        }
        return null;
    }
}
