using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace OpenSmc.Messaging.Hub;

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
        if (messageHubs.TryGetValue(address, out var hub))
            return hub;
        return messageHubs[address] = CreateHub(address, config);
    }

    private IMessageHub CreateHub<TAddress>(TAddress address, Func<MessageHubConfiguration, MessageHubConfiguration> config)
    {
        var settings = serviceProvider.GetRequiredService<HostedHubConfigurationSettings<TAddress>>();
        return serviceProvider.CreateMessageHub(address, config ?? settings.Configure);
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

        var needsFlush = true;
        while (needsFlush)
        {
            needsFlush = false;
            foreach (var hub in Hubs)
            {
                if (hub != null)
                    needsFlush = await hub.FlushAsync();  // TODO V10: should this be in the way of `needsFlush = await hub.FlushAsync() || needsFlush`? (2024/01/18, Dmitry Kalabin)
            }
        }

        while (Hubs.Any())
            foreach (var address in messageHubs.Keys.ToArray())
                if (messageHubs.TryRemove(address, out var hub) && hub != null)
                    await hub.DisposeAsync();
    }
}


// ReSharper disable once UnusedTypeParameter
public class HostedHubConfigurationSettings<TAddress>
{
    public Func<MessageHubConfiguration, MessageHubConfiguration> Configure { get; set; }
}