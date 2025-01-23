using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

public class HostedHubsCollection(IServiceProvider serviceProvider) : IDisposable
{
    public IEnumerable<IMessageHub> Hubs => messageHubs.Values;
    private readonly ILogger logger = serviceProvider.GetRequiredService<ILogger<HostedHubsCollection>>(); 

    private readonly ConcurrentDictionary<object, IMessageHub> messageHubs = new();

    public IMessageHub GetHub<TAddress>(TAddress address, Func<MessageHubConfiguration, MessageHubConfiguration> config, HostedHubCreation create)
        where TAddress : Address
    {
        lock (locker)
        {
            if (messageHubs.TryGetValue(address, out var hub))
                return hub;
            return create switch
            {
                HostedHubCreation.Always => messageHubs[address] = CreateHub(address, config ?? (x => x)),
                _ => null
            };
        }
    }

    public void Add(IMessageHub hub)
    {
        messageHubs[hub.Address] = hub;
        hub.RegisterForDisposal(h => messageHubs.TryRemove(h.Address, out _));
    }

    private IMessageHub CreateHub<TAddress>(TAddress address, Func<MessageHubConfiguration, MessageHubConfiguration> config)
    where TAddress:Address =>
        isDisposing
            ? null
            : serviceProvider.CreateMessageHub(address, config);

    private bool isDisposing;
    private readonly object locker = new();

    public Task Disposal { get; private set; }
    

    public void Dispose()
    {
        lock (locker)
        {
            if (Disposal is not null) return;
            Disposal = DisposeHubs();
        }




    }

    private Task DisposeHubs()
    {
        return Task.WhenAll(messageHubs.Values.ToArray().Select(DisposeHub));

    }

    private Task DisposeHub(IMessageHub hub)
    {
        var address = hub.Address;
        logger.LogDebug("Disposing hub {address}", address);
        try
        {
            hub.Dispose();
            return hub.Disposal;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during disposal of hub {address}", address);
            return Task.CompletedTask;
        }

    }

}
