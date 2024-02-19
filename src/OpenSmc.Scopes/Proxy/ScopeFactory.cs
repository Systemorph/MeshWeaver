using Microsoft.Extensions.DependencyInjection;

namespace OpenSmc.Scopes.Proxy
{
    //Scopes.ForIdentities() ==> scopeFactory.ForIdentities()

    public class ScopeFactory(IServiceProvider serviceProvider) : IScopeFactory
    {
        public ScopeBuilder<TIdentity, TStorage> ForIdentities<TIdentity, TStorage>(IEnumerable<TIdentity> identities, TStorage storage)
        {
            return new(identities.Cast<object>(), storage, GetInternalScopeFactory());
        }

        public ScopeBuilderWithIdentity<TIdentity> ForIdentities<TIdentity>(IEnumerable<TIdentity> identities)
        {
            var collection = identities as ICollection<TIdentity> ?? identities.ToArray();
            return new(collection.Cast<object>(), GetInternalScopeFactory());
        }

        public ScopeBuilderWithIdentity<TIdentity> ForIdentities<TIdentity>(params TIdentity[] identities)
        {
            return new(identities.Cast<object>(), GetInternalScopeFactory());
        }

        public ScopeBuilderSingleton<TStorage> ForStorage<TStorage>(TStorage storage)
        {
            return new(storage, GetInternalScopeFactory());
        }

        public ScopeBuilderSingleton ForSingleton()
        {
            return new(GetInternalScopeFactory());
        }


        private IInternalScopeFactory GetInternalScopeFactory()
        {
            return serviceProvider.GetRequiredService<IInternalScopeFactory>();
        }
    }
}