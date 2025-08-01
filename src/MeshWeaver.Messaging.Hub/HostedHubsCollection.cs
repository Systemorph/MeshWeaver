using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Messaging;

public class HostedHubsCollection(IServiceProvider serviceProvider, Address address) : IDisposable
{
    public IEnumerable<IMessageHub> Hubs => messageHubs.Values;
    public Address Host { get; } = address;
    private readonly ILogger logger = serviceProvider.GetRequiredService<ILogger<HostedHubsCollection>>();

    private readonly ConcurrentDictionary<object, IMessageHub> messageHubs = new();

    public IMessageHub? GetHub<TAddress>(TAddress address, Func<MessageHubConfiguration, MessageHubConfiguration> config, HostedHubCreation create)
        where TAddress : Address
    {
        lock (locker)
        {
            if (messageHubs.TryGetValue(address, out var hub))
                return hub;
            
            if (IsDisposing)
            {
                logger.LogWarning("Rejecting hosted hub creation for address {Address} in Host {Host} during disposal - collection is disposing", address, Host);
                return null;
            }
            
            if (create == HostedHubCreation.Always)
            {
                logger.LogDebug("Creating hosted hub for address {Address} in Host {Host}", address, Host);
                var newHub = CreateHub(address, config);
                if (newHub != null)
                    return messageHubs[address] = newHub;
            }

            return null;
        }
    }

    public void Add(IMessageHub hub)
    {
        messageHubs[hub.Address] = hub;
        hub.RegisterForDisposal(h => messageHubs.TryRemove(h.Address, out _));
    }

    private IMessageHub? CreateHub<TAddress>(TAddress address, Func<MessageHubConfiguration, MessageHubConfiguration> config)
    where TAddress : Address
    {
        if (IsDisposing)
        {
            logger.LogWarning("Preventing hub creation for address {Address} in host {Host} - collection is disposing", address, Host);
            return null;
        }
        
        try
        {
            logger.LogDebug("Creating new hosted hub for address {Address} in host {Host} ", address, Host);
            var hub = serviceProvider.CreateMessageHub(address, config);
            return hub;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create hosted hub for address {Address}", address);
            return null;
        }
    }

    private readonly object locker = new();
    private bool IsDisposing => Disposal is not null;
    public Task? Disposal { get; private set; }
    public void Dispose()
    {
        lock (locker)
        {
            if (IsDisposing) return;
            Disposal = DisposeHubs();
        }
    }
    private async Task DisposeHubs()
    {
        var totalStopwatch = Stopwatch.StartNew();
        var hubs = messageHubs.Values.ToArray();
        logger.LogDebug("Starting disposal of {count} hosted hubs: [{hubAddresses}]", 
            hubs.Length, string.Join(", ", hubs.Select(h => h.Address.ToString())));
        
        var disposalTasks = hubs.Select(DisposeHub).ToArray();
        var hubAddresses = hubs.Select(h => h.Address.ToString()).ToArray();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            logger.LogDebug("Waiting for all {count} hosted hubs to dispose with 10 second timeout", hubs.Length);
            await Task.WhenAll(disposalTasks).WaitAsync(cts.Token);
            logger.LogDebug("All {count} hosted hubs disposed successfully in {elapsed}ms", 
                hubs.Length, totalStopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            logger.LogError("Hosted hubs disposal timed out after 10 seconds ({elapsed}ms). Some hubs may not have disposed properly.", 
                totalStopwatch.ElapsedMilliseconds);

            // Log detailed status of each hub
            for (int i = 0; i < disposalTasks.Length; i++)
            {
                var task = disposalTasks[i];
                var hubAddress = hubAddresses[i];
                
                if (task.IsCompleted)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        logger.LogDebug("Hub {address} disposal completed successfully", hubAddress);
                    }
                    else if (task.IsFaulted)
                    {
                        logger.LogError("Hub {address} disposal failed with exception: {exception}", 
                            hubAddress, task.Exception?.GetBaseException());
                    }
                    else if (task.IsCanceled)
                    {
                        logger.LogWarning("Hub {address} disposal was canceled", hubAddress);
                    }
                }
                else
                {
                    logger.LogError("Hub {address} disposal did not complete within timeout - HANGING", hubAddress);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during hosted hubs disposal after {elapsed}ms", totalStopwatch.ElapsedMilliseconds);
            
            // Log status of each disposal task
            for (var i = 0; i < disposalTasks.Length; i++)
            {
                var task = disposalTasks[i];
                var hubAddress = hubAddresses[i];
                
                logger.LogError("Hub {address} disposal task status: IsCompleted={isCompleted}, IsFaulted={isFaulted}, IsCanceled={isCanceled}", 
                    hubAddress, task.IsCompleted, task.IsFaulted, task.IsCanceled);
                
                if (task.IsFaulted && task.Exception != null)
                {
                    logger.LogError("Hub {address} disposal exception: {exception}", hubAddress, task.Exception.GetBaseException());
                }
            }
        }
    }
    private Task DisposeHub(IMessageHub hub)
    {
        var address = hub.Address;
        var hubStopwatch = Stopwatch.StartNew();
        logger.LogDebug("Starting disposal of hub {address}", address);
        
        try
        {
            var disposeCallStopwatch = Stopwatch.StartNew();
            logger.LogDebug("Calling Dispose() on hub {address}", address);
            hub.Dispose();
            logger.LogDebug("Dispose() call completed for hub {address} in {elapsed}ms", 
                address, disposeCallStopwatch.ElapsedMilliseconds);

            logger.LogDebug("Hub {address} disposed successfully in {elapsed}ms", address, hubStopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            logger.LogError("Hub {address} disposal was cancelled (total elapsed: {elapsed}ms)", 
                address, hubStopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during disposal of hub {address} after {elapsed}ms", address, hubStopwatch.ElapsedMilliseconds);
            throw;
        }

        return hub.Disposal ?? Task.CompletedTask;
    }

}
