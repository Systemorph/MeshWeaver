using System.Collections.Concurrent;
using OpenSmc.Messaging;

namespace OpenSmc.Application.Scope;

public class ScopeSynchronizationsCache
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> scopeSynchronizations = new();
    private readonly IMessageHub hub;

    public ScopeSynchronizationsCache(IMessageHub hub)
    {
        this.hub = hub;
    }

    public void StopSynchronization(IInternalMutableScope ms, string id)
    {
        if (!scopeSynchronizations.TryGetValue(ms.GetGuid(), out var hs))
            return;
        hs.Remove(id, out _);
        if (hs.Count != 0)
            return;
        scopeSynchronizations.Remove(ms.GetGuid(), out _);
        hub.Post(new UnsubscribeScopeRequest(ms));
    }

    public void StopSynchronization(IEnumerable<IInternalMutableScope> scopes, string id)
    {
        foreach (var scope in scopes)
            StopSynchronization(scope, id);
    }


    public void Synchronize(IEnumerable<IInternalMutableScope> scopes, string id)
    {
        foreach (var scope in scopes)
            Synchronize(scope, id);
    }
    public void Synchronize(IInternalMutableScope ms, string id)
    {
        if (!scopeSynchronizations.TryGetValue(ms.GetGuid(), out var hs))
        {
            // TODO V10: think if we need callbacks (2023-10-02, Andrei Sirotenko)
            hub.Post(new SubscribeScopeRequest(ms));
            scopeSynchronizations.AddOrUpdate(ms.GetGuid(),
                                          _ => new ConcurrentDictionary<string, byte>(new[] { new KeyValuePair<string, byte>(id, default) }),
                                          (_, x) =>
                                          {
                                              x.AddOrUpdate(id, _ => default, (_, _) => default);
                                              return x;
                                          });
        }
        else
        {
            hs.AddOrUpdate(id, _ => default, (_, _) => default);
        }
    }
}