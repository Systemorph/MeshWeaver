using System.Collections.Concurrent;

namespace OpenSmc.Messaging.Hub;

public class HostedHubsCollection : IAsyncDisposable
{
    public IEnumerable<IMessageHub> Hubs => messageHubs.Values;

    private readonly ConcurrentDictionary<object, IMessageHub> messageHubs = new();

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
