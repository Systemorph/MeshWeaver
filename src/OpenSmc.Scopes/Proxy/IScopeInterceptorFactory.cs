namespace OpenSmc.Scopes.Proxy
{
    public interface IScopeInterceptorFactory
    {
        IEnumerable<IScopeInterceptor> GetInterceptors(Type tScope, IInternalScopeFactory scopeFactory);
    }
}