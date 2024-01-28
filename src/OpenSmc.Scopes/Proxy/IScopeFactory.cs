namespace OpenSmc.Scopes.Proxy
{
    public interface IScopeFactory
    {
        ScopeBuilder<TIdentity, TStorage> ForIdentities<TIdentity, TStorage>(IEnumerable<TIdentity> identities, TStorage storage);
        ScopeBuilderWithIdentity<TIdentity> ForIdentities<TIdentity>(IEnumerable<TIdentity> identities);
        ScopeBuilderWithIdentity<TIdentity> ForIdentities<TIdentity>(params TIdentity[] identities);
        ScopeBuilderSingleton<TStorage> ForStorage<TStorage>(TStorage storage);
        ScopeBuilderSingleton ForSingleton();
    }
}