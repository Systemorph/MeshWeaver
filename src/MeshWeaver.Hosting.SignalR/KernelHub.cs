using System.Collections.Concurrent;
using System.Collections.Immutable;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.SignalR;

namespace MeshWeaver.Hosting.SignalR;

public class KernelHub : Hub
{
    public const string EndPoint = "kernel";
    public const string KernelEventsMethod = "kernelEvents";
    private readonly ConcurrentDictionary<string, ImmutableList<string>> connectionsByKernel = new();
    private readonly ConcurrentDictionary<string, string> clientByConnection = new();
    private readonly IKernelService kernelService;


    public Task SubmitCommand(string kernelCommandEnvelope)
    {
        var clientId = GetClientId(Context.ConnectionId);
        return kernelService.SubmitCommandAsync(clientId, kernelCommandEnvelope);
    }

    private string GetClientId(string connectionId)
        => clientByConnection.GetValueOrDefault(connectionId);

    public void KernelCommandFromProxy(string kernelCommandEnvelope)
    {
        // TODO V10: Need to understand how to implement this (01.01.2025, Roland Bürgi)
    }
    public void KernelEventFromProxy(string kernelEventEnvelope)
    {
        // TODO V10: Need to understand how to implement this (01.01.2025, Roland Bürgi)
    }



    public async Task Connect(string clientId)
    {
        var kernel = await kernelService.GetKernelIdAsync(clientId);
        connectionsByKernel.AddOrUpdate(
            kernel, x => [x], (x, l) => l.Add(x));

        await Clients.Caller.SendAsync("connected", true);
    }



    public KernelHub(IMessageHub hub, IKernelService kernelService)
    {
        this.kernelService = kernelService;
        hub.Register<KernelEventEnvelope>(async (d, ct) =>
        {
            var (type,kernelId) = MessageHubExtensions.GetAddressTypeAndId(d.Sender);

            if (type != KernelAddress.TypeName)
                return d;

            if (connectionsByKernel.TryGetValue(kernelId, out var connections))
                foreach (var connection in connections)
                    await Clients.Client(connection).SendCoreAsync(KernelEventsMethod, [d.Message.Envelope], ct);

            return d.Processed();
        });

    }





}
