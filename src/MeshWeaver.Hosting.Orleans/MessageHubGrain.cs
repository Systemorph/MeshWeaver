using System.Collections.Immutable;
using System.Reflection;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans;

public class MessageHubGrain(ILogger<MessageHubGrain> logger, IMessageHub meshHub)
    : Grain, IMessageHubGrain
{

    private ModulesAssemblyLoadContext? loadContext;
    private readonly IMeshStorage persistence = meshHub.ServiceProvider.GetRequiredService<IMeshStorage>();
    private IMessageHub? Hub { get; set; }


    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamId = this.GetPrimaryKeyString();
        var address = meshHub.GetAddress(streamId);
        var addressPath = address.ToString();

        // Use unprotected read — the grain needs its own node to activate,
        // and it's not the correct hub identity for security checks.
        var node = await persistence.GetNodeAsync(addressPath, cancellationToken);

        // Fallback to MeshConfiguration.Nodes (in-memory registered nodes)
        if (node is null)
        {
            var meshConfig = meshHub.ServiceProvider.GetService<MeshConfiguration>();
            meshConfig?.Nodes.TryGetValue(addressPath, out node);
        }

        // Fallback to static node providers (e.g., DocumentationNodeProvider)
        // for nodes that are never persisted but served as embedded resources.
        node ??= meshHub.ServiceProvider.GetServices<IStaticNodeProvider>()
            .SelectMany(p => p.GetStaticNodes())
            .FirstOrDefault(n => string.Equals(n.Path, addressPath, StringComparison.OrdinalIgnoreCase));

        if (node is null)
        {
            // Throw so Orleans immediately rejects the message without forwarding attempts.
            // Using DeactivateOnIdle() here would leave a zombie activation that triggers
            // forwarding loops (ForwardCount=2 → OrleansMessageRejectionException).
            throw new InvalidOperationException(
                $"Cannot activate grain {streamId}: node not found at {addressPath}.");
        }

        // Enrich with node type info (triggers compilation if needed, sets HubConfiguration + AssemblyLocation)
        var nodeTypeService = meshHub.ServiceProvider.GetService<INodeTypeService>();
        if (nodeTypeService != null)
        {
            node = await nodeTypeService.EnrichWithNodeTypeAsync(node, cancellationToken);
        }

        Hub = await InstantiateFromHubConfiguration(address, node);
    }

    private async Task<IMessageHub> InstantiateFromHubConfiguration(Address address, MeshNode node)
    {
        if (node.AssemblyLocation is null)
            throw new ArgumentException(
                $"Assembly location is not configured for node {node.Path}."
            );
        var assembly = Assembly.LoadFrom(node.AssemblyLocation);
        if (assembly is null)
            throw new ArgumentException(
                $"Could not load assembly {node.AssemblyLocation}."
            );


        var nodeConfig = node.HubConfiguration;
        if (nodeConfig is null)
            throw new ArgumentException(
                $"No hub configuration is specified for {node.Path}."
            );

        // Compose with DefaultNodeHubConfiguration (same as MonolithRoutingService)
        var meshConfig = meshHub.ServiceProvider.GetRequiredService<MeshConfiguration>();
        var defaultConfig = meshConfig.DefaultNodeHubConfiguration;
        var hubConfig = defaultConfig != null
            ? (Func<MessageHubConfiguration, MessageHubConfiguration>)(config => nodeConfig(defaultConfig(config)))
            : nodeConfig;

        return meshHub.GetHostedHub(address, hubConfig)!;
    }


    public Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
    {
        logger.LogDebug("Received: {request}", delivery);
        if (Hub == null)
        {
            var address = this.GetPrimaryKeyString();
            logger.LogError("Hub not started for {address}", this.GetPrimaryKeyString());
            DeactivateOnIdle();
            return Task.FromResult(delivery.Failed($"Hub not started for {address}"));
        }

        Hub?.RegisterForDisposal(_ => DeactivateOnIdle());

        // Apply user identity from Orleans RequestContext to the delivery.
        // The client-side OrleansRoutingService sets UserId/UserName which Orleans
        // propagates across process boundaries. We set it on the delivery itself
        // so the hub's delivery pipeline (UserServiceDeliveryPipeline) picks it up
        // and sets AccessService.Context for the entire async processing chain.
        var userId = RequestContext.Get("UserId") as string;
        var userName = RequestContext.Get("UserName") as string;
        var msgType = delivery.Message?.GetType().Name ?? "(null)";
        var deliveryUser = delivery.AccessContext?.ObjectId;

        if (!string.IsNullOrEmpty(userId) &&
            (delivery.AccessContext == null || delivery.AccessContext.ObjectId != userId))
        {
            delivery = delivery.SetAccessContext(new AccessContext
            {
                ObjectId = userId,
                Name = userName ?? userId
            });
        }

        // Log identity chain for debugging — Warning level for identity-sensitive messages
        if (string.IsNullOrEmpty(userId) || msgType.Contains("Submit", StringComparison.Ordinal))
            logger.LogDebug(
                "GrainDeliver: grain={Grain}, message={MessageType}, requestContextUserId={RequestContextUser}, deliveryUser={DeliveryUser}, finalUser={FinalUser}",
                this.GetPrimaryKeyString(), msgType, userId ?? "(null)", deliveryUser ?? "(null)",
                delivery.AccessContext?.ObjectId ?? "(null)");

        var ret = Hub!.DeliverMessage(delivery);
        return Task.FromResult(ret);
    }


    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        var grainId = this.GetPrimaryKeyString();
        logger.LogInformation("Grain {GrainId} deactivating: reason={Reason}", grainId, reason.ReasonCode);

        if (Hub != null)
        {
            try
            {
                Hub.Dispose();
                // Wait for disposal (includes async flush of pending saves)
                var disposalTask = Hub.Disposal!;
                var completed = await Task.WhenAny(disposalTask, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
                if (completed != disposalTask)
                    logger.LogWarning("Grain {GrainId}: hub disposal timed out after 10s — pending saves may be lost!", grainId);
                else
                    logger.LogInformation("Grain {GrainId}: hub disposal completed", grainId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Grain {GrainId}: hub disposal failed — pending saves may be lost!", grainId);
            }
        }
        Hub = null;
        if (loadContext != null)
            loadContext.Unload();
        loadContext = null;
        await base.OnDeactivateAsync(reason, cancellationToken);
    }


}



public record StreamActivity
{
    public ImmutableDictionary<string, int> EventCounter { get; init; } = ImmutableDictionary<string, int>.Empty;
    public int ErrorCounter { get; init; }
    public StreamSequenceToken? Token { get; init; }
    public bool IsDeactivated { get; init; }
}



