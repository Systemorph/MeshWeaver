namespace OpenSmc.Application.SignalR;

public class GroupsSubscriptions<TIdentity>
{
    private readonly AsyncLock @lock = new();

    internal async ValueTask SubscribeAsync(string connectionId, TIdentity groupId)
    {
        using (await @lock.LockAsync()) // TODO V10: think about reducing locking for the cases when there is nothing to do (2024/04/26, Dmitry Kalabin)
        {
        }
    }
}
