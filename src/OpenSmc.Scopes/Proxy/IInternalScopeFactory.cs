namespace OpenSmc.Scopes.Proxy
{
    public interface IInternalScopeFactory
    {

        public IEnumerable<object> GetOrCreate(Type tScope, IEnumerable<object> identity, object storage, string context = null, Delegate factory = null);

        public IEnumerable<object> CreateScopes(Type tScope, IEnumerable<object> identities, object storage,
                                                IEnumerable<IScopeInterceptor> additionalInterceptors, string context, bool register);

        public IScopeRegistry ScopeRegistry { get; }

        public ILogger Logger { get; }
    }
}