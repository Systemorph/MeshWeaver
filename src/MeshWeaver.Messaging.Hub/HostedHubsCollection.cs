using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

public class HostedHubsCollection(IServiceProvider serviceProvider) : IAsyncDisposable
{
    public IEnumerable<IMessageHub> Hubs => messageHubs.Values;
    public readonly ILogger logger = serviceProvider.GetRequiredService<ILogger<HostedHubsCollection>>(); 

    private readonly ConcurrentDictionary<object, IMessageHub> messageHubs = new();

    public IMessageHub GetHub<TAddress>(TAddress address, Func<MessageHubConfiguration, MessageHubConfiguration> config)
    {
        lock (locker)
        {
            if (messageHubs.TryGetValue(address, out var hub))
                return hub;
            return messageHubs[address] = CreateHub(address, config);
        }
    }

    public void Add(IMessageHub hub)
        => messageHubs[hub.Address] = hub;

    private IMessageHub CreateHub<TAddress>(TAddress address, Func<MessageHubConfiguration, MessageHubConfiguration> config)
    {
        return serviceProvider.CreateMessageHub(address, config);
    }

    private bool isDisposing;
    private readonly object locker = new();

    public async ValueTask DisposeAsync()
    {
        lock (locker)
        {
            if (isDisposing) return;
            isDisposing = true;
        }



        while (Hubs.Any())
            foreach (var address in messageHubs.Keys.ToArray())
                if (messageHubs.TryRemove(address, out var hub) && hub != null)
                {
                    logger.LogDebug("Awaiting disposal of hub {address}", address);
                    await hub.DisposeAsync();
                    logger.LogDebug("Finished disposal of hub {address}", address);

                }
    }
}
