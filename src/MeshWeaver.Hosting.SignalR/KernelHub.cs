using System.Collections.Concurrent;
using System.Collections.Immutable;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;

namespace MeshWeaver.Hosting.SignalR;

public class KernelHub : Hub
{
    public const string EndPoint = "kernel";
    public const string KernelEventsMethod = "kernelEvents";
    private readonly ConcurrentDictionary<KernelAddress, ImmutableList<string>> connectionsByKernel = new();
    private readonly ConcurrentDictionary<string, KernelAddress> kernelByConnection = new();
    private readonly IKernelService kernelService;
    private readonly IHttpContextAccessor httpContextAccessor;


    public Task SubmitCommand(string kernelCommandEnvelope)
    {

        if (!kernelByConnection.TryGetValue(Context.ConnectionId, out var kernelAddress))
            throw new HubException("Kernel is not connected.");
        return kernelService.SubmitCommandAsync(kernelAddress, kernelCommandEnvelope, GetLayoutAreaAddress());
    }

    public string? GetLayoutAreaAddress()
    {
        var request = httpContextAccessor.HttpContext?.Request;
        if (request is null)
            return null;

        var baseUrl = $"{request?.Scheme}://{request?.Host}{request?.PathBase}/area";
        return baseUrl;
    }


    public Task SubmitEvent(string kernelEventEnvelope)
    {
        if (!kernelByConnection.TryGetValue(Context.ConnectionId, out var kernelAddress))
            throw new HubException("Kernel is not connected.");
        return kernelService.SubmitEventAsync(kernelAddress, kernelEventEnvelope);
    }



    public bool Connect(string kernelId)
    {
        var kernel = new KernelAddress { Id = kernelId };
        kernelByConnection[Context.ConnectionId] = kernel;
        connectionsByKernel.AddOrUpdate(
            kernel, _ => [Context.ConnectionId],
            (_, l) => l.Add(Context.ConnectionId)
        );

        return true;
    }

    public bool DisposeKernel()
    {
        if (!kernelByConnection.TryGetValue(Context.ConnectionId, out var kernelAddress))
            return false;
        kernelService.DisposeKernel(kernelAddress);
        return true;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (kernelByConnection.TryRemove(Context.ConnectionId, out var id))
            connectionsByKernel.AddOrUpdate(
                id, _ => ImmutableList<string>.Empty,
                (_, l) => l.Remove(Context.ConnectionId)
            );
        return base.OnDisconnectedAsync(exception);
    }

    public KernelHub(IMessageHub hub, IKernelService kernelService, IHttpContextAccessor httpContextAccessor)
    {
        this.kernelService = kernelService;
        this.httpContextAccessor = httpContextAccessor;
        hub.Register<KernelEventEnvelope>(async (d, ct) =>
        {
            var kernelAddress = MessageHubExtensions.GetAddressOfType<KernelAddress>(d.Sender);
            if (kernelAddress is null)
                return d;

            if (connectionsByKernel.TryGetValue(kernelAddress, out var connections))
                foreach (var connection in connections)
                    await Clients.Client(connection).SendCoreAsync(KernelEventsMethod, [d.Message.Envelope], ct);

            return d.Processed();
        });
    }
}
