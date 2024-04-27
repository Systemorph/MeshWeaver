using System.Collections.Immutable;

namespace OpenSmc.Application.SignalR;

public class GroupsSubscriptions<TIdentity>
{
    private readonly AsyncLock @lock = new();

    private readonly Dictionary<TIdentity, GroupSubscription> groupSubscriptions = new();

    internal async ValueTask SubscribeAsync(string connectionId, TIdentity groupId, Func<TIdentity, Func<IReadOnlyCollection<string>>, Task<IAsyncDisposable>> subscribeAsync)
    {
        using (await @lock.LockAsync()) // TODO V10: think about reducing locking for the cases when there is nothing to do (2024/04/26, Dmitry Kalabin)
        {
            if (!groupSubscriptions.TryGetValue(groupId, out subscription))
                groupSubscriptions.Add(groupId, subscription = new GroupSubscription(groupId));
            if (subscription.Add(connectionId))
                await subscription.SubscribeAsync(subscribeAsync);
        }
    }

    private class GroupSubscription(TIdentity groupId)
    {
        private ImmutableHashSet<string> connections = [];
        internal IReadOnlySet<string> Connections => connections;
        private IAsyncDisposable Disposable;

        internal bool Add(string connectionId)
        {
            var wasEmpty = connections.Count == 0;
            connections = connections.Add(connectionId);
            return wasEmpty;
        }

        internal async Task SubscribeAsync(Func<TIdentity, Func<IReadOnlyCollection<string>>, Task<IAsyncDisposable>> subscribeAsync)
        {
            Disposable = await subscribeAsync(groupId, () => Connections);
        }
    }
}
