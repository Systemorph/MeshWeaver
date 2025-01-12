using System.Collections.Concurrent;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR;

namespace MeshWeaver.Hosting.SignalR;

public class SignalRConnectionHub(IMessageHub hub, IRoutingService routingService) : Hub
{
    public const string EndPoint = "signalr";
    private readonly ConcurrentDictionary<Address, string> connections = new();
    private readonly IRoutingService routingService = routingService;

    public void Connect(Address address)
    {
        routingService.RegisterStreamAsync(address, (d,ct) => SendMessageToClient(d, Context.ConnectionId, ct));

        connections[address] = Context.ConnectionId;
    }

    private async Task<IMessageDelivery> SendMessageToClient(IMessageDelivery delivery, string connection, CancellationToken ct)
    {
        await Clients.Client(connection)
            .SendAsync("ReceiveMessage", delivery, ct);
        return delivery.Forwarded();
    }

    public void DeliverMessage(IMessageDelivery delivery)
    {
        hub.DeliverMessage(delivery);
    }

}
