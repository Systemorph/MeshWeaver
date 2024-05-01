using System.Collections.Immutable;

namespace OpenSmc.Application.SignalR;

public class GroupsSubscriptions<TIdentity>
{
    private readonly AsyncLock @lock = new();

    private readonly Dictionary<TIdentity, GroupSubscription> groupSubscriptions = new();
    private readonly Dictionary<string, HashSet<TIdentity>> connectionIdToGroupIds = new();

    internal async ValueTask SubscribeAsync(string connectionId, TIdentity groupId, Func<TIdentity, Func<IReadOnlyCollection<string>>, Task<IAsyncDisposable>> subscribeAsync)
    {
        if (groupSubscriptions.TryGetValue(groupId, out var subscription) && subscription.Connections.Contains(connectionId))
        {
            if (!connectionIdToGroupIds.ContainsKey(connectionId))
            {
                using (await @lock.LockAsync())
                {
                    if (!connectionIdToGroupIds.ContainsKey(connectionId))
                        connectionIdToGroupIds.Add(connectionId, [groupId]);
                }
            }
            return;
        }

        using (await @lock.LockAsync())
        {
            if (!groupSubscriptions.TryGetValue(groupId, out subscription))
                groupSubscriptions.Add(groupId, subscription = new GroupSubscription(groupId));
            if (subscription.Add(connectionId))
                await subscription.SubscribeAsync(subscribeAsync);
            if (!connectionIdToGroupIds.TryGetValue(connectionId, out var connectionGroupIds))
                connectionIdToGroupIds.Add(connectionId, connectionGroupIds = []);
            connectionGroupIds.Add(groupId);
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
