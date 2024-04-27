namespace OpenSmc.Application.SignalR;

public class GroupsSubscriptions<TIdentity>
{
    private readonly AsyncLock @lock = new();

    private readonly Dictionary<TIdentity, GroupSubscription> groupSubscriptions = new();

    internal async ValueTask SubscribeAsync(string connectionId, TIdentity groupId)
    {
        using (await @lock.LockAsync()) // TODO V10: think about reducing locking for the cases when there is nothing to do (2024/04/26, Dmitry Kalabin)
        {
            if (!groupSubscriptions.TryGetValue(groupId, out subscription))
                groupSubscriptions.Add(groupId, subscription = new GroupSubscription(groupId));
        }
    }

    private class GroupSubscription(TIdentity groupId)
    {
    }
}
