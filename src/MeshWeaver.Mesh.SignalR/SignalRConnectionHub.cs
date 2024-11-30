using System.Collections.Concurrent;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR;

namespace MeshWeaver.Mesh.SignalR.Server;
public class SignalRConnectionHub(IRoutingService routingService) : Hub
{
    public const string UrlPattern = "/connection/{addressType}/{id}";
    private readonly ConcurrentDictionary<(string addressType, string id), IDisposable> disposables = new();

    public override async Task OnConnectedAsync()
    {
        var context = Context.GetHttpContext();
        var addressType = context?.Request.RouteValues["addressType"]?.ToString();
        var id = context?.Request.RouteValues["id"]?.ToString();

        if (string.IsNullOrEmpty(addressType) || string.IsNullOrEmpty(id))
        {
            throw new HubException("addressType and id are required query parameters");
        }

        var caller = Clients.Caller;
        var disposable = await routingService
            .RegisterRouteAsync(
                addressType,
                id,
                async (delivery, ct) =>
                {
                    await caller.SendAsync("ReceiveMessage", delivery, cancellationToken: ct);
                    return delivery.Forwarded();
                });

        disposables.TryAdd((addressType, id), disposable);
        await base.OnConnectedAsync();
    }

    public void DeliverMessage(IMessageDelivery delivery)
    {
        routingService.DeliverMessage(delivery);
    }

    public override async Task OnDisconnectedAsync(Exception exception)
    {
        var context = Context.GetHttpContext();
        var addressType = context?.Request.Query["addressType"].ToString();
        var id = context?.Request.Query["id"].ToString();

        if (addressType != null && id != null)
        {
            if (disposables.TryRemove((addressType, id), out var disposable))
            {
                disposable.Dispose();
            }
        }

        await base.OnDisconnectedAsync(exception);
    }
}
