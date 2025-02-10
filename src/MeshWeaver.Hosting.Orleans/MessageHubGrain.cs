using System.Collections.Immutable;
using System.Reflection;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Providers;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans;

[StorageProvider(ProviderName = StorageProviders.Activity)]
public class MessageHubGrain(ILogger<MessageHubGrain> logger, IMessageHub meshHub)
    : Grain<StreamActivity>, IMessageHubGrain
{

    private ModulesAssemblyLoadContext loadContext;
    private readonly IMeshCatalog meshCatalog = meshHub.ServiceProvider.GetRequiredService<IMeshCatalog>();
    private IMessageHub Hub { get; set; }


    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {

        var streamId = this.GetPrimaryKeyString();

        var address = meshHub.GetAddress(streamId);
        var node = await meshCatalog.GetNodeAsync(address);

        if(node is null)
            throw new MeshException(
                $"Cannot instantiate Node {streamId}. Neither a {nameof(MeshNode.StartupScript)} nor a {nameof(MeshNode.HubConfiguration)}  are specified.");

        Hub = node.InstantiationType switch
        {
            InstantiationType.HubConfiguration => InstantiateFromHubConfiguration(address, node),
            _ => throw new NotSupportedException()
        };

        //var route = await routingService.RegisterStreamAsync(Hub.Address, Hub.DeliverMessage);
        //Hub.RegisterForDisposal(async (_, _) => await route.DisposeAsync());
        State = State with { IsDeactivated = false };

        await this.WriteStateAsync();
    }

    private IMessageHub InstantiateFromHubConfiguration(Address address, MeshNode node)
    {
        if (node.AssemblyLocation is null)
            throw new ArgumentException(
                $"Assembly location is not configured for node {node.Key}."
            );
        var assembly = Assembly.LoadFrom(node.AssemblyLocation);
        if(assembly is null)
            throw new ArgumentException(
                $"Could not load assembly {node.AssemblyLocation}."
            );

        node = assembly.GetCustomAttributes<MeshNodeAttribute>()
            .SelectMany(x => x.Nodes)
            .FirstOrDefault(x => x.Key == node.Key);

        if(node?.HubConfiguration is null)
            throw new ArgumentException(
                $"No hub configuration is specified for {node.Key}."
            );

        return meshHub.GetHostedHub(address, node.HubConfiguration);
    }


    public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
    {
        logger.LogDebug("Received: {request}", delivery);
        if (Hub == null)
        {
            var address = this.GetPrimaryKeyString();
            logger.LogError("Hub not started for {address}", this.GetPrimaryKeyString());
            DeactivateOnIdle();
            return delivery.Failed($"Hub not started for {address}");
        }


        var messageType = delivery.Message.GetType().FullName;
        this.State = State with
        {
            EventCounter =
            State.EventCounter.SetItem(messageType, State.EventCounter.GetValueOrDefault(messageType) + 1),
        };

        Hub.RegisterForDisposal(_ => DeactivateOnIdle());

        // TODO V10: Find out which cancellation token to pass. (11.01.2025, Roland Bürgi)
        var ret = Hub.DeliverMessage(delivery);
        await this.WriteStateAsync();
        return ret;
    }


    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {

        if (Hub != null)
        {
            Hub.Dispose();
            await Hub.Disposal;
        }
        Hub = null;
        if (loadContext != null)
            loadContext.Unload();
        loadContext = null;
        State = State with { IsDeactivated = true };
        try
        {
            await WriteStateAsync();
            await base.OnDeactivateAsync(reason, cancellationToken);
        }
        catch(Exception e)
        {
            logger.LogError(e, "Error during deactivation of {address}", this.GetPrimaryKeyString());
            throw;
        }
    }
}



public record StreamActivity
{
    public ImmutableDictionary<string, int> EventCounter { get; init; } = ImmutableDictionary<string, int>.Empty;
    public int ErrorCounter { get; init; }
    public StreamSequenceToken Token { get; init; }
    public bool IsDeactivated { get; init; }
}



