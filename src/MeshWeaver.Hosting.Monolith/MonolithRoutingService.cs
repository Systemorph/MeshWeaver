using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Monolith;

internal class MonolithRoutingService(IMessageHub hub, ILogger<MonolithRoutingService> logger) : RoutingServiceBase(hub)
{
    private readonly ConcurrentDictionary<Address, AsyncDelivery> streams = new();


    private Task UnregisterStreamAsync(Address address)
    {
        streams.TryRemove(address, out _);
        return Task.FromResult<Address>(null!);
    }


    public override Task<IAsyncDisposable> RegisterStreamAsync(Address address, AsyncDelivery callback)
    {
        streams[address] = callback;
        return Task.FromResult<IAsyncDisposable>(new AnonymousAsyncDisposable(() => { streams.TryRemove(address, out _);
            return Task.CompletedTask;
        }));
    }


    protected override async Task<IMessageDelivery> RouteImplAsync(
        IMessageDelivery delivery,
        MeshNode? node,
        Address address,
        CancellationToken cancellationToken)
    {
        if (streams.TryGetValue(address, out var stream))
            return await stream.Invoke(delivery, cancellationToken);

        var hub = await CreateHubAsync(node, address);
        if (hub is null)
        {
            var isShuttingDown = Mesh.Disposal is not null;
            string errorMessage;
            if (isShuttingDown)
                errorMessage = $"Mesh is shutting down, cannot route to {address}";
            else if (node is null)
                errorMessage = $"No node found for address {address}";
            else
                errorMessage = $"No hub configuration for node '{node.Path}' (NodeType: {node.NodeType ?? "null"}). Ensure the node type is registered via AddGraph() or a custom builder extension.";

            logger.LogWarning("No route found for {MessageType} → {Address}. Node: {NodePath}, NodeType: {NodeType}, Sender: {Sender}, ShuttingDown: {ShuttingDown}",
                delivery.Message.GetType().Name, address, node?.Path, node?.NodeType, delivery.Sender, isShuttingDown);

            // Post DeliveryFailure response so AwaitResponse callers get an exception.
            // Guard: don't post DeliveryFailure for DeliveryFailure messages or during shutdown.
            if (delivery.Message is not DeliveryFailure && Mesh.RunLevel < MessageHubRunLevel.DisposeHostedHubs)
            {
                Mesh.Post(
                    new DeliveryFailure(delivery)
                    {
                        ErrorType = isShuttingDown ? ErrorType.Failed : ErrorType.NotFound,
                        Message = errorMessage
                    }, o => o.ResponseFor(delivery)
                );
            }
            return delivery.Failed(errorMessage);
        }

        hub.DeliverMessage(delivery);
        return delivery.Forwarded(hub.Address);
    }

    private async Task<IMessageHub?> CreateHubAsync(MeshNode? node, Address address)
    {
        if (Mesh.Disposal is not null || node is null)
            return null;

        var hubFactory = Mesh.ServiceProvider.GetRequiredService<IMeshNodeHubFactory>();

        // Single Task bridge at the framework boundary (RouteImplAsync is
        // Task<IMessageDelivery> by signature). The reactive chain inside
        // ResolveHubConfiguration runs on the producer's scheduler — it does
        // NOT capture this caller's synchronization context, so a CompileRequest
        // routed back through this same RoutingService while we're awaiting
        // here doesn't deadlock the way the previous Task-returning
        // ResolveHubConfigurationAsync did.
        node = await hubFactory.ResolveHubConfiguration(node).FirstAsync().ToTask();

        if (node.HubConfiguration is null)
            throw new InvalidOperationException(
                $"No hub configuration for node '{node.Path}' (NodeType: {node.NodeType}). " +
                $"Ensure the node type is registered via AddGraph() or has a HubConfiguration set.");

        var hub = Mesh.GetHostedHub(address, node.HubConfiguration!);
        hub?.RegisterForDisposal((_, _) => UnregisterStreamAsync(hub.Address));
        return hub;
    }
}
