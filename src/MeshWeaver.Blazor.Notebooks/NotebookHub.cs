using System.Collections.Concurrent;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Notebooks;
using Microsoft.AspNetCore.SignalR;

namespace MeshWeaver.Blazor.Notebooks;
// In MeshWeaver.Portal project
// NotebookHub.cs
public class NotebookHub(IRoutingService routingService) : Hub
{
    private ConcurrentDictionary<(string addressType, string id), IDisposable> disposables = new();

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

    public override async Task OnDisconnectedAsync(Exception? exception)
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
