using System.Collections.Immutable;

namespace MeshWeaver.Application.SignalR;

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

    internal async Task UnsubscribeAllAsync(string connectionId)
    {
        using (await @lock.LockAsync())
        {
            if (connectionIdToGroupIds.TryGetValue(connectionId, out var connectionGroupIds))
            {
                foreach (var groupId in connectionGroupIds)
                {
                    if (!groupSubscriptions.TryGetValue(groupId, out var subscription))
                        continue;
                    if (await subscription.TryToUnsubscribeAsync(connectionId))
                    {
                        groupSubscriptions.Remove(groupId);
                    }
                }
                connectionIdToGroupIds.Remove(connectionId);
            }
            // TODO V10: we might try to do the hard way through all the existed Groups in the case connectionId is not present in the reverse map (2024/04/26, Dmitry Kalabin)
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

        internal async ValueTask<bool> TryToUnsubscribeAsync(string connectionId)
        {
            connections = connections.Remove(connectionId);
            if (connections.Count > 0)
                return false;

            if (Disposable != null)
            {
                await Disposable.DisposeAsync();
                Disposable = null;
            }
            return true;
        }
    }
}
