using System.Collections.Immutable;
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
public class MessageHubGrain(ILogger<MessageHubGrain> logger, IMessageHub meshHub, IRoutingService routingService)
    : Grain<StreamActivity>, IMessageHubGrain
{

    private ModulesAssemblyLoadContext loadContext;
    private readonly IMeshCatalog meshCatalog = meshHub.ServiceProvider.GetRequiredService<IMeshCatalog>();
    private IMessageHub Hub { get; set; }


    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {

        var streamId = this.GetPrimaryKeyString();
        var startupInfo = await GrainFactory.GetGrain<IAddressRegistryGrain>(streamId).GetStorageInfo();
        if (startupInfo == null || startupInfo is { AssemblyLocation: null })
        {
            logger.LogError("Cannot find info for {address}", this.GetPrimaryKeyString());
            return;
        }


        var node = await meshCatalog.GetNodeAsync(startupInfo.AddressType, startupInfo.Id);
        if (node.HubFactory is null)
            throw new MeshException(
                $"Cannot instantiate Node {node.Name}. Neither a {nameof(MeshNode.StartupScript)} nor a {nameof(MeshNode.HubFactory)}  are specified.");

        Hub = node.HubFactory(meshHub.ServiceProvider, node.AddressType, startupInfo.Id);
        Hub.RegisterForDisposal((_, _) => routingService.Unregister(Hub.Address));
        State = State with { IsDeactivated = false };

        await this.WriteStateAsync();
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

        // TODO V10: Find out which cancellation token to pass. (11.01.2025, Roland Bürgi)
        var ret = await Hub.DeliverMessageAsync(delivery, default);
        await this.WriteStateAsync();
        return ret;
    }


    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {

        if (Hub != null)
        {
            Hub.Dispose();
            await Hub.Disposed;
        }
        Hub = null;
        if (loadContext != null)
            loadContext.Unload();
        loadContext = null;
        State = State with { IsDeactivated = true };
        await WriteStateAsync();
        await base.OnDeactivateAsync(reason, cancellationToken);
    }
}



public record StreamActivity
{
    public ImmutableDictionary<string, int> EventCounter { get; init; } = ImmutableDictionary<string, int>.Empty;
    public int ErrorCounter { get; init; }
    public StreamSequenceToken Token { get; init; }
    public bool IsDeactivated { get; init; }
}



