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
        // Use unprotected read — the grain needs its own node to activate,
        // and it's not the correct hub identity for security checks.
        var node = await persistence.GetNodeAsync(address.ToString(), cancellationToken);

        if (node is null)
            throw new MeshException(
                $"Cannot instantiate Node {streamId}. No {nameof(MeshNode.HubConfiguration)} is specified.");

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


        var hubConfig = node.HubConfiguration;
        if (hubConfig is null)
            throw new ArgumentException(
                $"No hub configuration is specified for {node.Path}."
            );

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

        // TODO V10: Find out which cancellation token to pass. (11.01.2025, Roland Bürgi)
        var ret = Hub!.DeliverMessage(delivery);
        return Task.FromResult(ret);
    }


    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {

        if (Hub != null)
        {
            Hub.Dispose();
            await Hub.Disposal!;
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



