using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

public class HostedHubsCollection(IServiceProvider serviceProvider) : IAsyncDisposable
{
    public IEnumerable<IMessageHub> Hubs => messageHubs.Values;
    public readonly ILogger logger = serviceProvider.GetRequiredService<ILogger<HostedHubsCollection>>(); 

    private readonly ConcurrentDictionary<object, IMessageHub> messageHubs = new();

    public IMessageHub GetHub<TAddress>(TAddress address, Func<MessageHubConfiguration, MessageHubConfiguration> config, bool cachedOnly = false)
    {
        lock (locker)
        {
            if (messageHubs.TryGetValue(address, out var hub))
                return hub;
            if (cachedOnly)
                return null;
            return messageHubs[address] = CreateHub(address, config);
        }
    }

    public void Add(IMessageHub hub)
        => messageHubs[hub.Address] = hub;

    private IMessageHub CreateHub<TAddress>(TAddress address, Func<MessageHubConfiguration, MessageHubConfiguration> config)
    {
        if (isDisposing)
            return null; 
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
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // Set timeout duration
                    try
                    {
                        await Task.WhenAny(hub.DisposeAsync().AsTask(), Task.Delay(Timeout.Infinite, cts.Token));
                        if (cts.Token.IsCancellationRequested)
                        {
                            logger.LogError("Disposal of hub {address} timed out", address);
                        }
                        else
                        {
                            logger.LogDebug("Finished disposal of hub {address}", address);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error during disposal of hub {address}", address);
                    }
                    logger.LogDebug("Finished disposal of hub {address}", address);

                }
    }
}
