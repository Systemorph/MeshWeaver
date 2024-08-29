using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using MeshWeaver.Orleans.Client;
using Microsoft.Extensions.Logging;
using Orleans.Providers;
using Orleans.Streams;
using Orleans.Streams.Core;

namespace MeshWeaver.Orleans.Server;

[ImplicitStreamSubscription(MessageIn)]
[StorageProvider(ProviderName = StorageProviders.Activity)]
public class MessageHubGrain(ILogger<MessageHubGrain> logger, IMessageHub parentHub)
    : Grain<StreamActivity>, IMessageHubGrain
{
    public const string MessageIn = nameof(MessageIn);

    private AssemblyLoadContext loadContext;


    private IMessageHub Hub { get; set; }


    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {

        var streamId = this.GetPrimaryKeyString();
        var startupInfo = await GrainFactory.GetGrain<IAddressRegistryGrain>(streamId).GetStorageInfo();

        var pathToAssembly = Path.Combine(startupInfo.BaseDirectory, startupInfo.AssemblyLocation);
        loadContext = new(this.GetPrimaryKeyString());
        var loaded = loadContext.LoadFromAssemblyPath(pathToAssembly);
        var startupAttribute = loaded.GetCustomAttributes<MeshNodeAttribute>()
            .FirstOrDefault(a => a.Node.Id == startupInfo.NodeId);
        if (startupAttribute == null)
            throw new InvalidOperationException($"No HubStartupAttribute found for {startupInfo.NodeId}");

        Hub = startupAttribute.Create(parentHub.ServiceProvider, startupInfo.Address);
        State = State with { IsDeactivated = false };
        await this.WriteStateAsync();
    }




    public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery request)
    {
        logger.LogDebug("Received: [{Value} {Token}]", request);
        var messageType = request.Message.GetType().FullName;
        this.State = State with
        {
            EventCounter =
            State.EventCounter.SetItem(messageType, State.EventCounter.GetValueOrDefault(messageType) + 1),
        };
        var ret = Hub.DeliverMessage(request);
        await this.WriteStateAsync();
        return ret;
    }


    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if (Hub != null)
            await Hub.DisposeAsync();
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



public record StopMessageHubEvent;
