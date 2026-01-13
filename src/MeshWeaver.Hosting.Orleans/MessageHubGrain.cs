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
    private readonly IMeshCatalog meshCatalog = meshHub.ServiceProvider.GetRequiredService<IMeshCatalog>();
    private IMessageHub? Hub { get; set; }


    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {

        var streamId = this.GetPrimaryKeyString();

        var address = meshHub.GetAddress(streamId);
        var node = await meshCatalog.GetNodeAsync(address);

        if(node is null)
            throw new MeshException(
                $"Cannot instantiate Node {streamId}. Neither a {nameof(MeshNode.StartupScript)} nor a {nameof(MeshNode.HubConfiguration)}  are specified.");

        // Ensure on-demand compilation of dynamic node assemblies if service is available
        // This compiles/caches the node's DataModel type and generates MeshNodeAttribute
        await EnsureNodeAssemblyAsync(node, cancellationToken);

        Hub = node.InstantiationType switch
        {
            InstantiationType.HubConfiguration => await InstantiateFromHubConfiguration(address, node),
            _ => throw new NotSupportedException()
        };

        //var route = await routingService.RegisterStreamAsync(Hub.Address, Hub.DeliverMessage);
        //Hub.RegisterForDisposal(async (_, _) => await route.DisposeAsync());
    }

    /// <summary>
    /// Ensures the node's assembly is compiled and loaded if on-demand compilation is available.
    /// Uses IMeshNodeCompilationService if registered in DI.
    /// </summary>
    private async Task EnsureNodeAssemblyAsync(MeshNode node, CancellationToken ct)
    {
        // Try to get the on-demand compilation service (optional - from MeshWeaver.Graph)
        // This service compiles DataModel types and generates MeshNodeAttribute for dynamic nodes
        var compilationService = meshHub.ServiceProvider.GetService<IMeshNodeCompilationService>();
        if (compilationService != null)
        {
            try
            {
                var assemblyLocation = await compilationService.GetAssemblyLocationAsync(node, ct);
                if (assemblyLocation != null)
                {
                    logger.LogDebug("On-demand compilation ensured for node {NodePath} at {AssemblyLocation}", node.Path, assemblyLocation);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to ensure on-demand compilation for node {NodePath}", node.Path);
                // Continue - the node may still work if already configured
            }
        }
    }

    private async Task<IMessageHub> InstantiateFromHubConfiguration(Address address, MeshNode node)
    {
        if (node.AssemblyLocation is null)
            throw new ArgumentException(
                $"Assembly location is not configured for node {node.Path}."
            );
        var assembly = Assembly.LoadFrom(node.AssemblyLocation);
        if(assembly is null)
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



