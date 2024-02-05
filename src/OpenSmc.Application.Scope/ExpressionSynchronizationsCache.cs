using System.Collections.Concurrent;
using OpenSmc.Scopes.Synchronization;

namespace OpenSmc.Application.Scope;

public class ExpressionSynchronizationsCache
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, IInternalMutableScope>> scopeSynchronizations = new();
    //public EventHandler<ScopePropertyChangedEvent> ScopePropertyChanged;
    public void StopSynchronization(IInternalMutableScope ms, string id)
    {
        if (!scopeSynchronizations.TryGetValue(ms.GetGuid(), out var hs))
            return;
        hs.Remove(id, out _);
        if (hs.Count != 0)
            return;
        scopeSynchronizations.Remove(ms.GetGuid(), out var item);

        //ms.ScopePropertyChanged -= OnScopePropertyChanged;
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
            //ms.ScopePropertyChanged += OnScopePropertyChanged;
            scopeSynchronizations.AddOrUpdate(ms.GetGuid(),
                                          _ => new ConcurrentDictionary<string, IInternalMutableScope>(new[] { new KeyValuePair<string, IInternalMutableScope>(id, ms) }),
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

    //private void OnScopePropertyChanged(object sender, ScopePropertyChangedEvent e)
    //{
    //    if(ScopePropertyChanged != null)
    //        ScopePropertyChanged.Invoke(sender, e);
    //}
}