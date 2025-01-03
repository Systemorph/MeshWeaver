using System.Collections.Concurrent;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.SignalR;

public class SignalRConnectionHub(IMessageHub hub) : Hub
{
    public const string EndPoint = "signalr";
    private readonly ConcurrentDictionary<(string addressType, string id), MeshConnection> connections = new();

    public async Task<MeshConnection> Connect(string address)
    {
        var split = address.Split('/');
        if (split.Length < 2)
            throw new HubException("Invalid address format");

        var addressType = split[0];
        var id = split[1];

        var routingService = hub.ServiceProvider.GetRequiredService<IRoutingService>();
        var caller = Clients.Caller;
        var key = (addressType, id);
        if (connections.TryRemove(key, out var conn))
            conn.Dispose();

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
        connections.TryAdd(key, connection);
        return connection;
    }

    public Task DeliverMessage(IMessageDelivery delivery)
    {
        hub.DeliverMessage(delivery);
        return Task.CompletedTask;
    }

    public override async Task OnDisconnectedAsync(Exception exception)
    {
        var context = Context.GetHttpContext();
        var addressType = context?.Request.Query["addressType"].ToString();
        var id = context?.Request.Query["id"].ToString();

        if (addressType != null)
        {
            if (connections.TryRemove((addressType, id), out var disposable))
            {
                disposable.Dispose();
            }
        }

        await base.OnDisconnectedAsync(exception);
    }
}
