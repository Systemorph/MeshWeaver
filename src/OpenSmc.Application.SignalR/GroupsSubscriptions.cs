namespace OpenSmc.Application.SignalR;

public class GroupsSubscriptions<TIdentity>
{
    private readonly AsyncLock @lock = new();
}
