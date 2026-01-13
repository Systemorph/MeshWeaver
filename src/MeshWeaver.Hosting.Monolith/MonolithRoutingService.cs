using System.Collections.Concurrent;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Monolith;

public class MonolithRoutingService(IMessageHub hub, ILogger<MonolithRoutingService> logger) : RoutingServiceBase(hub)
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

        var hub = CreateHub(node, address);
        if (hub is null)
        {
            logger.LogWarning("No node found for address {Address}", address);
            return delivery.Failed($"No node found for address {address}");
        }

        hub.DeliverMessage(delivery);
        return delivery.Forwarded(hub.Address);
    }

    private IMessageHub? CreateHub(MeshNode? node, Address address)
    {
        var hubConfig = node?.HubConfiguration ?? GetTemplateHubConfiguration(address);

        if (hubConfig is not null)
        {
            var hub = Mesh.GetHostedHub(address, hubConfig);
            if(hub is not null)
            {
                hub.RegisterForDisposal((_, _) => UnregisterStreamAsync(hub.Address));
            }
            return hub;
        }
        return null;
    }

    /// <summary>
    /// Finds a template node's HubConfiguration by walking up the address path.
    /// Template nodes registered via AddMeshNodes() have HubConfiguration and AddressSegments > 0.
    /// </summary>
    private Func<MessageHubConfiguration, MessageHubConfiguration>? GetTemplateHubConfiguration(Address address)
    {
        var segments = address.Segments;

        // Walk up the path, looking for template nodes in Configuration.Nodes
        for (int depth = segments.Length - 1; depth >= 1; depth--)
        {
            var parentPath = string.Join("/", segments.Take(depth));
            if (MeshCatalog.Configuration.Nodes.TryGetValue(parentPath, out var templateNode) &&
                templateNode.HubConfiguration is not null &&
                templateNode.AddressSegments >= segments.Length)
            {
                logger.LogDebug("Using template HubConfiguration from {TemplatePath} for {Address}",
                    templateNode.Path, address);
                return templateNode.HubConfiguration;
            }
        }

        return null;
    }
}
