using System.Collections.Concurrent;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR;

namespace MeshWeaver.Mesh.SignalR.Server;

public class TestHub : Hub
{
    public async Task SendMessage(string user, string message)
    {
        await Clients.All.SendAsync("ReceiveMessage", user, message);
    }

    public async Task<MeshConnection> Connect(string addressType, string id)
    {
        return new(addressType, id);
    }
}
    public class SignalRConnectionHub(IRoutingService routingService) : Hub
{
    private readonly ConcurrentDictionary<(string addressType, string id), MeshConnection> connections = new();


    public async Task<MeshConnection> Connect(string addressType, string id)
    {
        if (string.IsNullOrEmpty(addressType) || string.IsNullOrEmpty(id))
        {
            throw new HubException("addressType and id are required query parameters");
        }

        var caller = Clients.Caller;
        var route = await routingService
            .RegisterRouteAsync(
                addressType,
                id,
                async (delivery, ct) =>
                {
                    await caller.SendAsync("ReceiveMessage", delivery, cancellationToken: ct);
                    return delivery.Forwarded();
                });


        var connection = new MeshConnection(addressType, id);
        connection.WithDisposeAction(() => route.Dispose());
        connections.TryAdd((addressType, id), connection);
        return connection;
    }

    public Task DeliverMessage(IMessageDelivery delivery)
    {
        routingService.DeliverMessage(delivery);
        return Task.CompletedTask;
    }

    public override async Task OnDisconnectedAsync(Exception exception)
    {
        var context = Context.GetHttpContext();
        var addressType = context?.Request.Query["addressType"].ToString();
        var id = context?.Request.Query["id"].ToString();

        if (addressType != null && id != null)
        {
            if (connections.TryRemove((addressType, id), out var disposable))
            {
                disposable.Dispose();
            }
        }

        await base.OnDisconnectedAsync(exception);
    }
}
