using System.Collections.Immutable;
using System.Reflection;
using MeshWeaver.Domain;
using MeshWeaver.Hosting.Orleans.Client;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Providers;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans.Server;

[StorageProvider(ProviderName = StorageProviders.Activity)]
public class MessageHubGrain(ILogger<MessageHubGrain> logger, IMessageHub parentHub)
    : Grain<StreamActivity>, IMessageHubGrain
{

    private ModulesAssemblyLoadContext loadContext;


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

        var pathToAssembly = Path.Combine(startupInfo.BaseDirectory, startupInfo.AssemblyLocation);
        loadContext = new(startupInfo.BaseDirectory);
        var loaded = Assembly.LoadFrom(pathToAssembly);


        var meshAttribute = loaded.GetCustomAttributes<MeshNodeAttribute>()
            .FirstOrDefault(a => a.Node.Id == startupInfo.Id && a.Node.AddressType == startupInfo.AddressType);

        if (meshAttribute == null)
            throw new InvalidOperationException($"No HubStartupAttribute found for {startupInfo.Id} of type {startupInfo.AddressType}");
        
        
        var typeRegistry = Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        var type = typeRegistry.GetType(startupInfo.AddressType);
        if (type == null)
            throw new InvalidOperationException($"Type {startupInfo.AddressType} not found in registry");
        var address = Activator.CreateInstance(type, new object[] { startupInfo.Id });
        Hub = meshAttribute.Create(parentHub.ServiceProvider, address);
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
        var ret = Hub.DeliverMessage(delivery);
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



