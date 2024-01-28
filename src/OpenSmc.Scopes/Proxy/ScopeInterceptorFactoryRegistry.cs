using System.Collections.Concurrent;

namespace OpenSmc.Scopes.Proxy;

public interface IScopeInterceptorFactoryRegistry
{
    IScopeInterceptorFactoryRegistry Register(IScopeInterceptorFactory factory);
    IReadOnlyCollection<IScopeInterceptorFactory> GetFactories();
}

public class ScopeInterceptorFactoryRegistry: IScopeInterceptorFactoryRegistry
{
    private readonly ConcurrentBag<IScopeInterceptorFactory> factories = new();

    public IScopeInterceptorFactoryRegistry Register(IScopeInterceptorFactory factory)
    {
        factories.Add(factory);
        return this;
    }

    public IReadOnlyCollection<IScopeInterceptorFactory> GetFactories()
    {
        return factories.ToArray();
    }
}
