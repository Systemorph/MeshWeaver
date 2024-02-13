using System.Collections.Concurrent;
using System.Collections.Immutable;
using OpenSmc.Scopes.Operations;

namespace OpenSmc.Scopes.Proxy;

public interface IScopeInterceptorFactoryRegistry
{
    IScopeInterceptorFactoryRegistry Register(IScopeInterceptorFactory factory);
    IReadOnlyCollection<IScopeInterceptorFactory> GetFactories();
}

public class ScopeInterceptorFactoryRegistry: IScopeInterceptorFactoryRegistry
{
    private readonly ImmutableList<IScopeInterceptorFactory> factories = ImmutableList<IScopeInterceptorFactory>.Empty
        .AddRange(new IScopeInterceptorFactory[]
        {
            new ScopeRegistryInterceptorFactory(),
            new CachingInterceptorFactory(),
            new ApplicabilityInterceptorFactory(),
            new FilterableScopeInterceptorFactory(),
            new DelegateToInterfaceDefaultImplementationInterceptorFactory(),
        });

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
