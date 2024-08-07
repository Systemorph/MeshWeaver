using System.Collections.Concurrent;

namespace MeshWeaver.Messaging;

public class HostedHubsCollection : IAsyncDisposable
{
    private readonly IServiceProvider serviceProvider;
    public IEnumerable<IMessageHub> Hubs => messageHubs.Values;

    private readonly ConcurrentDictionary<object, IMessageHub> messageHubs = new();

    public HostedHubsCollection(IServiceProvider serviceProvider)
    {
        this.serviceProvider = serviceProvider;
    }

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
                    await hub.DisposeAsync();
    }
}
