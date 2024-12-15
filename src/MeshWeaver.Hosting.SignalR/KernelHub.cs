using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR;

namespace MeshWeaver.Hosting.SignalR;

public class KernelHub : Hub
{

    private KernelAddress KernelAddress { get; } = new();


    public void SubmitCommand(string kernelCommandEnvelope)
    {
        KernelCommandFromProxy(kernelCommandEnvelope);
    }

    public void KernelCommandFromProxy(string kernelCommandEnvelope)
    {
        hub.Post(new KernelCommandEnvelope(kernelCommandEnvelope), o => o.WithTarget(KernelAddress));
    }
    public void KernelEventFromProxy(string kernelEventEnvelope)
    {
        hub.Post(new KernelEventEnvelope(kernelEventEnvelope), o => o.WithTarget(KernelAddress));
    }

    public async Task Connect()
    {
        await Clients.Caller.SendAsync("connected");
    }



    private bool isDisposing;
    private readonly object locker = new();
    private readonly IMessageHub hub;
    private readonly ISingleClientProxy Caller;

    public KernelHub(IMessageHub hub)
    {
        this.hub = hub;
        Caller = Clients.Caller;
        hub.Register<KernelEventEnvelope>(async (d, ct) =>
        {
            await Caller.SendCoreAsync("kernelEvents", [d.Message.Envelope], ct);
            return d.Processed();
        });
    }

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
