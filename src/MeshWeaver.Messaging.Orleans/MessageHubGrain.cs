using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Messaging;
using MeshWeaver.Orleans.Contract;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Providers;
using Orleans.Streams;
using Orleans.Streams.Core;

namespace MeshWeaver.Orleans;

[ImplicitStreamSubscription(MessageIn)]
[StorageProvider(ProviderName = StorageProviders.Activity)]
public class MessageHubGrain(ILogger<MessageHubGrain> logger, IMessageHub parentHub) : Grain<StreamActivity>, IStreamSubscriptionObserver
{
    public const string MessageIn = nameof(MessageIn);

    private AssemblyLoadContext loadContext;


    private IMessageHub Hub { get; set; }




    public async Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory)
    {
        logger.LogInformation($"OnSubscribed: {handleFactory.ProviderName}/{handleFactory.StreamId}");

        var streamId = this.GetPrimaryKeyString();
        var startupInfo = await GrainFactory.GetGrain<IAddressRegistryGrain>(streamId).GetStorageInfo();

        var pathToAssembly = Path.Combine(startupInfo.BaseDirectory, startupInfo.AssemblyLocation);
        loadContext = new(this.GetPrimaryKeyString());
        var loaded = loadContext.LoadFromAssemblyPath(pathToAssembly);
        var startupAttribute = loaded.GetCustomAttributes<MeshNodeAttribute>().FirstOrDefault(a => a.Node.Id == startupInfo.NodeId);
        if (startupAttribute == null)
            throw new InvalidOperationException($"No HubStartupAttribute found for {startupInfo.NodeId}");

        Hub = startupAttribute.Create(parentHub.ServiceProvider, startupInfo.Address);
        State = State with { IsDeactivated = false };
        await this.WriteStateAsync();
        
        await handleFactory.Create<IMessageDelivery>().ResumeAsync(OnNext, OnError, OnCompleted, this.State.Token);

        async Task OnNext(IMessageDelivery value, StreamSequenceToken token)
        {
            logger.LogDebug("Received: [{Value} {Token}]", value, token);
            var messageType = value.Message.GetType().FullName;
            this.State = State with
            {
                EventCounter = State.EventCounter.SetItem(messageType, State.EventCounter.GetValueOrDefault(messageType) + 1),
                Token = token,
            };
            Hub.DeliverMessage(value);
            await this.WriteStateAsync();
        }

        async Task OnError(Exception ex)
        {
            logger.LogError("Error: {Exception}", ex);
            this.State = State with{ErrorCounter = State.ErrorCounter + 1};
            await this.WriteStateAsync();
        }

        async Task OnCompleted()
        {
            if(Hub != null)
                await Hub.DisposeAsync();
            Hub = null;
            State = State with { IsDeactivated = true };
            await WriteStateAsync();
        }
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if(Hub != null)
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
