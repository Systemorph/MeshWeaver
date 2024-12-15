using System.Collections.Concurrent;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.AspNetCore.SignalR;

namespace MeshWeaver.Hosting.SignalR;

public class KernelHub(IMessageHub hub) : Hub
{
    public const string EndPoint = "kernel";

    public void SubmitCommand(string kernelCommandEnvelope)
    {
        KernelCommandFromProxy(kernelCommandEnvelope);
    }

    public void KernelCommandFromProxy(string kernelCommandEnvelope)
    {
        PostToKernel(new KernelCommandEnvelope(kernelCommandEnvelope));
    }
    public void KernelEventFromProxy(string kernelEventEnvelope)
    {
        PostToKernel(new KernelEventEnvelope(kernelEventEnvelope));
    }

    private void PostToKernel(object message)
    {
        if (!callers.TryGetValue(Context.ConnectionId, out var tuple))
            throw new MeshException($"Could not find SignalR connection {Context.ConnectionId}");
        hub.Post(message, o => o.WithTarget(tuple.Address));
    }

    public override Task OnConnectedAsync()
    {
        var clientId = Context.ConnectionId;
        var kernelId = Guid.NewGuid().AsString();
        callers.TryAdd(clientId, (new KernelAddress{Id = kernelId}, hub.Register<KernelEventEnvelope>(async (d, ct) =>
                {
                    await Clients.Client(clientId).SendCoreAsync("kernelEvents", [d.Message.Envelope], ct);
                    return d.Processed();
                },
                d => d.Sender is KernelAddress ka && ka.Id == kernelId))
        );
;
        return Task.CompletedTask;
    }

    public async Task Connect()
    {
        
        await Clients.Caller.SendAsync("connected", true);
    }



    private bool isDisposing;
    private readonly object locker = new();
    private readonly ConcurrentDictionary<string, (KernelAddress Address,IDisposable Disposable)> callers = new();

    public override Task OnDisconnectedAsync(Exception exception)
    {
        if (callers.TryRemove(Context.ConnectionId, out var d))
        {
            PostToKernel(new DisposeRequest());
            d.Disposable.Dispose();
            
        }
        return base.OnDisconnectedAsync(exception);
    }



}
