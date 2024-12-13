using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR;

namespace MeshWeaver.Hosting.SignalR;

public class KernelHub(IMessageHub hub) : Hub
{

    private readonly KernelAddress KernelAddress = new();


    public void SubmitCommand(string kernelCommandEnvelope)
    {
        KernelCommandFromServer(kernelCommandEnvelope);
    }

    public void KernelCommandFromServer(string kernelCommandEnvelope)
    {
        hub.Post(new KernelCommandEnvelope(kernelCommandEnvelope), o => o.WithTarget(KernelAddress));
    }

    public void KernelEvent(string kernelEventEnvelope)
    {
        KernelEventFromServer(kernelEventEnvelope);
    }

    public void KernelEventFromServer(string kernelEventEnvelope)
    {
        hub.Post(new KernelEventEnvelope(kernelEventEnvelope));
    }

    public async Task Connect()
    {
        await Clients.Caller.SendAsync("connected");
    }



    private bool isDisposing;
    private readonly object locker = new();
    public override Task OnDisconnectedAsync(Exception exception)
    {
        lock (locker)
        {
            if (isDisposing)
                return Task.CompletedTask;
            isDisposing = true;
        }

        hub.Post(new DisposeRequest(), o => o.WithTarget(KernelAddress));
        return base.OnDisconnectedAsync(exception);
    }
}
