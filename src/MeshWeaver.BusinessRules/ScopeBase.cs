namespace MeshWeaver.BusinessRules;

public class ScopeBase<TScope, TIdentity, TState>(
    TIdentity identity, 
    ScopeRegistry<TState> registry
    ) : IScope<TIdentity, TState>
where TScope : IScope<TIdentity, TState>
{
    public TIdentity Identity => identity;
    public TState GetStorage() => registry.State;
    public TRelatedScope GetScope<TRelatedScope>(object id) where TRelatedScope : IScope => registry.GetScope<TRelatedScope>(id);
    public Type ScopeType => typeof(TScope);
}
