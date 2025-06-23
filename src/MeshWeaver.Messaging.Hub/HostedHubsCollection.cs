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
            if (create == HostedHubCreation.Always)
            {
                logger.LogDebug("Creating hosted hub for address {Address}", address);
                return messageHubs[address] = CreateHub(address, config ?? (x => x));
            }

            return null;
        }
    }

    public void Add(IMessageHub hub)
    {
        messageHubs[hub.Address] = hub;
        hub.RegisterForDisposal(h => messageHubs.TryRemove(h.Address, out _));
    }

    private IMessageHub CreateHub<TAddress>(TAddress address, Func<MessageHubConfiguration, MessageHubConfiguration> config)
    where TAddress : Address =>
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
            isDisposing = true; // Set this before starting disposal to prevent new hub creation
            Disposal = DisposeHubs();
        }
    }
    private async Task DisposeHubs()
    {
        var hubs = messageHubs.Values.ToArray();
        var disposalTasks = hubs.Select(hub => DisposeHub(hub)).ToArray();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Task.WhenAll(disposalTasks).WaitAsync(cts.Token);
            logger.LogDebug("All hosted hubs disposed successfully");
        }
        catch (OperationCanceledException)
        {
            logger.LogError("Hosted hubs disposal timed out after 10 seconds. Some hubs may not have disposed properly.");

            // Log which hubs didn't complete disposal
            for (int i = 0; i < disposalTasks.Length; i++)
            {
                if (!disposalTasks[i].IsCompleted)
                {
                    logger.LogError("Hub {address} disposal did not complete within timeout", hubs[i].Address);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during hosted hubs disposal");
        }
    }
    private async Task DisposeHub(IMessageHub hub)
    {
        var address = hub.Address;
        logger.LogDebug("Disposing hub {address}", address);
        try
        {
            hub.Dispose();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await hub.Disposal.WaitAsync(cts.Token);
            logger.LogDebug("Hub {address} disposed successfully", address);
        }
        catch (OperationCanceledException)
        {
            logger.LogError("Hub {address} disposal timed out after 5 seconds", address);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during disposal of hub {address}", address);
            throw;
        }
    }

}
