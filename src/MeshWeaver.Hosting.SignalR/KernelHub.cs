using System.Collections.Concurrent;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.AspNetCore.SignalR;

namespace MeshWeaver.Hosting.SignalR;

public class KernelHub : Hub
{
    public const string EndPoint = "kernel";
    public const string KernelEventsMethod = "kernelEvents";


    public void SubmitCommand(string kernelCommandEnvelope)
    {
        KernelCommandFromProxy(kernelCommandEnvelope);
    }

    public void KernelCommandFromProxy(string kernelCommandEnvelope)
    {
        PostToKernel(new KernelCommandEnvelope(kernelCommandEnvelope), GetKernelId());
    }
    public void KernelEventFromProxy(string kernelEventEnvelope)
    {
        PostToKernel(new KernelEventEnvelope(kernelEventEnvelope), GetKernelId());
    }

    private string GetKernelId()
    {
        if (!kernelByClient.TryGetValue(Context.ConnectionId, out var ret))
            throw new MeshException($"No kernel mapped for connection {Context.ConnectionId}");
        return ret;
    }

    private void PostToKernel(object message, string kernelId)
    {
        hub.Post(message, o => o.WithTarget(new KernelAddress(){Id = kernelId}));
    }

    public override Task OnConnectedAsync()
    {
        var clientId = Context.ConnectionId;
        if (kernelByClient.ContainsKey(clientId))
            return Task.CompletedTask;

        var kernelId = Guid.NewGuid().AsString();
        kernelByClient[clientId] = kernelId;
        clientByKernel[kernelId] = clientId;

        return Task.CompletedTask;
    }

    public async Task Connect()
    {
        
        await Clients.Caller.SendAsync("connected", true);
    }


    private readonly ConcurrentDictionary<string, string> kernelByClient = new();
    private readonly ConcurrentDictionary<string, string> clientByKernel = new();
    private readonly IMessageHub hub;

    public KernelHub(IMessageHub hub)
    {
        this.hub = hub;
        hub.Register<KernelEventEnvelope>(async (d, ct) =>
            {
                var id = GetClientId(d.Sender);
                if (id != null)
                    await Clients.Client(id).SendCoreAsync(KernelEventsMethod, [d.Message.Envelope], ct);
                return d.Processed();
            });
    }

    private string GetClientId(object sender)
    {
        var kernelId = (sender as KernelAddress)?.Id;
        if (kernelId is null)
            return null; // TODO V10: Should we throw here? or clean up? (15.12.2024, Roland Bürgi)
        return clientByKernel.GetValueOrDefault(kernelId);
    }

    public override Task OnDisconnectedAsync(Exception exception)
    {
        if (kernelByClient.TryRemove(Context.ConnectionId, out var kernelId))
        {
            PostToKernel(new DisposeRequest(), kernelId);
            clientByKernel.TryRemove(kernelId, out _);
        }
        return base.OnDisconnectedAsync(exception);
    }



}
