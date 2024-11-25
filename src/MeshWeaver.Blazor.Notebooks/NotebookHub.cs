using System.Collections.Concurrent;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Notebooks;
using Microsoft.AspNetCore.SignalR;

namespace MeshWeaver.Blazor.Notebooks;
// In MeshWeaver.Portal project
public class NotebookHub(IRoutingService routingService) : Hub
{
    private ConcurrentDictionary<object,IDisposable> disposables = new();

    public async Task Register(object address)
    {
        if(address is not NotebookAddress notebookAddress)
            throw new ArgumentException("Invalid address type", nameof(address));
        var caller = Clients.Caller;
        var disposable = await routingService
            .RegisterRouteAsync(
                typeof(NotebookAddress).FullName, 
                notebookAddress.Id, 
            async (delivery, ct) =>
        {
            await caller.SendAsync("ReceiveMessage", delivery, cancellationToken: ct);
            return delivery.Forwarded();
        });
        disposables.TryAdd(address, disposable);
    }

    public Task Unregister(object address)
    {
        if (disposables.TryRemove(address, out var disposable))
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
