using System.Collections.Concurrent;
using System.Collections.Immutable;
using MeshWeaver.Scopes.DataCubes;
using MeshWeaver.Scopes.Operations;

namespace MeshWeaver.Scopes.Proxy;

public interface IScopeInterceptorFactoryRegistry
{
    IScopeInterceptorFactoryRegistry RegisterBefore<T>(IScopeInterceptorFactory factory);
    IReadOnlyCollection<IScopeInterceptorFactory> GetFactories();
}

public class ScopeInterceptorFactoryRegistry : IScopeInterceptorFactoryRegistry
{
    private ImmutableList<IScopeInterceptorFactory> factories =
        ImmutableList<IScopeInterceptorFactory>.Empty.AddRange(
            new IScopeInterceptorFactory[]
            {
                new DataCubeScopeInterceptorFactory(),
                new ScopeRegistryInterceptorFactory(),
                new CachingInterceptorFactory(),
                new ApplicabilityInterceptorFactory(),
                new FilterableScopeInterceptorFactory(),
                new DelegateToInterfaceDefaultImplementationInterceptorFactory(),
            }
        );

    public IScopeInterceptorFactoryRegistry RegisterBefore<T>(IScopeInterceptorFactory factory)
    {
        var index = factories.FindIndex(x => x is T);
        factories = factories.Insert(index, factory);
        return this;
    }

    public IReadOnlyCollection<IScopeInterceptorFactory> GetFactories()
    {
        return factories;
    }
}
