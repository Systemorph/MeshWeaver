using System.Collections.Concurrent;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Notebooks;
using Microsoft.AspNetCore.SignalR;

namespace MeshWeaver.Blazor.Notebooks;
// In MeshWeaver.Portal project
public class NotebookHub(IRoutingService routingService) : Hub
{
    private ConcurrentDictionary<(string addressType, string id),IDisposable> disposables = new();

    public async Task Register(string addressType, string id)
    {
        var caller = Clients.Caller;
        var disposable = await routingService
            .RegisterRouteAsync(
                typeof(NotebookAddress).FullName, 
                id, 
            async (delivery, ct) =>
        {
            await caller.SendAsync("ReceiveMessage", delivery, cancellationToken: ct);
            return delivery.Forwarded();
        });
        disposables.TryAdd((addressType, id), disposable);
    }

    public Task Unregister(string addressType, string id)
    {
        if (disposables.TryRemove((addressType, id), out var disposable))
        {
            disposable.Dispose();
        }
        return Task.CompletedTask;
    }
    
    public override Task OnDisconnectedAsync(Exception exception)
    {
        foreach (var disposable in disposables.Values)
        {
            disposable.Dispose();
        }
        disposables.Clear();
        return base.OnDisconnectedAsync(exception);
    }
}
